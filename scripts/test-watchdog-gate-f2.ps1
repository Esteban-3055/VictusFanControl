param(
    [ValidateRange(90, 300)]
    [int]$FailsafeDelaySeconds = 120,

    [ValidateRange(10, 100)]
    [int]$MaxKillDeltaMs = 50
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdog'
$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath = Join-Path $root 'gate-f2-write-armed.ready'
$resultPath = Join-Path $root 'gate-f2-write-armed.result'
$localRestoreStartedPath = Join-Path $root 'gate-f2.local-restore-started'
$failsafeLog = Join-Path $root 'watchdog-gate-f2-failsafe.log'
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
$doubleKillIssued = $false
$pass = $false
$failure = $null
$servicePidBefore = 0
$serviceStartTicksBefore = 0L
$servicePidAfter = 0
$productionServiceReinstalled = $false
$serviceInstalled = $false

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This Gate F2 hardware test must be run from an elevated PowerShell.'
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

function Get-ServiceProcessId {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $svc) {
        return 0
    }

    return [int]$svc.ProcessId
}

function Get-ProcessStartTicks {
    param([int]$ProcessId)

    $candidate = [System.Diagnostics.Process]::GetProcessById($ProcessId)
    try {
        return [long]$candidate.StartTime.ToUniversalTime().Ticks
    }
    finally {
        $candidate.Dispose()
    }
}

function Test-JournalWriteArmedPhase {
    param($Phase)

    if ($null -eq $Phase) {
        return $false
    }

    if ($Phase -is [string]) {
        return ($Phase -ceq 'WriteArmed' -or $Phase -ceq '1')
    }

    try {
        return ([int]$Phase -eq 1)
    }
    catch {
        return $false
    }
}

function Wait-ForJournalGone {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Test-Path $journalPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }

    return (-not (Test-Path $journalPath))
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

function Wait-ForStableWatchdogReady {
    param(
        [int]$Seconds,
        [string]$RequiredRecovery,
        [int]$ExcludedPid = 0
    )

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
            Write-Host "Watchdog observed PID=$currentPid; waiting for matching Ready status..."
            $lastObservedPid = $currentPid
        }

        if ($currentPid -gt 0 -and
            $currentPid -ne $ExcludedPid -and
            (Test-Path $serviceStatusPath)) {
            try {
                $status = Get-Content $serviceStatusPath -Raw | ConvertFrom-Json

                if ([int]$status.ProcessId -eq $currentPid) {
                    if ($status.Blocked) {
                        throw "Watchdog PID $currentPid published Blocked status: $($status.RecoveryDisposition) - $($status.Detail)"
                    }

                    if ($status.Ready -and
                        [int]$status.SessionId -eq 0 -and
                        $status.AccountName -match 'SYSTEM$') {
                        if ($status.RecoveryDisposition -cne $RequiredRecovery) {
                            throw "Watchdog PID $currentPid reached Ready with recovery '$($status.RecoveryDisposition)', expected '$RequiredRecovery'."
                        }

                        Start-Sleep -Milliseconds 500
                        $confirm = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue

                        if ($confirm -and
                            $confirm.State -eq 'Running' -and
                            [int]$confirm.ProcessId -eq $currentPid) {
                            return [pscustomobject]@{
                                Pid = $currentPid
                                Status = $status
                                StartTicks = Get-ProcessStartTicks -ProcessId $currentPid
                            }
                        }
                    }
                }
            }
            catch {
                if ($_.Exception.Message -like 'Watchdog PID * published Blocked status:*' -or
                    $_.Exception.Message -like 'Watchdog PID * reached Ready with recovery *') {
                    throw
                }
            }
        }

        Start-Sleep -Milliseconds 100
    }

    return $null
}

function Restore-ProductionWatchdogService {
    Write-Host ''
    Write-Host 'Reinstalling production watchdog baseline (1s / 5s / 10s)...' -ForegroundColor Cyan

    & (Join-Path $PSScriptRoot 'install-watchdog-gate-d.ps1')
    $script:serviceInstalled = $true

    Start-Service -Name $serviceName
    $ready = Wait-ForStableWatchdogReady -Seconds 35 -RequiredRecovery 'Ready'

    if (-not $ready) {
        throw 'Production watchdog reinstall did not reach stable Ready LocalSystem/Session 0 within 35 s.'
    }

    if (Test-Path $journalPath) {
        throw 'Production watchdog returned Ready but a durable lease journal is still present.'
    }

    Assert-ProductionRecoveryPolicy

    $script:productionServiceReinstalled = $true
    Write-Host "Production watchdog Ready. PID=$($ready.Pid) recovery=$($ready.Status.RecoveryDisposition)" -ForegroundColor Green
    Write-Host 'Production SCM recovery verified: 1s / 5s / 10s.' -ForegroundColor Green
}

function Wait-ForFirmwareSafe {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path $journalPath)) {
            try {
                $state = Read-EcState
                if ($state.Cpu -eq 255 -and $state.Gpu -eq 255) {
                    return $state
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

Assert-Administrator

Write-Host 'VictusFanControl - WATCHDOG GATE F2 (WRITE_ARMED POST-ACK DOUBLE DEATH)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test reaches durable WRITE_ARMED with real WMI 30/30 + EC/dual-tach ACK, holds immediately before Commit, then force-kills watchdog and GUI back-to-back.' -ForegroundColor Yellow
Write-Host 'The parent PowerShell performs NO HP fan restore. Recovery must come from the SCM-restarted watchdog reading the durable journal.' -ForegroundColor Yellow
Write-Host "The two kill calls must be issued within $MaxKillDeltaMs ms." -ForegroundColor Yellow
Write-Host 'An independent delayed emergency fallback is armed before 30/30; if it reaches its delay, Gate F2 cannot pass.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and any normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        Write-Error "Refusing Gate F2 while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: build + synthetic/invariant regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-gate-e-invariants.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-gate-f-invariants.ps1')
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

Start-Sleep -Milliseconds 750
$baseline = Read-EcState
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    Write-Error "Gate F2 requires firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu)."
    exit 2
}

if (Test-Path $journalPath) {
    Write-Error "Gate F2 refuses to reinstall/start the watchdog while a durable lease journal already exists: $journalPath"
    exit 2
}

Write-Host 'Durable journal      : absent'

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Step 3: install/start the watchdog with the REAL production SCM policy...' -ForegroundColor Cyan

try {
    & (Join-Path $PSScriptRoot 'install-watchdog-gate-d.ps1')
    $serviceInstalled = $true
    Start-Service -Name $serviceName

    $initialReady = Wait-ForStableWatchdogReady -Seconds 35 -RequiredRecovery 'Ready'
    if (-not $initialReady) {
        throw 'Gate F2 watchdog did not reach stable Ready under the production recovery policy.'
    }

    $servicePidBefore = [int]$initialReady.Pid
    $serviceStartTicksBefore = [long]$initialReady.StartTicks

    Assert-ProductionRecoveryPolicy

    Write-Host "Service Ready       : $($initialReady.Status.Ready)"
    Write-Host "Service Blocked     : $($initialReady.Status.Blocked)"
    Write-Host "Service Session     : $($initialReady.Status.SessionId)"
    Write-Host "Service Account     : $($initialReady.Status.AccountName)"
    Write-Host "Startup recovery    : $($initialReady.Status.RecoveryDisposition)"
    Write-Host "Service PID         : $servicePidBefore"
    Write-Host "Service startTicks  : $serviceStartTicksBefore"
    Write-Host 'SCM policy          : production 1 s / 5 s / 10 s'
}
catch {
    $setupFailure = $_.Exception.Message
    Write-Warning "Gate F2 production-service setup failed before any fan write: $setupFailure"

    if ($serviceInstalled) {
        try {
            Restore-ProductionWatchdogService
        }
        catch {
            Write-Warning "Automatic production watchdog baseline restore also failed: $($_.Exception.Message)"
        }
    }

    throw $setupFailure
}

Write-Host ''
Write-Host 'Gate F2 will now enter durable WRITE_ARMED, perform the real 30/30 write + EC/dual-tach ACK, HOLD before Commit, then destroy BOTH original failure domains.' -ForegroundColor Yellow
$confirm = Read-Host 'Type GATEF2-30 to continue'
if ($confirm -cne 'GATEF2-30') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Remove-Item $readyPath, $resultPath, $localRestoreStartedPath, $failsafeLog -Force -ErrorAction SilentlyContinue

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

Start-Sleep -Milliseconds 250
$failsafe.Refresh()
if ($failsafe.HasExited) {
    throw 'Independent emergency fallback exited before Gate F2 could enter Custom; refusing the hardware test.'
}

Write-Host 'Emergency fallback    : armed/alive'

try {
    Write-Host ''
    Write-Host 'Step 5: launch Gate F2 GUI and require WRITE_ARMED 30/30 after real EC+dual-tach ACK but before Commit...' -ForegroundColor Cyan

    $proc = Start-Process -FilePath $app -ArgumentList @(
        '--gate-f2-write-armed-double-death-test',
        '--gate-f2-test-token',
        '88F8-GATEF2-30'
    ) -PassThru

    $deadline = (Get-Date).AddSeconds(75)
    while (-not (Test-Path $readyPath)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $resultPath) {
                Get-Content $resultPath -Raw
            } else {
                'no Gate F2 result marker'
            }

            throw "VictusFanControl.App exited before Gate F2 READY. ExitCode=$($proc.ExitCode). $detail"
        }

        if ((Get-Date) -gt $deadline) {
            throw 'Timed out waiting for watchdog-protected Gate F2 READY.'
        }

        Start-Sleep -Milliseconds 50
    }

    $readyReached = $true
    Write-Host 'Gate F2 READY marker:' -ForegroundColor Green
    $readyMarker = (Get-Content $readyPath -Raw).Trim()
    Write-Host $readyMarker

    $readyMatch = [regex]::Match(
        $readyMarker,
        '^READY\|.+\|phase=WriteArmed\|cpu=30\|gpu=30\|pid=(\d+)\|ack=backend-ec\+tachs-before-commit

    if ($proc.HasExited) {
        throw 'Gate F2 GUI exited before the double-kill boundary.'
    }

    if (Test-Path $localRestoreStartedPath) {
        throw "Gate F2 GUI entered Restoring before fault injection: $((Get-Content $localRestoreStartedPath -Raw).Trim())"
    }

    $servicePidAtKillBoundary = Get-ServiceProcessId
    if ($servicePidAtKillBoundary -ne $servicePidBefore) {
        throw "Watchdog service identity changed before Gate F2 double kill: expected PID $servicePidBefore, observed $servicePidAtKillBoundary."
    }

    $serviceStartTicksAtBoundary = Get-ProcessStartTicks -ProcessId $servicePidAtKillBoundary
    if ($serviceStartTicksAtBoundary -ne $serviceStartTicksBefore) {
        throw "Watchdog PID $servicePidBefore was reused/restarted before Gate F2 fault injection."
    }

    $preKillJournal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $preKillWriteArmed = Test-JournalWriteArmedPhase -Phase $preKillJournal.Phase

    if (-not $preKillWriteArmed -or
        [int]$preKillJournal.Controller.ProcessId -ne $proc.Id -or
        [long]$preKillJournal.Controller.ProcessStartUtcTicks -ne $procStartTicks -or
        [int]$preKillJournal.Pending.Cpu -ne 30 -or
        [int]$preKillJournal.Pending.Gpu -ne 30 -or
        $null -ne $preKillJournal.PreviousOwned -or
        $null -ne $preKillJournal.Owned) {
        throw 'Gate F2 lost durable first-write WRITE_ARMED pending 30/30 or exact controller identity before double kill.'
    }

    $failsafe.Refresh()
    if ($failsafe.HasExited) {
        throw 'Independent emergency fallback is no longer armed at the Gate F2 double-kill boundary.'
    }

    Write-Host 'Pre-kill proof        : READY at post-WMI/post-EC+tachs/pre-Commit + durable WRITE_ARMED pending 30/30 + exact GUI/watchdog identities + live emergency fallback.' -ForegroundColor Green
    Write-Host 'No out-of-band EC probe is issued while Custom is active.' -ForegroundColor Green

    $watchdogProcess = [System.Diagnostics.Process]::GetProcessById($servicePidBefore)
    try {
        if ($watchdogProcess.HasExited) {
            throw 'Original watchdog exited before Gate F2 double kill.'
        }

        $watchdogHandleStartTicks = [long]$watchdogProcess.StartTime.ToUniversalTime().Ticks
        if ($watchdogHandleStartTicks -ne $serviceStartTicksBefore) {
            throw 'Opened watchdog process handle does not match the validated original creation time.'
        }

        Write-Host ''
        Write-Host 'Step 6: DOUBLE-KILL original watchdog then GUI with no sleep/probe between calls...' -ForegroundColor Yellow

        $watchdogKillTick = [System.Diagnostics.Stopwatch]::GetTimestamp()
        $watchdogProcess.Kill()
        $guiKillTick = [System.Diagnostics.Stopwatch]::GetTimestamp()
        $proc.Kill()
        $doubleKillIssued = $true

        $killDeltaMs =
            (($guiKillTick - $watchdogKillTick) * 1000.0) /
            [System.Diagnostics.Stopwatch]::Frequency

        Write-Host ("Kill issue delta     : {0:N3} ms" -f $killDeltaMs)

        if ($killDeltaMs -gt $MaxKillDeltaMs) {
            throw "Gate F2 double-kill issue delta $([Math]::Round($killDeltaMs, 3)) ms exceeds the $MaxKillDeltaMs ms causal bound."
        }

        if (-not $watchdogProcess.WaitForExit(3000)) {
            throw 'Original watchdog did not terminate within 3 s of force-kill.'
        }

        if (-not $proc.WaitForExit(3000)) {
            throw 'Original GUI did not terminate within 3 s of force-kill.'
        }
    }
    finally {
        $watchdogProcess.Dispose()
    }

    Write-Host "Original watchdog dead: PID=$servicePidBefore startTicks=$serviceStartTicksBefore"
    Write-Host "Original GUI dead     : PID=$($proc.Id) startTicks=$procStartTicks"

    if (Test-Path $localRestoreStartedPath) {
        $localAttempt = (Get-Content $localRestoreStartedPath -Raw).Trim()
        throw "Gate F2 live GUI began a local restore before its death; double-failure causality is invalid. Marker: $localAttempt"
    }

    Write-Host ''
    Write-Host 'Step 7: require SCM restart to recover the durable WRITE_ARMED journal...' -ForegroundColor Cyan

    $restart = Wait-ForStableWatchdogReady -Seconds 35 -RequiredRecovery 'RestoredFirmware' -ExcludedPid $servicePidBefore

    if (-not $restart) {
        throw 'SCM did not produce a stable replacement watchdog with RestoredFirmware within 35 s.'
    }

    $servicePidAfter = [int]$restart.Pid

    Write-Host "Restarted service PID: $servicePidAfter"
    Write-Host "Restart startTicks    : $($restart.StartTicks)"
    Write-Host "Restart Ready         : $($restart.Status.Ready)"
    Write-Host "Restart Blocked       : $($restart.Status.Blocked)"
    Write-Host "Restart recovery      : $($restart.Status.RecoveryDisposition)"
    Write-Host "Restart detail        : $($restart.Status.Detail)"

    if ($servicePidAfter -eq $servicePidBefore) {
        throw 'Gate F2 requires a distinct replacement watchdog PID after force-killing the original service process.'
    }

    if (-not (Wait-ForJournalGone -Seconds 5)) {
        throw 'Replacement watchdog reported RestoredFirmware but the durable WRITE_ARMED journal was not cleared.'
    }

    $final = Read-EcState
    Write-Host "Final EC              : $($final.Raw)"

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        throw "Gate F2 recovery completed but EC is not FF/FF: $($final.Cpu)/$($final.Gpu)."
    }

    if (Test-Path $localRestoreStartedPath) {
        throw 'Gate F2 found a GUI local-restore-started marker after SCM recovery; recovery cannot be attributed solely to durable restart.'
    }

    $failsafe.Refresh()
    if ($failsafe.HasExited) {
        throw 'Emergency fallback reached its delay before Gate F2 recovery was independently proven; Gate F2 cannot pass.'
    }

    Assert-ProductionRecoveryPolicy
    $pass = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try {
            $proc.WaitForExit(5000) | Out-Null
        }
        catch {
        }
    }

    $firmwareSafeState = $null

    if ($serviceInstalled) {
        $recoveryWaitSeconds =
            [Math]::Min($FailsafeDelaySeconds + 10, 140)

        $firmwareSafeState = Wait-ForFirmwareSafe -Seconds $recoveryWaitSeconds
    }

    $firmwareSafe = $null -ne $firmwareSafeState
    if ($firmwareSafe) {
        Write-Host "Post-test EC check    : $($firmwareSafeState.Raw)"
    }

    if ($firmwareSafe -and $failsafe) {
        $failsafe.Refresh()
        if (-not $failsafe.HasExited) {
            Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
            Write-Host 'Emergency fallback cancelled only after FF/FF + cleared journal were independently proven.' -ForegroundColor Green
        }
        elseif ($pass) {
            $pass = $false
            $failure = 'Gate F2 recovery reached firmware safety, but the emergency fallback had already executed/reached its delay.'
        }
    }
    elseif ($failsafe -and -not $firmwareSafe) {
        Write-Warning "Firmware safety is not yet independently proven. Emergency fallback PID $($failsafe.Id) remains armed."
    }

    if ($firmwareSafe -and $serviceInstalled -and -not $productionServiceReinstalled) {
        try {
            Restore-ProductionWatchdogService
        }
        catch {
            Write-Warning "Could not restore the production watchdog baseline automatically: $($_.Exception.Message)"
            if ($pass) {
                $pass = $false
                $failure = "Gate F2 recovery passed, but production watchdog baseline reinstall failed: $($_.Exception.Message)"
            }
        }
    }
}

if (-not $pass) {
    Write-Host ''
    Write-Host 'Gate F2 did NOT pass.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent watchdog log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 100
    }

    if (Test-Path $resultPath) {
        Write-Host ''
        Write-Host 'Gate F2 GUI result:' -ForegroundColor Cyan
        Get-Content $resultPath
    }

    if (Test-Path $localRestoreStartedPath) {
        Write-Host ''
        Write-Host 'Gate F2 local-restore-started marker:' -ForegroundColor Cyan
        Get-Content $localRestoreStartedPath
    }

    if (Test-Path $failsafeLog) {
        Write-Host ''
        Write-Host 'Emergency fallback log:' -ForegroundColor Cyan
        Get-Content $failsafeLog
    }

    exit 121
}

Write-Host ''
Write-Host 'Step 8: verify OMEN Gaming Hub undervolt...' -ForegroundColor Cyan
$post = Read-Host 'Type SAME if the CPU undervolt is unchanged, or CHANGED if it changed'

if ($post -cne 'SAME') {
    if ($post -ceq 'CHANGED') {
        Write-Warning 'Undervolt preservation FAILED/CHANGED.'
        exit 122
    }

    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 123
}

Write-Host ''
Write-Host 'PASS: Gate F2 proved durable WRITE_ARMED post-WMI/post-ACK recovery after near-simultaneous watchdog + GUI death.' -ForegroundColor Green
Write-Host 'Verified: WRITE_ARMED pending 30/30 after real WMI+EC+tachs ACK -> watchdog kill -> GUI kill within causal bound -> both originals dead -> SCM replacement RestoredFirmware -> journal cleared -> EC FF/FF.' -ForegroundColor Green
Write-Host 'Parent shell issued no HP fan restore; no live-GUI restore began; emergency fallback did not fire.' -ForegroundColor Green
Write-Host 'Production watchdog baseline was reinstalled and SCM recovery re-verified at 1 s / 5 s / 10 s.' -ForegroundColor Green
Write-Host 'OMEN Gaming Hub undervolt: SAME (user-confirmed).' -ForegroundColor Green
Write-Host 'F1 + F2 are now physically satisfied if this run is accepted with SAME; Gate F may be formally closed after evidence review/documentation.' -ForegroundColor Green
exit 0
)

    if (-not $readyMatch.Success) {
        throw "Gate F2 READY marker does not prove the post-ACK/pre-Commit WRITE_ARMED boundary: $readyMarker"
    }

    if (-not (Test-Path $journalPath)) {
        throw 'GUI reported Gate F2 READY but the durable watchdog journal is missing.'
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $journalWriteArmed = Test-JournalWriteArmedPhase -Phase $journal.Phase
    $procStartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks
    $readyPid = [int]$readyMatch.Groups[1].Value
    $phaseDisplay = if ($journalWriteArmed) { "$($journal.Phase) (WriteArmed)" } else { "$($journal.Phase)" }

    Write-Host "Journal phase        : $phaseDisplay"
    Write-Host "Journal generation   : $($journal.Generation)"
    Write-Host "Journal controller   : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "GUI identity         : PID=$($proc.Id) startTicks=$procStartTicks"
    Write-Host "READY marker PID     : $readyPid"
    Write-Host "Journal pending      : $($journal.Pending.Cpu)/$($journal.Pending.Gpu)"
    Write-Host "Journal previous     : $($journal.PreviousOwned)"
    Write-Host "Journal owned        : $($journal.Owned)"

    if (-not $journalWriteArmed -or
        $readyPid -ne $proc.Id -or
        [int]$journal.Controller.ProcessId -ne $proc.Id -or
        [long]$journal.Controller.ProcessStartUtcTicks -ne $procStartTicks -or
        [int]$journal.Pending.Cpu -ne 30 -or
        [int]$journal.Pending.Gpu -ne 30 -or
        $null -ne $journal.PreviousOwned -or
        $null -ne $journal.Owned) {
        throw 'Gate F2 READY is not backed by first-write durable WRITE_ARMED pending 30/30, no previous/owned target, and the exact GUI PID + creation time.'
    }

    if ($proc.HasExited) {
        throw 'Gate F2 GUI exited before the double-kill boundary.'
    }

    if (Test-Path $localRestoreStartedPath) {
        throw "Gate F2 GUI entered Restoring before fault injection: $((Get-Content $localRestoreStartedPath -Raw).Trim())"
    }

    $servicePidAtKillBoundary = Get-ServiceProcessId
    if ($servicePidAtKillBoundary -ne $servicePidBefore) {
        throw "Watchdog service identity changed before Gate F2 double kill: expected PID $servicePidBefore, observed $servicePidAtKillBoundary."
    }

    $serviceStartTicksAtBoundary = Get-ProcessStartTicks -ProcessId $servicePidAtKillBoundary
    if ($serviceStartTicksAtBoundary -ne $serviceStartTicksBefore) {
        throw "Watchdog PID $servicePidBefore was reused/restarted before Gate F2 fault injection."
    }

    $preKillJournal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $preKillOwned = Test-JournalOwnedPhase -Phase $preKillJournal.Phase

    if (-not $preKillOwned -or
        [int]$preKillJournal.Controller.ProcessId -ne $proc.Id -or
        [long]$preKillJournal.Controller.ProcessStartUtcTicks -ne $procStartTicks -or
        [int]$preKillJournal.Owned.Cpu -ne 30 -or
        [int]$preKillJournal.Owned.Gpu -ne 30) {
        throw 'Gate F2 lost durable OWNED 30/30 or exact controller identity before double kill.'
    }

    $failsafe.Refresh()
    if ($failsafe.HasExited) {
        throw 'Independent emergency fallback is no longer armed at the Gate F2 double-kill boundary.'
    }

    Write-Host 'Pre-kill proof        : READY backend EC+tachs ACK + durable OWNED 30/30 + exact GUI/watchdog identities + live emergency fallback.' -ForegroundColor Green
    Write-Host 'No out-of-band EC probe is issued while Custom is active.' -ForegroundColor Green

    $watchdogProcess = [System.Diagnostics.Process]::GetProcessById($servicePidBefore)
    try {
        if ($watchdogProcess.HasExited) {
            throw 'Original watchdog exited before Gate F2 double kill.'
        }

        $watchdogHandleStartTicks = [long]$watchdogProcess.StartTime.ToUniversalTime().Ticks
        if ($watchdogHandleStartTicks -ne $serviceStartTicksBefore) {
            throw 'Opened watchdog process handle does not match the validated original creation time.'
        }

        Write-Host ''
        Write-Host 'Step 6: DOUBLE-KILL original watchdog then GUI with no sleep/probe between calls...' -ForegroundColor Yellow

        $watchdogKillTick = [System.Diagnostics.Stopwatch]::GetTimestamp()
        $watchdogProcess.Kill()
        $guiKillTick = [System.Diagnostics.Stopwatch]::GetTimestamp()
        $proc.Kill()
        $doubleKillIssued = $true

        $killDeltaMs =
            (($guiKillTick - $watchdogKillTick) * 1000.0) /
            [System.Diagnostics.Stopwatch]::Frequency

        Write-Host ("Kill issue delta     : {0:N3} ms" -f $killDeltaMs)

        if ($killDeltaMs -gt $MaxKillDeltaMs) {
            throw "Gate F2 double-kill issue delta $([Math]::Round($killDeltaMs, 3)) ms exceeds the $MaxKillDeltaMs ms causal bound."
        }

        if (-not $watchdogProcess.WaitForExit(3000)) {
            throw 'Original watchdog did not terminate within 3 s of force-kill.'
        }

        if (-not $proc.WaitForExit(3000)) {
            throw 'Original GUI did not terminate within 3 s of force-kill.'
        }
    }
    finally {
        $watchdogProcess.Dispose()
    }

    Write-Host "Original watchdog dead: PID=$servicePidBefore startTicks=$serviceStartTicksBefore"
    Write-Host "Original GUI dead     : PID=$($proc.Id) startTicks=$procStartTicks"

    if (Test-Path $localRestoreStartedPath) {
        $localAttempt = (Get-Content $localRestoreStartedPath -Raw).Trim()
        throw "Gate F2 live GUI began a local restore before its death; double-failure causality is invalid. Marker: $localAttempt"
    }

    Write-Host ''
    Write-Host 'Step 7: require SCM restart to recover the durable WRITE_ARMED journal...' -ForegroundColor Cyan

    $restart = Wait-ForStableWatchdogReady -Seconds 35 -RequiredRecovery 'RestoredFirmware' -ExcludedPid $servicePidBefore

    if (-not $restart) {
        throw 'SCM did not produce a stable replacement watchdog with RestoredFirmware within 35 s.'
    }

    $servicePidAfter = [int]$restart.Pid

    Write-Host "Restarted service PID: $servicePidAfter"
    Write-Host "Restart startTicks    : $($restart.StartTicks)"
    Write-Host "Restart Ready         : $($restart.Status.Ready)"
    Write-Host "Restart Blocked       : $($restart.Status.Blocked)"
    Write-Host "Restart recovery      : $($restart.Status.RecoveryDisposition)"
    Write-Host "Restart detail        : $($restart.Status.Detail)"

    if ($servicePidAfter -eq $servicePidBefore) {
        throw 'Gate F2 requires a distinct replacement watchdog PID after force-killing the original service process.'
    }

    if (-not (Wait-ForJournalGone -Seconds 5)) {
        throw 'Replacement watchdog reported RestoredFirmware but the durable WRITE_ARMED journal was not cleared.'
    }

    $final = Read-EcState
    Write-Host "Final EC              : $($final.Raw)"

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        throw "Gate F2 recovery completed but EC is not FF/FF: $($final.Cpu)/$($final.Gpu)."
    }

    if (Test-Path $localRestoreStartedPath) {
        throw 'Gate F2 found a GUI local-restore-started marker after SCM recovery; recovery cannot be attributed solely to durable restart.'
    }

    $failsafe.Refresh()
    if ($failsafe.HasExited) {
        throw 'Emergency fallback reached its delay before Gate F2 recovery was independently proven; Gate F2 cannot pass.'
    }

    Assert-ProductionRecoveryPolicy
    $pass = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try {
            $proc.WaitForExit(5000) | Out-Null
        }
        catch {
        }
    }

    $firmwareSafeState = $null

    if ($serviceInstalled) {
        $recoveryWaitSeconds =
            [Math]::Min($FailsafeDelaySeconds + 10, 140)

        $firmwareSafeState = Wait-ForFirmwareSafe -Seconds $recoveryWaitSeconds
    }

    $firmwareSafe = $null -ne $firmwareSafeState
    if ($firmwareSafe) {
        Write-Host "Post-test EC check    : $($firmwareSafeState.Raw)"
    }

    if ($firmwareSafe -and $failsafe) {
        $failsafe.Refresh()
        if (-not $failsafe.HasExited) {
            Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
            Write-Host 'Emergency fallback cancelled only after FF/FF + cleared journal were independently proven.' -ForegroundColor Green
        }
        elseif ($pass) {
            $pass = $false
            $failure = 'Gate F2 recovery reached firmware safety, but the emergency fallback had already executed/reached its delay.'
        }
    }
    elseif ($failsafe -and -not $firmwareSafe) {
        Write-Warning "Firmware safety is not yet independently proven. Emergency fallback PID $($failsafe.Id) remains armed."
    }

    if ($firmwareSafe -and $serviceInstalled -and -not $productionServiceReinstalled) {
        try {
            Restore-ProductionWatchdogService
        }
        catch {
            Write-Warning "Could not restore the production watchdog baseline automatically: $($_.Exception.Message)"
            if ($pass) {
                $pass = $false
                $failure = "Gate F2 recovery passed, but production watchdog baseline reinstall failed: $($_.Exception.Message)"
            }
        }
    }
}

if (-not $pass) {
    Write-Host ''
    Write-Host 'Gate F2 did NOT pass.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent watchdog log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 100
    }

    if (Test-Path $resultPath) {
        Write-Host ''
        Write-Host 'Gate F2 GUI result:' -ForegroundColor Cyan
        Get-Content $resultPath
    }

    if (Test-Path $localRestoreStartedPath) {
        Write-Host ''
        Write-Host 'Gate F2 local-restore-started marker:' -ForegroundColor Cyan
        Get-Content $localRestoreStartedPath
    }

    if (Test-Path $failsafeLog) {
        Write-Host ''
        Write-Host 'Emergency fallback log:' -ForegroundColor Cyan
        Get-Content $failsafeLog
    }

    exit 121
}

Write-Host ''
Write-Host 'Step 8: verify OMEN Gaming Hub undervolt...' -ForegroundColor Cyan
$post = Read-Host 'Type SAME if the CPU undervolt is unchanged, or CHANGED if it changed'

if ($post -cne 'SAME') {
    if ($post -ceq 'CHANGED') {
        Write-Warning 'Undervolt preservation FAILED/CHANGED.'
        exit 122
    }

    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 123
}

Write-Host ''
Write-Host 'PASS: Gate F2 proved durable WRITE_ARMED recovery after near-simultaneous watchdog + GUI death.' -ForegroundColor Green
Write-Host 'Verified: WRITE_ARMED pending 30/30 after real WMI+EC+tachs ACK -> watchdog kill -> GUI kill within causal bound -> both originals dead -> SCM replacement RestoredFirmware -> journal cleared -> EC FF/FF.' -ForegroundColor Green
Write-Host 'Parent shell issued no HP fan restore; no live-GUI restore began; emergency fallback did not fire.' -ForegroundColor Green
Write-Host 'Production watchdog baseline was reinstalled and SCM recovery re-verified at 1 s / 5 s / 10 s.' -ForegroundColor Green
Write-Host 'OMEN Gaming Hub undervolt: SAME (user-confirmed).' -ForegroundColor Green
Write-Host 'Gate F remains OPEN until F2 validates WRITE_ARMED after real WMI + EC/tach ACK but before Commit.' -ForegroundColor Yellow
exit 0
