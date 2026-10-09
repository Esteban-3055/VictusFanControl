param(
    [ValidateRange(10, 30)]
    [int]$LocalRestoreTimeoutSeconds = 12,
    [ValidateRange(10, 30)]
    [int]$RestartRecoveryTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM4'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$stateDir = Join-Path $serviceRoot 'state'
$statusPath = Join-Path $stateDir 'm4-8c40.status.json'
$journalPath = Join-Path $stateDir 'lease.json'
$serviceLog = Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$localRoot = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath = Join-Path $localRoot 'm5b-8c40-watchdog.ready.json'
$localRestorePath = Join-Path $localRoot 'm5b-8c40-watchdog.local-restore.json'
$completionPath = Join-Path $localRoot 'm5b-8c40-watchdog.parent-complete'

$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'
$requiredToken = '8C40-M5B-WATCHDOG-DEATH30'

$controller = $null
$servicePidBefore = 0
$servicePidAfter = 0
$readyReached = $false
$watchdogKilled = $false
$localRestoreProven = $false
$restartRecoveryProven = $false
$pass = $false
$failure = $null
$logLineBoundary = 0

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'M5B must be run from an elevated PowerShell.'
    }
}

function Read-8C40Setpoint {
    $output = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    $line = ($output -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1)

    if (-not $line) {
        throw "Could not parse HP 8C40 setpoint probe. Raw output: $output"
    }

    $match = [regex]::Match(
        $line,
        '^setpoint CPU=(\d+) GPU=(\d+)$')

    if (-not $match.Success) {
        throw "Could not parse HP 8C40 setpoints from: $line"
    }

    [pscustomobject]@{
        Cpu = [int]$match.Groups[1].Value
        Gpu = [int]$match.Groups[2].Value
        Raw = $line
    }
}

function Get-ServiceProcessId {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue

    if (-not $svc -or $svc.State -ne 'Running') {
        return 0
    }

    return [int]$svc.ProcessId
}

function Wait-ServiceStopped {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue

        if ($svc -and $svc.State -eq 'Stopped') {
            return $true
        }

        Start-Sleep -Milliseconds 100
    }

    return $false
}

function Wait-FreshM4Ready {
    param(
        [int]$ExpectedPid,
        [int]$Seconds,
        [string[]]$AllowedRecoveryDispositions
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statusPath) {
            try {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json

                if ([int]$status.ProcessId -eq $ExpectedPid -and
                    $status.Ready -and
                    -not $status.Blocked -and
                    [int]$status.SessionId -eq 0 -and
                    $status.AccountName -match 'SYSTEM$' -and
                    $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                    $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                    $AllowedRecoveryDispositions -contains [string]$status.RecoveryDisposition) {
                    return $status
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for fresh M4 Ready status for PID=$ExpectedPid."
}

function Wait-ForFile {
    param(
        [string]$Path,
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while (-not (Test-Path $Path) -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }

    return (Test-Path $Path)
}

function Wait-ForJournalGone {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Test-Path $journalPath) -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }

    return (-not (Test-Path $journalPath))
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

function Assert-OwnedJournalForController {
    param(
        $Journal,
        [int]$ControllerPid,
        [long]$ControllerStartTicks
    )

    $owned = Test-JournalOwnedPhase -Phase $Journal.Phase

    if ([int]$Journal.SchemaVersion -ne 2 -or
        $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not $owned -or
        [int]$Journal.Controller.ProcessId -ne $ControllerPid -or
        [long]$Journal.Controller.ProcessStartUtcTicks -ne $ControllerStartTicks -or
        [int]$Journal.Owned.Cpu -ne 30 -or
        [int]$Journal.Owned.Gpu -ne 30) {
        throw 'M5B durable journal is not exact-target OWNED 30/30 bound to the exact controller PID + creation time.'
    }
}

function Show-Diagnostics {
    Write-Host ''

    foreach ($entry in @(
        @{ Label = 'M5B READY marker'; Path = $readyPath },
        @{ Label = 'M5B local-restore marker'; Path = $localRestorePath },
        @{ Label = 'M4 service status'; Path = $statusPath },
        @{ Label = 'M4 durable journal'; Path = $journalPath }
    )) {
        if (Test-Path $entry.Path) {
            Write-Host $entry.Label -ForegroundColor Cyan
            Get-Content $entry.Path
            Write-Host ''
        }
    }

    if (Test-Path $serviceLog) {
        Write-Host 'Recent M4 service log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 140
        Write-Host ''
    }

    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M5B WATCHDOG-DEATH RECOVERY' -ForegroundColor Cyan
Write-Host ''
Write-Host 'M5B force-kills only the LocalSystem watchdog while the controller remains alive.' -ForegroundColor Yellow
Write-Host 'The live controller must detect WATCHDOG_IPC_LOSS and locally restore FF/FF.' -ForegroundColor Yellow
Write-Host 'Only after that proof does the parent manually restart the service so startup recovery can consume the retained journal.' -ForegroundColor Yellow
Write-Host 'The parent PowerShell never issues a direct HP/WMI restore command.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "Refusing M5B while process '$name' is running. Close it and retry."
    }
}

Write-Host 'Step 1: build + watchdog/backend/safety regressions...' -ForegroundColor Cyan

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m4-8c40-self-test
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
Write-Host 'Step 2: clean firmware-owned baseline...' -ForegroundColor Cyan

$baseline = Read-8C40Setpoint
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or
    $baseline.Gpu -ne 255) {
    throw "M5B requires firmware-owned FF/FF baseline; observed $($baseline.Cpu)/$($baseline.Gpu)."
}

if (Test-Path $journalPath) {
    Write-Host 'Durable lease evidence already exists. M5B will not delete it.' -ForegroundColor Red
    Get-Content $journalPath
    throw "M5B requires an absent journal before installation: $journalPath"
}

Write-Host ''
Write-Host 'Step 3: install/start isolated LocalSystem M4 service...' -ForegroundColor Cyan

& (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')

Remove-Item $statusPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $localRoot | Out-Null
Remove-Item $readyPath, $localRestorePath, $completionPath -Force -ErrorAction SilentlyContinue

Start-Service -Name $serviceName
$servicePidBefore = 0
$pidDeadline = (Get-Date).AddSeconds(15)

while ($servicePidBefore -le 0 -and
       (Get-Date) -lt $pidDeadline) {
    $servicePidBefore = Get-ServiceProcessId

    if ($servicePidBefore -le 0) {
        Start-Sleep -Milliseconds 100
    }
}

if ($servicePidBefore -le 0) {
    throw 'Could not resolve the running M4 service PID.'
}

$status = Wait-FreshM4Ready -ExpectedPid $servicePidBefore -Seconds 15 -AllowedRecoveryDispositions @('Ready','ClearedPrepared','RestoredFirmware')

Write-Host "M4 Ready          : $($status.Ready)"
Write-Host "Session           : $($status.SessionId)"
Write-Host "Account           : $($status.AccountName)"
Write-Host "Target            : $($status.TargetProfileId)"
Write-Host "Service PID       : $servicePidBefore"
Write-Host "Startup recovery  : $($status.RecoveryDisposition)"

$logLineBoundary = if (Test-Path $serviceLog) {
    @(Get-Content $serviceLog).Count
} else {
    0
}

Write-Host ''
Write-Host 'ACTIVE M5B FAILURE-INJECTION BOUNDARY' -ForegroundColor Yellow
Write-Host 'The child will acquire OWNED 30/30. The harness will then kill ONLY the watchdog service process.' -ForegroundColor Yellow
Write-Host 'Do not suspend/hibernate, close the lid, start a workload, kill the child, or close this shell.' -ForegroundColor Yellow
Write-Host ''

$token = Read-Host "Type exactly $requiredToken to continue"

if ($token -cne $requiredToken) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    throw 'M5B cancelled before any fan write.'
}

try {
    Write-Host ''
    Write-Host 'Step 4: launch M5B live controller and wait for durable OWNED 30/30...' -ForegroundColor Cyan

    $controller = Start-Process -FilePath 'dotnet' -ArgumentList @(
        $cli,
        '--8c40-m5b-watchdog-death-controller',
        '--8c40-m5b-token',
        $requiredToken,
        '--8c40-m5b-ready-path',
        $readyPath,
        '--8c40-m5b-local-restore-path',
        $localRestorePath,
        '--8c40-m5b-completion-path',
        $completionPath,
        '--modules-dir',
        $modulesDir
    ) -PassThru -NoNewWindow

    $deadline = (Get-Date).AddSeconds(45)

    while (-not (Test-Path $readyPath)) {
        if ($controller.HasExited) {
            throw "M5B controller exited before READY. ExitCode=$($controller.ExitCode)."
        }

        if ((Get-Date) -gt $deadline) {
            throw 'Timed out waiting for M5B controller READY.'
        }

        Start-Sleep -Milliseconds 100
    }

    $readyReached = $true
    $ready = Get-Content $readyPath -Raw | ConvertFrom-Json
    $controllerStartTicks = [long]$controller.StartTime.ToUniversalTime().Ticks

    Write-Host 'M5B READY marker:' -ForegroundColor Green
    Get-Content $readyPath

    if ([int]$ready.SchemaVersion -ne 1 -or
        $ready.Gate -cne 'M5B' -or
        $ready.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        [int]$ready.ProcessId -ne $controller.Id -or
        [long]$ready.ProcessStartUtcTicks -ne $controllerStartTicks -or
        $ready.Authority -cne 'Custom' -or
        [int]$ready.CpuSetpoint -ne 30 -or
        [int]$ready.GpuSetpoint -ne 30 -or
        [int]$ready.MaxFan -ne 0 -or
        [int]$ready.FanSwitch -ne 0 -or
        [int]$ready.CpuRpm -le 0 -or
        [int]$ready.GpuRpm -le 0 -or
        $ready.Ack -cne 'backend-ec+tachs+watchdog-owned') {
        throw 'M5B READY did not prove exact controller identity plus healthy backend EC+tachs+watchdog OWNED 30/30.'
    }

    if (-not (Test-Path $journalPath)) {
        throw 'M5B READY exists but the durable watchdog journal is missing.'
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournalForController -Journal $journal -ControllerPid $controller.Id -ControllerStartTicks $controllerStartTicks

    Write-Host "Journal phase      : $($journal.Phase)"
    Write-Host "Journal generation : $($journal.Generation)"
    Write-Host "Journal controller : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "Journal target     : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

    if ($controller.HasExited) {
        throw 'M5B controller exited before watchdog-death injection.'
    }

    if ((Get-ServiceProcessId) -ne $servicePidBefore) {
        throw 'M5B watchdog PID changed before intentional watchdog death.'
    }

    Write-Host ''
    Write-Host "Step 5: FORCE-KILL watchdog service PID $servicePidBefore; controller stays alive..." -ForegroundColor Yellow

    Stop-Process -Id $servicePidBefore -Force
    $watchdogKilled = $true

    if (-not (Wait-ServiceStopped -Seconds 5)) {
        throw 'SCM did not report the M4 watchdog service Stopped after force-kill.'
    }

    if ((Get-ServiceProcessId) -ne 0) {
        throw 'A watchdog service process still exists after the M5B kill boundary.'
    }

    if ($controller.HasExited) {
        throw 'M5B controller died together with the watchdog; that belongs to M5C, not M5B.'
    }

    Write-Host 'Watchdog is absent. Waiting for live-controller WATCHDOG_IPC_LOSS local restore...' -ForegroundColor Cyan

    if (-not (Wait-ForFile -Path $localRestorePath -Seconds $LocalRestoreTimeoutSeconds)) {
        throw "M5B live controller did not publish local restore within $LocalRestoreTimeoutSeconds s."
    }

    $local = Get-Content $localRestorePath -Raw | ConvertFrom-Json

    Write-Host 'M5B local-restore marker:' -ForegroundColor Green
    Get-Content $localRestorePath

    if ([int]$local.SchemaVersion -ne 1 -or
        $local.Gate -cne 'M5B' -or
        $local.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        [int]$local.ProcessId -ne $controller.Id -or
        [long]$local.ProcessStartUtcTicks -ne $controllerStartTicks -or
        $local.Authority -cne 'Firmware' -or
        $local.Reason -notmatch 'WATCHDOG_IPC_LOSS' -or
        [int]$local.CpuSetpoint -ne 255 -or
        [int]$local.GpuSetpoint -ne 255 -or
        [int]$local.MaxFan -ne 0 -or
        [int]$local.FanSwitch -ne 0 -or
        -not [bool]$local.LocalFirmwareAckVerified -or
        -not [bool]$local.WatchdogLeaseRequired -or
        [bool]$local.WatchdogReleaseVerified) {
        throw 'M5B local marker did not causally prove WATCHDOG_IPC_LOSS -> live-controller local FF/FF with watchdog release unavailable.'
    }

    if ((Get-ServiceProcessId) -ne 0) {
        throw 'A replacement watchdog appeared before M5B local-controller restore proof.'
    }

    if ($controller.HasExited) {
        throw 'M5B controller exited before parent verified its local restore.'
    }

    $localIndependent = Read-8C40Setpoint
    Write-Host "Independent local EC : $($localIndependent.Raw)"

    if ($localIndependent.Cpu -ne 255 -or
        $localIndependent.Gpu -ne 255) {
        throw "M5B live controller reported local restore but independent EC is $($localIndependent.Cpu)/$($localIndependent.Gpu)."
    }

    if (-not (Test-Path $journalPath)) {
        throw 'M5B durable OWNED journal disappeared while watchdog was absent; retained service evidence was expected.'
    }

    $retained = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournalForController -Journal $retained -ControllerPid $controller.Id -ControllerStartTicks $controllerStartTicks

    $localRestoreProven = $true

    Write-Host ''
    Write-Host 'Step 6: manually start a replacement service only AFTER local-controller restore proof...' -ForegroundColor Cyan

    Start-Service -Name $serviceName

    $restartPidDeadline = (Get-Date).AddSeconds(10)

    while ($servicePidAfter -le 0 -and
           (Get-Date) -lt $restartPidDeadline) {
        $servicePidAfter = Get-ServiceProcessId

        if ($servicePidAfter -le 0) {
            Start-Sleep -Milliseconds 100
        }
    }

    if ($servicePidAfter -le 0) {
        throw 'M5B replacement service did not start.'
    }

    if ($servicePidAfter -eq $servicePidBefore) {
        throw 'M5B replacement service unexpectedly reused the original watchdog PID.'
    }

    $restartStatus = Wait-FreshM4Ready -ExpectedPid $servicePidAfter -Seconds $RestartRecoveryTimeoutSeconds -AllowedRecoveryDispositions @('RestoredFirmware')

    Write-Host "Replacement PID       : $servicePidAfter"
    Write-Host "Startup recovery      : $($restartStatus.RecoveryDisposition)"
    Write-Host "Startup detail        : $($restartStatus.Detail)"

    if (-not (Wait-ForJournalGone -Seconds 2)) {
        throw 'M5B replacement service reported RestoredFirmware but durable journal remains.'
    }

    $final = Read-8C40Setpoint
    Write-Host "Independent final EC  : $($final.Raw)"

    if ($final.Cpu -ne 255 -or
        $final.Gpu -ne 255) {
        throw "M5B restart recovery completed but final EC is $($final.Cpu)/$($final.Gpu), expected FF/FF."
    }

    $restartRecoveryProven = $true

    "M5B-PARENT-COMPLETE|replacementPid=$servicePidAfter|recovery=RestoredFirmware|final=FF/FF" |
        Set-Content -Path $completionPath -Encoding Ascii

    $controllerExitDeadline = (Get-Date).AddSeconds(8)

    while (-not $controller.HasExited -and
           (Get-Date) -lt $controllerExitDeadline) {
        Start-Sleep -Milliseconds 100
    }

    if (-not $controller.HasExited) {
        throw 'M5B controller did not exit after parent completion proof.'
    }

    # PowerShell can expose a blank ExitCode on a Start-Process -PassThru object
    # when HasExited is observed before the native exit information has been
    # synchronized into the Process instance. Refresh the handle-backed state
    # explicitly before treating ExitCode as a PASS/FAIL signal.
    $controller.WaitForExit()
    $controller.Refresh()
    $controllerExitCode = $controller.ExitCode

    if ($null -eq $controllerExitCode) {
        throw 'M5B controller exited after parent completion proof, but PowerShell did not expose a synchronized ExitCode.'
    }

    if ([int]$controllerExitCode -ne 0) {
        throw "M5B controller exited with code $controllerExitCode after recovery."
    }

    $newLog = @()
    if (Test-Path $serviceLog) {
        $allLog = @(Get-Content $serviceLog)
        $newLog = @($allLog | Select-Object -Skip $logLineBoundary)
    }

    Write-Host ''
    Write-Host 'M5B watchdog/service evidence:' -ForegroundColor Cyan
    $newLog | Select-Object -Last 100

    $startupRecoveryEvidence =
        $newLog |
        Where-Object {
            $_ -match 'M4 STARTUP RECOVERY disposition=RestoredFirmware' -and
            $_ -match 'journalRetained=False'
        }

    if (-not $startupRecoveryEvidence) {
        throw 'M5B final state is safe but service log lacks fresh startup RestoredFirmware evidence.'
    }

    $pass = $true

    Write-Host ''
    Write-Host 'PASS: HP 8C40 M5B watchdog-death recovery completed.' -ForegroundColor Green
    Write-Host 'Proven: OWNED 30/30 -> watchdog PID death -> live controller WATCHDOG_IPC_LOSS -> local FF/FF -> retained journal -> replacement startup RestoredFirmware -> journal cleared.' -ForegroundColor Green
    Write-Host 'M5C double-death remains a separate gate.' -ForegroundColor Yellow
}
catch {
    $failure = $_.Exception.Message
    Show-Diagnostics
}
finally {
    if ($controller -and -not $controller.HasExited) {
        if (-not $localRestoreProven) {
            Write-Warning 'M5B failed before live-controller local-restore proof. Force-killing the exact child; service-journal recovery becomes the cleanup path.'
        }

        Stop-Process -Id $controller.Id -Force -ErrorAction SilentlyContinue
        try {
            [void]$controller.WaitForExit(5000)
        }
        catch {
        }
    }

    if (Test-Path $journalPath) {
        $pid = Get-ServiceProcessId

        if ($pid -le 0) {
            Write-Warning 'Cleanup: durable journal remains while service is absent; starting M4 service for startup recovery.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }

        [void](Wait-ForJournalGone -Seconds $RestartRecoveryTimeoutSeconds)
    }

    $firmwareSafe = $false

    if (-not (Test-Path $journalPath)) {
        try {
            $cleanupState = Read-8C40Setpoint
            Write-Host "Post-test EC check      : $($cleanupState.Raw)"
            $firmwareSafe =
                $cleanupState.Cpu -eq 255 -and
                $cleanupState.Gpu -eq 255
        }
        catch {
            Write-Warning "Final read-only EC check failed: $($_.Exception.Message)"
        }
    }

    if ($firmwareSafe) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    }
    elseif (Test-Path $journalPath) {
        Write-Host ''
        Write-Host 'CRITICAL: durable M5B ownership evidence remains. It was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Do not reinstall the service or run another fan-write gate until this state is inspected/recovered.' -ForegroundColor Red
    }

    Remove-Item $completionPath -Force -ErrorAction SilentlyContinue
}

if (-not $pass) {
    throw "M5B FAILED: $failure"
}
