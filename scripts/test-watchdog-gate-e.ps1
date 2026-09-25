param(
    [ValidateRange(90, 300)]
    [int]$FailsafeDelaySeconds = 120,

    [ValidateRange(20, 60)]
    [int]$IsolatedRestartDelaySeconds = 25
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdog'
$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath = Join-Path $root 'gate-e.ready'
$resultPath = Join-Path $root 'gate-e.result'
$localRestorePath = Join-Path $root 'gate-e.local-restore'
$failsafeLog = Join-Path $root 'watchdog-gate-e-failsafe.log'
$failsafeScript = Join-Path $PSScriptRoot 'watchdog-gate-b-failsafe.ps1'

$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogService'
$serviceStatusPath = Join-Path $serviceRoot 'state\gate-d.status.json'
$journalPath = Join-Path $serviceRoot 'state\lease.json'
$serviceLog = Join-Path $serviceRoot ("logs\watchdog-gate-d-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$app = Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'

$proc = $null
$failsafe = $null
$readyReached = $false
$localRestoreProven = $false
$pass = $false
$failure = $null
$servicePidBefore = 0
$servicePidAfter = 0
$productionServiceReinstalled = $false

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This Gate E hardware test must be run from an elevated PowerShell.'
    }
}

function Read-EcState {
    param(
        [ValidateRange(1, 5)]
        [int]$Attempts = 3,

        [ValidateRange(100, 5000)]
        [int]$RetryDelayMs = 750
    )

    $lastFailure = $null

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $output = (& dotnet $cli --probe-88f8-ec-state 2>&1 | Out-String)
            $line = ($output -split "[\r\n]+" |
                Where-Object { $_ -match '^level CPU=' } |
                Select-Object -Last 1)

            if (-not $line) {
                throw "Could not parse EC state. Raw output: $output"
            }

            $match = [regex]::Match(
                $line,
                'level CPU=(\d+) GPU=(\d+).*manual=0x([0-9A-Fa-f]{2}) countdown=(\d+).*RPM CPU=(\d+) GPU=(\d+)')

            if (-not $match.Success) {
                throw "Could not parse EC state line: $line"
            }

            return [pscustomobject]@{
                Cpu = [int]$match.Groups[1].Value
                Gpu = [int]$match.Groups[2].Value
                Manual = $match.Groups[3].Value.ToUpperInvariant()
                Countdown = [int]$match.Groups[4].Value
                CpuRpm = [int]$match.Groups[5].Value
                GpuRpm = [int]$match.Groups[6].Value
                Raw = $line
            }
        }
        catch {
            $lastFailure = $_.Exception

            if ($attempt -ge $Attempts) {
                break
            }

            Write-Warning (
                "Read-only EC probe attempt {0}/{1} failed: {2} Retrying in {3} ms. No fan-control write was issued." -f
                $attempt,
                $Attempts,
                $lastFailure.Message,
                $RetryDelayMs)

            Start-Sleep -Milliseconds $RetryDelayMs
        }
    }

    throw "Read-only EC probe failed after $Attempts process attempt(s): $($lastFailure.Message)"
}

function Wait-ForFile {
    param(
        [string]$Path,
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Path $Path) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }

    return (Test-Path $Path)
}

function Wait-ForJournalGone {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Test-Path $journalPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }

    return (-not (Test-Path $journalPath))
}

function Get-ServiceProcessId {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $svc) {
        return 0
    }

    return [int]$svc.ProcessId
}

function Test-JournalOwnedPhase {
    param($Phase)

    if ($null -eq $Phase) {
        return $false
    }

    if ($Phase -is [string]) {
        return ($Phase -ceq 'Owned' -or $Phase -ceq '2')
    }

    try {
        return ([int]$Phase -eq 2)
    }
    catch {
        return $false
    }
}

function Set-IsolatedGateERecovery {
    $delayMs = $IsolatedRestartDelaySeconds * 1000

    & sc.exe failure $serviceName reset= 86400 actions= "restart/$delayMs/restart/$delayMs/restart/$delayMs" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not configure the isolated Gate E SCM restart delay; sc.exe exit=$LASTEXITCODE."
    }

    & sc.exe failureflag $serviceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enable non-crash failure actions for Gate E; sc.exe exit=$LASTEXITCODE."
    }
}

function Wait-ForFreshServiceStatus {
    param(
        [int]$ExpectedPid,
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $serviceStatusPath) {
            try {
                $status = Get-Content $serviceStatusPath -Raw | ConvertFrom-Json
                if ([int]$status.ProcessId -eq $ExpectedPid) {
                    return $status
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 200
    }

    return $null
}

function Wait-ForProductionWatchdogReady {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)
    $lastObservedPid = 0

    while ((Get-Date) -lt $deadline) {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        $currentPid = if ($svc -and $svc.State -eq 'Running') {
            [int]$svc.ProcessId
        } else {
            0
        }

        if ($currentPid -gt 0 -and $currentPid -ne $lastObservedPid) {
            if ($lastObservedPid -gt 0) {
                Write-Warning "Production watchdog PID changed during bounded startup recovery: $lastObservedPid -> $currentPid. Waiting for the replacement process to publish Ready."
            } else {
                Write-Host "Production watchdog observed PID=$currentPid; waiting for a matching Ready status..."
            }

            $lastObservedPid = $currentPid
        }

        if ($currentPid -gt 0 -and (Test-Path $serviceStatusPath)) {
            try {
                $status = Get-Content $serviceStatusPath -Raw | ConvertFrom-Json

                if ([int]$status.ProcessId -eq $currentPid) {
                    if ($status.Blocked) {
                        throw "Production watchdog PID $currentPid published Blocked status: $($status.RecoveryDisposition) - $($status.Detail)"
                    }

                    if ($status.Ready -and
                        [int]$status.SessionId -eq 0 -and
                        $status.AccountName -match 'SYSTEM

Assert-Administrator

Write-Host 'VictusFanControl - WATCHDOG GATE E (WATCHDOG PROCESS DEATH)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test kills the LocalSystem watchdog process while the GUI owns real 30/30.' -ForegroundColor Yellow
Write-Host 'The parent PowerShell NEVER invokes --restore-hp-auto.' -ForegroundColor Yellow
Write-Host "SCM restart is temporarily delayed to $IsolatedRestartDelaySeconds s so the live GUI local-restore path can be proven before the service returns." -ForegroundColor Yellow
Write-Host 'An independent delayed emergency fallback is armed before 30/30.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and any normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        Write-Error "Refusing Gate E while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: build + synthetic regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-c-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: read-only live hardware preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The telemetry probe has just completed its own EC session. Give Windows/ACPI
# a short quiet handoff before opening the independent full-state verifier.
Start-Sleep -Milliseconds 750
$baseline = Read-EcState
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    Write-Error "Gate E requires firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu)."
    exit 2
}

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Step 3: install watchdog and isolate the first SCM restart window...' -ForegroundColor Cyan

try {
    & (Join-Path $PSScriptRoot 'install-watchdog-gate-d.ps1')
    Set-IsolatedGateERecovery

    Start-Service -Name $serviceName
    $service = Get-Service -Name $serviceName
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))

    $servicePidBefore = Get-ServiceProcessId
    if ($servicePidBefore -le 0) {
        throw 'Could not resolve the running watchdog service PID.'
    }

    $status = Wait-ForFreshServiceStatus -ExpectedPid $servicePidBefore -Seconds 15
    if (-not $status) {
        throw 'Gate E watchdog did not publish a fresh startup status.'
    }

    Write-Host "Service Ready       : $($status.Ready)"
    Write-Host "Service Blocked     : $($status.Blocked)"
    Write-Host "Service Session     : $($status.SessionId)"
    Write-Host "Service Account     : $($status.AccountName)"
    Write-Host "Startup recovery    : $($status.RecoveryDisposition)"
    Write-Host "Service PID         : $servicePidBefore"
    Write-Host "Isolated restart    : $IsolatedRestartDelaySeconds s"

    if (-not $status.Ready -or
        $status.Blocked -or
        [int]$status.SessionId -ne 0 -or
        $status.AccountName -notmatch 'SYSTEM$') {
        throw 'Gate E service is not Ready under LocalSystem/Session 0.'
    }
}
catch {
    $setupFailure = $_.Exception.Message
    Write-Warning "Gate E service setup failed after recovery-policy isolation: $setupFailure"

    try {
        Restore-ProductionWatchdogService
    }
    catch {
        Write-Warning "Automatic production watchdog reinstall also failed: $($_.Exception.Message)"
    }

    throw $setupFailure
}

Write-Host ''
Write-Host 'Gate E will now acquire OWNED 30/30, kill ONLY the watchdog service process, and require the still-live GUI to restore locally before SCM restarts it.' -ForegroundColor Yellow
$confirm = Read-Host 'Type GATEE30 to continue'
if ($confirm -cne 'GATEE30') {
    Write-Host 'Cancelled before any fan write.'
    Restore-ProductionWatchdogService
    exit 1
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Remove-Item $readyPath, $resultPath, $localRestorePath, $failsafeLog -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Step 4: arm independent delayed emergency fallback BEFORE 30/30...' -ForegroundColor Cyan
$failsafe = Start-Process powershell.exe -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $failsafeScript,
    '-Cli', $cli,
    '-RepoRoot', $repoRoot,
    '-LogPath', $failsafeLog,
    '-DelaySeconds', $FailsafeDelaySeconds
) -WindowStyle Hidden -PassThru

Write-Host "Emergency fallback PID: $($failsafe.Id)"
Write-Host "Emergency delay       : $FailsafeDelaySeconds s"

try {
    Write-Host ''
    Write-Host 'Step 5: launch Gate E GUI and require durable OWNED 30/30...' -ForegroundColor Cyan

    $proc = Start-Process -FilePath $app -ArgumentList @(
        '--gate-e-watchdog-death-test',
        '--gate-e-test-token',
        '88F8-GATEE30'
    ) -PassThru

    $deadline = (Get-Date).AddSeconds(75)
    while (-not (Test-Path $readyPath)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $resultPath) {
                Get-Content $resultPath -Raw
            } else {
                'no Gate E result marker'
            }

            throw "VictusFanControl.App exited before Gate E READY. ExitCode=$($proc.ExitCode). $detail"
        }

        if ((Get-Date) -gt $deadline) {
            throw 'Timed out waiting for watchdog-protected Gate E READY.'
        }

        Start-Sleep -Milliseconds 250
    }

    $readyReached = $true
    Write-Host 'Gate E READY marker:' -ForegroundColor Green
    Get-Content $readyPath

    if (-not (Test-Path $journalPath)) {
        throw 'GUI reported Gate E READY but the durable watchdog journal is missing.'
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $journalOwned = Test-JournalOwnedPhase -Phase $journal.Phase
    $procStartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks
    $phaseDisplay = if ($journalOwned) { "$($journal.Phase) (Owned)" } else { "$($journal.Phase)" }

    Write-Host "Journal phase        : $phaseDisplay"
    Write-Host "Journal generation   : $($journal.Generation)"
    Write-Host "Journal controller   : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "GUI identity         : PID=$($proc.Id) startTicks=$procStartTicks"
    Write-Host "Journal owned target : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

    if (-not $journalOwned -or
        [int]$journal.Controller.ProcessId -ne $proc.Id -or
        [long]$journal.Controller.ProcessStartUtcTicks -ne $procStartTicks -or
        [int]$journal.Owned.Cpu -ne 30 -or
        [int]$journal.Owned.Gpu -ne 30) {
        throw 'Gate E READY is not backed by durable OWNED 30/30 bound to the exact GUI PID + creation time.'
    }

    # Keep the Custom-time out-of-band full-state probe single-shot. Production
    # ownership/feedback is already continuously verified by the backend, and
    # repeated external EC snapshots here would add avoidable contention.
    $ownedEc = Read-EcState -Attempts 1
    Write-Host "Pre-kill EC          : $($ownedEc.Raw)"

    if ($ownedEc.Cpu -ne 30 -or $ownedEc.Gpu -ne 30) {
        throw "Gate E durable OWNED journal exists but EC is not 30/30: $($ownedEc.Cpu)/$($ownedEc.Gpu)."
    }

    Write-Host ''
    Write-Host "Step 6: FORCE-KILL watchdog service PID $servicePidBefore. GUI remains alive..." -ForegroundColor Yellow
    Stop-Process -Id $servicePidBefore -Force

    $killDeadline = (Get-Date).AddSeconds(5)
    while ((Get-Process -Id $servicePidBefore -ErrorAction SilentlyContinue) -and
           (Get-Date) -lt $killDeadline) {
        Start-Sleep -Milliseconds 50
    }

    if (Get-Process -Id $servicePidBefore -ErrorAction SilentlyContinue) {
        throw 'Watchdog service process did not terminate after force-kill.'
    }

    Write-Host 'Watchdog process is dead. Parent shell will NOT send any HP restore command.' -ForegroundColor Yellow

    Write-Host ''
    Write-Host 'Step 7: prove live-GUI local restore BEFORE SCM restart...' -ForegroundColor Cyan

    # Causality is determined by the actual service PID, not by assuming SCM
    # restarts at an exact wall-clock instant. Allow a small scheduling margin,
    # but fail immediately if a replacement watchdog PID appears first.
    $localDeadline = (Get-Date).AddSeconds($IsolatedRestartDelaySeconds + 5)

    while (-not (Test-Path $localRestorePath)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $resultPath) {
                Get-Content $resultPath -Raw
            } else {
                'no Gate E result marker'
            }

            throw "Gate E GUI exited before local restore proof. ExitCode=$($proc.ExitCode). $detail"
        }

        $currentServicePid = Get-ServiceProcessId
        if ($currentServicePid -gt 0 -and $currentServicePid -ne $servicePidBefore) {
            throw "SCM restarted watchdog PID $currentServicePid before the GUI local-restore marker; Gate E local fallback was not isolated."
        }

        if ((Get-Date) -gt $localDeadline) {
            throw 'GUI did not publish local restore within the isolated watchdog-death window.'
        }

        Start-Sleep -Milliseconds 100
    }

    Write-Host 'GUI local-restore marker:' -ForegroundColor Green
    $localRestoreMarker = (Get-Content $localRestorePath -Raw).Trim()
    Write-Host $localRestoreMarker

    if ($localRestoreMarker -notmatch '^LOCAL-RESTORE\|.+\|authority=Firmware\|reason=Backend health/ownership probe failed during custom authority:') {
        throw "Gate E local-restore marker does not prove watchdog-loss Firmware handoff: $localRestoreMarker"
    }

    if (Get-Process -Id $servicePidBefore -ErrorAction SilentlyContinue) {
        throw 'Old watchdog PID is unexpectedly alive during local-restore proof.'
    }

    $servicePidDuringLocalProof = Get-ServiceProcessId
    if ($servicePidDuringLocalProof -gt 0 -and
        $servicePidDuringLocalProof -ne $servicePidBefore) {
        throw "A restarted watchdog PID $servicePidDuringLocalProof already exists; cannot attribute FF/FF solely to the live GUI."
    }

    $localEc = Read-EcState
    Write-Host "Local-restore EC     : $($localEc.Raw)"

    if ($localEc.Cpu -ne 255 -or $localEc.Gpu -ne 255) {
        throw "GUI reported local restore but EC is not FF/FF: $($localEc.Cpu)/$($localEc.Gpu)."
    }

    # Re-check after the independent EC read as well. This closes the narrow
    # attribution race where SCM could restart the service during the probe.
    $servicePidAfterLocalEc = Get-ServiceProcessId
    if ($servicePidAfterLocalEc -gt 0 -and
        $servicePidAfterLocalEc -ne $servicePidBefore) {
        throw "SCM restarted watchdog PID $servicePidAfterLocalEc during the local EC verification; cannot attribute FF/FF solely to the live GUI."
    }

    if (-not (Test-Path $journalPath)) {
        throw 'Durable journal disappeared while the watchdog service was still dead; Gate E expected the dead service to leave recovery evidence for SCM restart.'
    }

    $localRestoreProven = $true

    Write-Host ''
    Write-Host 'Step 8: wait for SCM to restart watchdog and recover the retained journal...' -ForegroundColor Cyan

    $restartDeadline = (Get-Date).AddSeconds($IsolatedRestartDelaySeconds + 20)
    while ($servicePidAfter -le 0 -and (Get-Date) -lt $restartDeadline) {
        $candidate = Get-ServiceProcessId
        if ($candidate -gt 0 -and $candidate -ne $servicePidBefore) {
            $servicePidAfter = $candidate
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if ($servicePidAfter -le 0) {
        throw 'SCM did not restart the watchdog service within the bounded Gate E window.'
    }

    Write-Host "Restarted service PID: $servicePidAfter"

    $restartStatus = Wait-ForFreshServiceStatus -ExpectedPid $servicePidAfter -Seconds 15
    if (-not $restartStatus) {
        throw 'Restarted watchdog did not publish a fresh status marker.'
    }

    Write-Host "Restart Ready        : $($restartStatus.Ready)"
    Write-Host "Restart Blocked      : $($restartStatus.Blocked)"
    Write-Host "Restart recovery     : $($restartStatus.RecoveryDisposition)"
    Write-Host "Restart detail       : $($restartStatus.Detail)"

    if (-not $restartStatus.Ready -or
        $restartStatus.Blocked -or
        [int]$restartStatus.SessionId -ne 0 -or
        $restartStatus.AccountName -notmatch 'SYSTEM$') {
        throw 'SCM restarted the watchdog but it did not return Ready under LocalSystem/Session 0.'
    }

    if ($restartStatus.RecoveryDisposition -cne 'RestoredFirmware') {
        throw "Expected startup recovery RestoredFirmware from the retained OWNED journal; observed '$($restartStatus.RecoveryDisposition)'."
    }

    if (-not (Wait-ForJournalGone -Seconds 5)) {
        throw 'Restarted watchdog reported Ready but the retained Gate E journal was not cleared.'
    }

    $final = Read-EcState
    Write-Host "Final EC             : $($final.Raw)"

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        throw "Gate E restart recovery completed but EC is not FF/FF: $($final.Cpu)/$($final.Gpu)."
    }

    $pass = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    # Never issue the HP restore CLI from this parent shell.
    # If Gate E has not yet proven local restore, keep the GUI alive until the
    # service or emergency fallback can safely recover the retained lease.
    if (-not $localRestoreProven -and $readyReached) {
        Write-Warning 'Gate E failed before local-restore proof. Keeping the GUI alive while waiting for watchdog/fallback recovery; do not close PowerShell.'

        $recoveryDeadline = (Get-Date).AddSeconds(
            [Math]::Min($FailsafeDelaySeconds + 10, 140))

        while ((Get-Date) -lt $recoveryDeadline) {
            $safe = $false

            if (-not (Test-Path $journalPath)) {
                try {
                    $state = Read-EcState
                    $safe = $state.Cpu -eq 255 -and $state.Gpu -eq 255
                }
                catch {
                }
            }

            if ($safe) {
                $localRestoreProven = $true
                break
            }

            Start-Sleep -Milliseconds 500
        }
    }

    $firmwareSafe = $false
    try {
        if (-not (Test-Path $journalPath)) {
            $cleanupState = Read-EcState
            Write-Host "Post-test EC check   : $($cleanupState.Raw)"
            $firmwareSafe =
                $cleanupState.Cpu -eq 255 -and
                $cleanupState.Gpu -eq 255
        }
    }
    catch {
        Write-Warning "Could not perform final read-only EC check: $($_.Exception.Message)"
    }

    if ($firmwareSafe -and $proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try {
            $proc.WaitForExit(5000) | Out-Null
        }
        catch {
        }

        Write-Host 'Gate E GUI terminated only after firmware-safe FF/FF was independently proven.' -ForegroundColor Green
    }

    if ($firmwareSafe -and $failsafe -and -not $failsafe.HasExited) {
        Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
        Write-Host 'Emergency fallback cancelled only after FF/FF + cleared journal were independently proven.' -ForegroundColor Green
    }
    elseif ($failsafe -and -not $firmwareSafe) {
        Write-Warning "Firmware safety is not yet independently proven. Emergency fallback PID $($failsafe.Id) remains armed."
    }

    if ($firmwareSafe -and -not $productionServiceReinstalled) {
        try {
            Restore-ProductionWatchdogService
        }
        catch {
            Write-Warning "Could not reinstall the production watchdog policy automatically: $($_.Exception.Message)"
            if ($pass) {
                $pass = $false
                $failure = "Gate E recovery passed, but production watchdog reinstall failed: $($_.Exception.Message)"
            }
        }
    }
}

if (-not $pass) {
    Write-Host ''
    Write-Host 'Gate E did NOT pass.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent watchdog log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 80
    }

    if (Test-Path $resultPath) {
        Write-Host ''
        Write-Host 'Gate E GUI result:' -ForegroundColor Cyan
        Get-Content $resultPath
    }

    exit 111
}

Write-Host ''
Write-Host 'Step 9: verify OMEN Gaming Hub undervolt...' -ForegroundColor Cyan
$post = Read-Host 'Type SAME if the CPU undervolt is unchanged, or CHANGED if it changed'

if ($post -cne 'SAME') {
    if ($post -ceq 'CHANGED') {
        Write-Warning 'Undervolt preservation FAILED/CHANGED.'
        exit 112
    }

    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 113
}

Write-Host ''
Write-Host 'PASS: Gate E proved the live GUI/controller restores HP firmware after watchdog-process death, before SCM restarts the service.' -ForegroundColor Green
Write-Host 'Verified: OWNED 30/30 -> watchdog PID force-kill -> GUI local FF/FF -> retained durable journal -> SCM restart -> startup RestoredFirmware -> journal cleared.' -ForegroundColor Green
Write-Host 'Parent PowerShell issued no HP restore command; emergency fallback did not fire.' -ForegroundColor Green
Write-Host 'Production watchdog installation/recovery policy was reinstalled after the test.' -ForegroundColor Green
Write-Host 'OMEN Gaming Hub undervolt: SAME (user-confirmed).' -ForegroundColor Green
exit 0
) {
                        # Prove this exact Ready process is still the live SCM
                        # instance after a short stability interval. A transient
                        # first-start failure is allowed to exercise the configured
                        # SCM restart policy, but a crash loop is not accepted.
                        Start-Sleep -Milliseconds 500
                        $confirm = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue

                        if ($confirm -and
                            $confirm.State -eq 'Running' -and
                            [int]$confirm.ProcessId -eq $currentPid) {
                            return [pscustomobject]@{
                                Pid = $currentPid
                                Status = $status
                            }
                        }
                    }
                }
            }
            catch {
                if ($_.Exception.Message -like 'Production watchdog PID * published Blocked status:*') {
                    throw
                }

                # Status replacement is atomic but the reader may race the file
                # swap. Ignore only parse/read races and keep the bounded wait.
            }
        }

        Start-Sleep -Milliseconds 200
    }

    return $null
}

function Assert-ProductionRecoveryPolicy {
    $qfailure = (& sc.exe qfailure $serviceName 2>&1 | Out-String)

    if ($LASTEXITCODE -ne 0) {
        throw "Could not query production SCM recovery policy; sc.exe exit=$LASTEXITCODE."
    }

    if ($qfailure -notmatch '(?s)1000\s*ms.*5000\s*ms.*10000\s*ms') {
        throw "Production SCM recovery policy is not the required ordered 1s / 5s / 10s sequence. Raw qfailure output: $qfailure"
    }
}

function Restore-ProductionWatchdogService {
    Write-Host ''
    Write-Host 'Restoring production watchdog installation/recovery policy (1s / 5s / 10s)...' -ForegroundColor Cyan

    & (Join-Path $PSScriptRoot 'install-watchdog-gate-d.ps1')

    Start-Service -Name $serviceName

    # Do not bind housekeeping success to the first PID returned by SCM.
    # Production recovery is intentionally configured to restart a watchdog
    # process that fails during startup. Accept only a bounded eventual Ready
    # process whose status matches the CURRENT SCM PID and remains stable.
    $ready = Wait-ForProductionWatchdogReady -Seconds 35
    if (-not $ready) {
        throw 'Production watchdog reinstall did not reach a stable Ready LocalSystem/Session 0 process within 35 s.'
    }

    if (Test-Path $journalPath) {
        throw 'Production watchdog returned Ready but a durable lease journal is still present.'
    }

    Assert-ProductionRecoveryPolicy

    $script:productionServiceReinstalled = $true
    Write-Host "Production watchdog Ready. PID=$($ready.Pid) recovery=$($ready.Status.RecoveryDisposition)" -ForegroundColor Green
    Write-Host 'Production SCM recovery verified: 1s / 5s / 10s.' -ForegroundColor Green
}

Assert-Administrator

Write-Host 'VictusFanControl - WATCHDOG GATE E (WATCHDOG PROCESS DEATH)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test kills the LocalSystem watchdog process while the GUI owns real 30/30.' -ForegroundColor Yellow
Write-Host 'The parent PowerShell NEVER invokes --restore-hp-auto.' -ForegroundColor Yellow
Write-Host "SCM restart is temporarily delayed to $IsolatedRestartDelaySeconds s so the live GUI local-restore path can be proven before the service returns." -ForegroundColor Yellow
Write-Host 'An independent delayed emergency fallback is armed before 30/30.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and any normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        Write-Error "Refusing Gate E while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: build + synthetic regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --gate-c-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: read-only live hardware preflight...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The telemetry probe has just completed its own EC session. Give Windows/ACPI
# a short quiet handoff before opening the independent full-state verifier.
Start-Sleep -Milliseconds 750
$baseline = Read-EcState
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    Write-Error "Gate E requires firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu)."
    exit 2
}

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Step 3: install watchdog and isolate the first SCM restart window...' -ForegroundColor Cyan

try {
    & (Join-Path $PSScriptRoot 'install-watchdog-gate-d.ps1')
    Set-IsolatedGateERecovery

    Start-Service -Name $serviceName
    $service = Get-Service -Name $serviceName
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))

    $servicePidBefore = Get-ServiceProcessId
    if ($servicePidBefore -le 0) {
        throw 'Could not resolve the running watchdog service PID.'
    }

    $status = Wait-ForFreshServiceStatus -ExpectedPid $servicePidBefore -Seconds 15
    if (-not $status) {
        throw 'Gate E watchdog did not publish a fresh startup status.'
    }

    Write-Host "Service Ready       : $($status.Ready)"
    Write-Host "Service Blocked     : $($status.Blocked)"
    Write-Host "Service Session     : $($status.SessionId)"
    Write-Host "Service Account     : $($status.AccountName)"
    Write-Host "Startup recovery    : $($status.RecoveryDisposition)"
    Write-Host "Service PID         : $servicePidBefore"
    Write-Host "Isolated restart    : $IsolatedRestartDelaySeconds s"

    if (-not $status.Ready -or
        $status.Blocked -or
        [int]$status.SessionId -ne 0 -or
        $status.AccountName -notmatch 'SYSTEM$') {
        throw 'Gate E service is not Ready under LocalSystem/Session 0.'
    }
}
catch {
    $setupFailure = $_.Exception.Message
    Write-Warning "Gate E service setup failed after recovery-policy isolation: $setupFailure"

    try {
        Restore-ProductionWatchdogService
    }
    catch {
        Write-Warning "Automatic production watchdog reinstall also failed: $($_.Exception.Message)"
    }

    throw $setupFailure
}

Write-Host ''
Write-Host 'Gate E will now acquire OWNED 30/30, kill ONLY the watchdog service process, and require the still-live GUI to restore locally before SCM restarts it.' -ForegroundColor Yellow
$confirm = Read-Host 'Type GATEE30 to continue'
if ($confirm -cne 'GATEE30') {
    Write-Host 'Cancelled before any fan write.'
    Restore-ProductionWatchdogService
    exit 1
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Remove-Item $readyPath, $resultPath, $localRestorePath, $failsafeLog -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Step 4: arm independent delayed emergency fallback BEFORE 30/30...' -ForegroundColor Cyan
$failsafe = Start-Process powershell.exe -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $failsafeScript,
    '-Cli', $cli,
    '-RepoRoot', $repoRoot,
    '-LogPath', $failsafeLog,
    '-DelaySeconds', $FailsafeDelaySeconds
) -WindowStyle Hidden -PassThru

Write-Host "Emergency fallback PID: $($failsafe.Id)"
Write-Host "Emergency delay       : $FailsafeDelaySeconds s"

try {
    Write-Host ''
    Write-Host 'Step 5: launch Gate E GUI and require durable OWNED 30/30...' -ForegroundColor Cyan

    $proc = Start-Process -FilePath $app -ArgumentList @(
        '--gate-e-watchdog-death-test',
        '--gate-e-test-token',
        '88F8-GATEE30'
    ) -PassThru

    $deadline = (Get-Date).AddSeconds(75)
    while (-not (Test-Path $readyPath)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $resultPath) {
                Get-Content $resultPath -Raw
            } else {
                'no Gate E result marker'
            }

            throw "VictusFanControl.App exited before Gate E READY. ExitCode=$($proc.ExitCode). $detail"
        }

        if ((Get-Date) -gt $deadline) {
            throw 'Timed out waiting for watchdog-protected Gate E READY.'
        }

        Start-Sleep -Milliseconds 250
    }

    $readyReached = $true
    Write-Host 'Gate E READY marker:' -ForegroundColor Green
    Get-Content $readyPath

    if (-not (Test-Path $journalPath)) {
        throw 'GUI reported Gate E READY but the durable watchdog journal is missing.'
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $journalOwned = Test-JournalOwnedPhase -Phase $journal.Phase
    $procStartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks
    $phaseDisplay = if ($journalOwned) { "$($journal.Phase) (Owned)" } else { "$($journal.Phase)" }

    Write-Host "Journal phase        : $phaseDisplay"
    Write-Host "Journal generation   : $($journal.Generation)"
    Write-Host "Journal controller   : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "GUI identity         : PID=$($proc.Id) startTicks=$procStartTicks"
    Write-Host "Journal owned target : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

    if (-not $journalOwned -or
        [int]$journal.Controller.ProcessId -ne $proc.Id -or
        [long]$journal.Controller.ProcessStartUtcTicks -ne $procStartTicks -or
        [int]$journal.Owned.Cpu -ne 30 -or
        [int]$journal.Owned.Gpu -ne 30) {
        throw 'Gate E READY is not backed by durable OWNED 30/30 bound to the exact GUI PID + creation time.'
    }

    # Keep the Custom-time out-of-band full-state probe single-shot. Production
    # ownership/feedback is already continuously verified by the backend, and
    # repeated external EC snapshots here would add avoidable contention.
    $ownedEc = Read-EcState -Attempts 1
    Write-Host "Pre-kill EC          : $($ownedEc.Raw)"

    if ($ownedEc.Cpu -ne 30 -or $ownedEc.Gpu -ne 30) {
        throw "Gate E durable OWNED journal exists but EC is not 30/30: $($ownedEc.Cpu)/$($ownedEc.Gpu)."
    }

    Write-Host ''
    Write-Host "Step 6: FORCE-KILL watchdog service PID $servicePidBefore. GUI remains alive..." -ForegroundColor Yellow
    Stop-Process -Id $servicePidBefore -Force

    $killDeadline = (Get-Date).AddSeconds(5)
    while ((Get-Process -Id $servicePidBefore -ErrorAction SilentlyContinue) -and
           (Get-Date) -lt $killDeadline) {
        Start-Sleep -Milliseconds 50
    }

    if (Get-Process -Id $servicePidBefore -ErrorAction SilentlyContinue) {
        throw 'Watchdog service process did not terminate after force-kill.'
    }

    Write-Host 'Watchdog process is dead. Parent shell will NOT send any HP restore command.' -ForegroundColor Yellow

    Write-Host ''
    Write-Host 'Step 7: prove live-GUI local restore BEFORE SCM restart...' -ForegroundColor Cyan

    # Causality is determined by the actual service PID, not by assuming SCM
    # restarts at an exact wall-clock instant. Allow a small scheduling margin,
    # but fail immediately if a replacement watchdog PID appears first.
    $localDeadline = (Get-Date).AddSeconds($IsolatedRestartDelaySeconds + 5)

    while (-not (Test-Path $localRestorePath)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $resultPath) {
                Get-Content $resultPath -Raw
            } else {
                'no Gate E result marker'
            }

            throw "Gate E GUI exited before local restore proof. ExitCode=$($proc.ExitCode). $detail"
        }

        $currentServicePid = Get-ServiceProcessId
        if ($currentServicePid -gt 0 -and $currentServicePid -ne $servicePidBefore) {
            throw "SCM restarted watchdog PID $currentServicePid before the GUI local-restore marker; Gate E local fallback was not isolated."
        }

        if ((Get-Date) -gt $localDeadline) {
            throw 'GUI did not publish local restore within the isolated watchdog-death window.'
        }

        Start-Sleep -Milliseconds 100
    }

    Write-Host 'GUI local-restore marker:' -ForegroundColor Green
    $localRestoreMarker = (Get-Content $localRestorePath -Raw).Trim()
    Write-Host $localRestoreMarker

    if ($localRestoreMarker -notmatch '^LOCAL-RESTORE\|.+\|authority=Firmware\|reason=Backend health/ownership probe failed during custom authority:') {
        throw "Gate E local-restore marker does not prove watchdog-loss Firmware handoff: $localRestoreMarker"
    }

    if (Get-Process -Id $servicePidBefore -ErrorAction SilentlyContinue) {
        throw 'Old watchdog PID is unexpectedly alive during local-restore proof.'
    }

    $servicePidDuringLocalProof = Get-ServiceProcessId
    if ($servicePidDuringLocalProof -gt 0 -and
        $servicePidDuringLocalProof -ne $servicePidBefore) {
        throw "A restarted watchdog PID $servicePidDuringLocalProof already exists; cannot attribute FF/FF solely to the live GUI."
    }

    $localEc = Read-EcState
    Write-Host "Local-restore EC     : $($localEc.Raw)"

    if ($localEc.Cpu -ne 255 -or $localEc.Gpu -ne 255) {
        throw "GUI reported local restore but EC is not FF/FF: $($localEc.Cpu)/$($localEc.Gpu)."
    }

    # Re-check after the independent EC read as well. This closes the narrow
    # attribution race where SCM could restart the service during the probe.
    $servicePidAfterLocalEc = Get-ServiceProcessId
    if ($servicePidAfterLocalEc -gt 0 -and
        $servicePidAfterLocalEc -ne $servicePidBefore) {
        throw "SCM restarted watchdog PID $servicePidAfterLocalEc during the local EC verification; cannot attribute FF/FF solely to the live GUI."
    }

    if (-not (Test-Path $journalPath)) {
        throw 'Durable journal disappeared while the watchdog service was still dead; Gate E expected the dead service to leave recovery evidence for SCM restart.'
    }

    $localRestoreProven = $true

    Write-Host ''
    Write-Host 'Step 8: wait for SCM to restart watchdog and recover the retained journal...' -ForegroundColor Cyan

    $restartDeadline = (Get-Date).AddSeconds($IsolatedRestartDelaySeconds + 20)
    while ($servicePidAfter -le 0 -and (Get-Date) -lt $restartDeadline) {
        $candidate = Get-ServiceProcessId
        if ($candidate -gt 0 -and $candidate -ne $servicePidBefore) {
            $servicePidAfter = $candidate
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if ($servicePidAfter -le 0) {
        throw 'SCM did not restart the watchdog service within the bounded Gate E window.'
    }

    Write-Host "Restarted service PID: $servicePidAfter"

    $restartStatus = Wait-ForFreshServiceStatus -ExpectedPid $servicePidAfter -Seconds 15
    if (-not $restartStatus) {
        throw 'Restarted watchdog did not publish a fresh status marker.'
    }

    Write-Host "Restart Ready        : $($restartStatus.Ready)"
    Write-Host "Restart Blocked      : $($restartStatus.Blocked)"
    Write-Host "Restart recovery     : $($restartStatus.RecoveryDisposition)"
    Write-Host "Restart detail       : $($restartStatus.Detail)"

    if (-not $restartStatus.Ready -or
        $restartStatus.Blocked -or
        [int]$restartStatus.SessionId -ne 0 -or
        $restartStatus.AccountName -notmatch 'SYSTEM$') {
        throw 'SCM restarted the watchdog but it did not return Ready under LocalSystem/Session 0.'
    }

    if ($restartStatus.RecoveryDisposition -cne 'RestoredFirmware') {
        throw "Expected startup recovery RestoredFirmware from the retained OWNED journal; observed '$($restartStatus.RecoveryDisposition)'."
    }

    if (-not (Wait-ForJournalGone -Seconds 5)) {
        throw 'Restarted watchdog reported Ready but the retained Gate E journal was not cleared.'
    }

    $final = Read-EcState
    Write-Host "Final EC             : $($final.Raw)"

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        throw "Gate E restart recovery completed but EC is not FF/FF: $($final.Cpu)/$($final.Gpu)."
    }

    $pass = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    # Never issue the HP restore CLI from this parent shell.
    # If Gate E has not yet proven local restore, keep the GUI alive until the
    # service or emergency fallback can safely recover the retained lease.
    if (-not $localRestoreProven -and $readyReached) {
        Write-Warning 'Gate E failed before local-restore proof. Keeping the GUI alive while waiting for watchdog/fallback recovery; do not close PowerShell.'

        $recoveryDeadline = (Get-Date).AddSeconds(
            [Math]::Min($FailsafeDelaySeconds + 10, 140))

        while ((Get-Date) -lt $recoveryDeadline) {
            $safe = $false

            if (-not (Test-Path $journalPath)) {
                try {
                    $state = Read-EcState
                    $safe = $state.Cpu -eq 255 -and $state.Gpu -eq 255
                }
                catch {
                }
            }

            if ($safe) {
                $localRestoreProven = $true
                break
            }

            Start-Sleep -Milliseconds 500
        }
    }

    $firmwareSafe = $false
    try {
        if (-not (Test-Path $journalPath)) {
            $cleanupState = Read-EcState
            Write-Host "Post-test EC check   : $($cleanupState.Raw)"
            $firmwareSafe =
                $cleanupState.Cpu -eq 255 -and
                $cleanupState.Gpu -eq 255
        }
    }
    catch {
        Write-Warning "Could not perform final read-only EC check: $($_.Exception.Message)"
    }

    if ($firmwareSafe -and $proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try {
            $proc.WaitForExit(5000) | Out-Null
        }
        catch {
        }

        Write-Host 'Gate E GUI terminated only after firmware-safe FF/FF was independently proven.' -ForegroundColor Green
    }

    if ($firmwareSafe -and $failsafe -and -not $failsafe.HasExited) {
        Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
        Write-Host 'Emergency fallback cancelled only after FF/FF + cleared journal were independently proven.' -ForegroundColor Green
    }
    elseif ($failsafe -and -not $firmwareSafe) {
        Write-Warning "Firmware safety is not yet independently proven. Emergency fallback PID $($failsafe.Id) remains armed."
    }

    if ($firmwareSafe -and -not $productionServiceReinstalled) {
        try {
            Restore-ProductionWatchdogService
        }
        catch {
            Write-Warning "Could not reinstall the production watchdog policy automatically: $($_.Exception.Message)"
            if ($pass) {
                $pass = $false
                $failure = "Gate E recovery passed, but production watchdog reinstall failed: $($_.Exception.Message)"
            }
        }
    }
}

if (-not $pass) {
    Write-Host ''
    Write-Host 'Gate E did NOT pass.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent watchdog log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 80
    }

    if (Test-Path $resultPath) {
        Write-Host ''
        Write-Host 'Gate E GUI result:' -ForegroundColor Cyan
        Get-Content $resultPath
    }

    exit 111
}

Write-Host ''
Write-Host 'Step 9: verify OMEN Gaming Hub undervolt...' -ForegroundColor Cyan
$post = Read-Host 'Type SAME if the CPU undervolt is unchanged, or CHANGED if it changed'

if ($post -cne 'SAME') {
    if ($post -ceq 'CHANGED') {
        Write-Warning 'Undervolt preservation FAILED/CHANGED.'
        exit 112
    }

    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 113
}

Write-Host ''
Write-Host 'PASS: Gate E proved the live GUI/controller restores HP firmware after watchdog-process death, before SCM restarts the service.' -ForegroundColor Green
Write-Host 'Verified: OWNED 30/30 -> watchdog PID force-kill -> GUI local FF/FF -> retained durable journal -> SCM restart -> startup RestoredFirmware -> journal cleared.' -ForegroundColor Green
Write-Host 'Parent PowerShell issued no HP restore command; emergency fallback did not fire.' -ForegroundColor Green
Write-Host 'Production watchdog installation/recovery policy was reinstalled after the test.' -ForegroundColor Green
Write-Host 'OMEN Gaming Hub undervolt: SAME (user-confirmed).' -ForegroundColor Green
exit 0
