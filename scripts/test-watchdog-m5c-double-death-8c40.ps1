param(
    [ValidateRange(10, 100)]
    [int]$MaxKillDeltaMs = 50,

    [ValidateRange(3000, 10000)]
    [int]$RestartDelayMs = 5000,

    [ValidateRange(90, 300)]
    [int]$FailsafeDelaySeconds = 120,

    [ValidateRange(10, 40)]
    [int]$RecoveryTimeoutSeconds = 25
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
$failsafeLog = Join-Path $serviceRoot ("logs\watchdog-m5c-failsafe-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd-HHmmss'))

$localRoot = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath = Join-Path $localRoot 'm5c-8c40-owned.ready.json'

$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'
$failsafeScript = Join-Path $PSScriptRoot 'watchdog-m5c-service-failsafe-8c40.ps1'

$userToken = '8C40-M5C-DOUBLE-DEATH30'
$ownedArmToken = '8C40-M5A-CONTROLLER-DEATH30'

$controller = $null
$watchdogProcess = $null
$failsafe = $null
$pass = $false
$failure = $null
$servicePidBefore = 0
$serviceStartTicksBefore = 0L
$servicePidAfter = 0
$logLineBoundary = 0

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'M5C must be run from an elevated PowerShell.'
    }
}

function Read-8C40Setpoint {
    $output = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    $line = ($output -split "[\r\n]+" | Where-Object { $_ -match '^setpoint CPU=' } | Select-Object -Last 1)

    if (-not $line) {
        throw "Could not parse HP 8C40 setpoint probe. Raw output: $output"
    }

    $match = [regex]::Match($line, '^setpoint CPU=(\d+) GPU=(\d+)$')
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

function Test-JournalOwnedPhase {
    param($Phase)

    if ($null -eq $Phase) { return $false }
    if ($Phase -is [string]) {
        return ($Phase -ceq 'Owned' -or $Phase -ceq '2')
    }

    try { return ([int]$Phase -eq 2) }
    catch { return $false }
}

function Assert-OwnedJournal {
    param(
        $Journal,
        [int]$ControllerPid,
        [long]$ControllerStartTicks
    )

    if ([int]$Journal.SchemaVersion -ne 2 -or
        $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not (Test-JournalOwnedPhase -Phase $Journal.Phase) -or
        [int]$Journal.Controller.ProcessId -ne $ControllerPid -or
        [long]$Journal.Controller.ProcessStartUtcTicks -ne $ControllerStartTicks -or
        [int]$Journal.Owned.Cpu -ne 30 -or
        [int]$Journal.Owned.Gpu -ne 30) {
        throw 'M5C durable journal is not exact-target OWNED 30/30 bound to the exact controller PID + creation time.'
    }
}

function Wait-InitialReady {
    param([int]$ExpectedPid)

    $deadline = (Get-Date).AddSeconds(20)
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
                    $status.RecoveryDisposition -ceq 'Ready') {
                    return $status
                }
            }
            catch {
            }
        }
        Start-Sleep -Milliseconds 100
    }

    throw 'Timed out waiting for the original exact-target M4 Ready state.'
}

function Wait-ServiceAbsent {
    param([int]$Milliseconds)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $Milliseconds) {
        if ((Get-ServiceProcessId) -eq 0) {
            return $true
        }
        Start-Sleep -Milliseconds 25
    }
    return $false
}

function Wait-ReplacementRecovery {
    param(
        [int]$OriginalPid,
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $currentPid = Get-ServiceProcessId

        if ($currentPid -gt 0 -and $currentPid -ne $OriginalPid -and (Test-Path $statusPath)) {
            try {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json
                if ([int]$status.ProcessId -eq $currentPid -and
                    $status.Ready -and
                    -not $status.Blocked -and
                    [int]$status.SessionId -eq 0 -and
                    $status.AccountName -match 'SYSTEM$' -and
                    $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                    $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                    $status.RecoveryDisposition -ceq 'RestoredFirmware') {
                    Start-Sleep -Milliseconds 300
                    if ((Get-ServiceProcessId) -eq $currentPid) {
                        return [pscustomobject]@{
                            Pid = $currentPid
                            StartTicks = Get-ProcessStartTicks -ProcessId $currentPid
                            Status = $status
                        }
                    }
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 100
    }

    return $null
}

function Wait-ForJournalGone {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Test-Path $journalPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    return (-not (Test-Path $journalPath))
}

function Configure-M5CRecoveryPolicy {
    & sc.exe failure $serviceName reset= 86400 actions= "restart/$RestartDelayMs/restart/5000/restart/10000" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not configure M5C SCM recovery actions; sc.exe exit=$LASTEXITCODE."
    }

    & sc.exe failureflag $serviceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enable M5C SCM failure actions; sc.exe exit=$LASTEXITCODE."
    }

    $qfailure = (& sc.exe qfailure $serviceName 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not query M5C SCM recovery policy; sc.exe exit=$LASTEXITCODE."
    }

    if ($qfailure -notmatch ("(?s){0}\s*ms.*5000\s*ms.*10000\s*ms" -f $RestartDelayMs)) {
        throw "M5C SCM recovery policy does not contain ordered $RestartDelayMs/5000/10000 ms restarts. Raw output: $qfailure"
    }
}

function Start-DelayedFailsafe {
    if (-not (Test-Path $failsafeScript)) {
        throw "M5C delayed failsafe script is missing: $failsafeScript"
    }

    Remove-Item $failsafeLog -Force -ErrorAction SilentlyContinue

    $process = Start-Process powershell.exe -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $failsafeScript,
        '-DelaySeconds', $FailsafeDelaySeconds,
        '-LogPath', $failsafeLog
    ) -WindowStyle Hidden -PassThru

    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) {
        throw 'M5C delayed failsafe exited before the fault boundary.'
    }

    return $process
}

function Test-FailsafeTakeover {
    if (-not (Test-Path $failsafeLog)) { return $false }
    $text = Get-Content $failsafeLog -Raw
    return ($text -match 'M5C FAILSAFE TAKEOVER:' -or
            $text -match 'M5C FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M5C FAILSAFE SERVICE-START:' -or
            $text -match 'M5C FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M5C FAILSAFE STARTED:' -or
            $text -match 'M5C FAILSAFE RECOVERED:')
}

function Restore-M4Baseline {
    if (Test-Path $journalPath) {
        throw 'M5C refuses to reinstall the service while durable lease evidence remains.'
    }

    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')

    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    if ($svc.StartType -ne 'Manual' -or $svc.Status -ne 'Stopped') {
        throw "M5C baseline restore failed: StartType=$($svc.StartType) Status=$($svc.Status)."
    }
}

function Show-Diagnostics {
    Write-Host ''

    if (Test-Path $readyPath) {
        Write-Host 'M5C READY marker:' -ForegroundColor Cyan
        Get-Content $readyPath
        Write-Host ''
    }

    if (Test-Path $statusPath) {
        Write-Host 'M4 service status:' -ForegroundColor Cyan
        Get-Content $statusPath
        Write-Host ''
    }

    if (Test-Path $journalPath) {
        Write-Host 'M4 durable journal:' -ForegroundColor Yellow
        Get-Content $journalPath
        Write-Host ''
    }

    if (Test-Path $failsafeLog) {
        Write-Host 'M5C failsafe log:' -ForegroundColor Cyan
        Get-Content $failsafeLog
        Write-Host ''
    }

    if (Test-Path $serviceLog) {
        Write-Host 'Recent M4 service log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 120
        Write-Host ''
    }

    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M5C OWNED DOUBLE-DEATH RECOVERY' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This is the destructive M5C gate.' -ForegroundColor Yellow
Write-Host 'It will acquire watchdog-backed OWNED 30/30 and then force-kill the original watchdog and controller back-to-back.' -ForegroundColor Yellow
Write-Host 'Recovery must come from a distinct SCM-restarted LocalSystem watchdog reading the durable journal.' -ForegroundColor Yellow
Write-Host 'The parent shell never issues a direct HP/WMI restore.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "M5C refused while '$name' is running. Close it and retry."
    }
}

Write-Host 'Step 1: build + current M5/M4/safety regressions...' -ForegroundColor Cyan

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-m5a-8c40-invariants.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-m5b-8c40-invariants.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-m5c-preflight-invariants.ps1')
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
Write-Host 'Step 2: clean firmware/durable-state baseline...' -ForegroundColor Cyan
$baseline = Read-8C40Setpoint
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    throw "M5C requires FF/FF baseline; observed $($baseline.Cpu)/$($baseline.Gpu)."
}

if (Test-Path $journalPath) {
    Get-Content $journalPath
    throw 'M5C refuses an existing durable journal. Do not delete retained ownership evidence.'
}

Write-Host ''
Write-Host 'Step 3: install exact-target LocalSystem service + temporary SCM recovery...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')
Configure-M5CRecoveryPolicy

Remove-Item $statusPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $localRoot | Out-Null
Remove-Item $readyPath -Force -ErrorAction SilentlyContinue
Start-Service -Name $serviceName

$pidDeadline = (Get-Date).AddSeconds(15)
while ($servicePidBefore -le 0 -and (Get-Date) -lt $pidDeadline) {
    $servicePidBefore = Get-ServiceProcessId
    if ($servicePidBefore -le 0) { Start-Sleep -Milliseconds 100 }
}
if ($servicePidBefore -le 0) { throw 'M5C could not resolve the original service PID.' }

$serviceStartTicksBefore = Get-ProcessStartTicks -ProcessId $servicePidBefore
$initialStatus = Wait-InitialReady -ExpectedPid $servicePidBefore

Write-Host "Ready                : $($initialStatus.Ready)"
Write-Host "Session              : $($initialStatus.SessionId)"
Write-Host "Account              : $($initialStatus.AccountName)"
Write-Host "Target               : $($initialStatus.TargetProfileId)"
Write-Host "Original watchdog PID: $servicePidBefore"
Write-Host "Watchdog startTicks  : $serviceStartTicksBefore"
Write-Host "SCM first restart    : $RestartDelayMs ms"

$logLineBoundary = if (Test-Path $serviceLog) { @(Get-Content $serviceLog).Count } else { 0 }

Write-Host ''
Write-Host 'ACTIVE M5C DOUBLE-DEATH BOUNDARY' -ForegroundColor Yellow
Write-Host 'Do not suspend/hibernate, close the lid, start a workload, close this shell, or kill anything manually.' -ForegroundColor Yellow
$confirm = Read-Host "Type exactly $userToken to continue"

if ($confirm -cne $userToken) {
    Restore-M4Baseline
    throw 'M5C cancelled before any fan write.'
}

try {
    Write-Host ''
    Write-Host 'Step 4: arm delayed independent safety fallback, then acquire durable OWNED 30/30...' -ForegroundColor Cyan

    # Arm the independent safety process before launching the controller. If the
    # parent shell disappears after the write, the fallback can neutralize the
    # exact journal-bound controller and/or start the already-qualified watchdog.
    $failsafe = Start-DelayedFailsafe
    Write-Host "Emergency fallback PID: $($failsafe.Id)"
    Write-Host "Emergency delay       : $FailsafeDelaySeconds s"

    $controller = Start-Process -FilePath 'dotnet' -ArgumentList @(
        $cli,
        '--8c40-m5a-controller-death-arm',
        '--8c40-m5a-token',
        $ownedArmToken,
        '--8c40-m5a-ready-path',
        $readyPath,
        '--modules-dir',
        $modulesDir
    ) -PassThru -NoNewWindow

    $readyDeadline = (Get-Date).AddSeconds(45)
    while (-not (Test-Path $readyPath)) {
        if ($controller.HasExited) {
            $controller.WaitForExit()
            $controller.Refresh()
            throw "M5C controller exited before READY. ExitCode=$($controller.ExitCode)."
        }
        if ((Get-Date) -gt $readyDeadline) { throw 'Timed out waiting for M5C READY.' }
        Start-Sleep -Milliseconds 100
    }

    $ready = Get-Content $readyPath -Raw | ConvertFrom-Json
    $controllerStartTicks = [long]$controller.StartTime.ToUniversalTime().Ticks

    Write-Host 'M5C READY marker:' -ForegroundColor Green
    Get-Content $readyPath

    if ([int]$ready.SchemaVersion -ne 1 -or
        $ready.Gate -cne 'M5A' -or
        $ready.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        [int]$ready.ProcessId -ne $controller.Id -or
        [long]$ready.ProcessStartUtcTicks -ne $controllerStartTicks -or
        $ready.Authority -cne 'Custom' -or
        [int]$ready.CpuSetpoint -ne 30 -or
        [int]$ready.GpuSetpoint -ne 30 -or
        [int]$ready.CpuRpm -le 0 -or
        [int]$ready.GpuRpm -le 0 -or
        [int]$ready.MaxFan -ne 0 -or
        [int]$ready.FanSwitch -ne 0 -or
        $ready.Ack -cne 'backend-ec+tachs+watchdog-owned') {
        throw 'M5C READY does not prove exact healthy OWNED 30/30.'
    }

    if (-not (Test-Path $journalPath)) { throw 'M5C READY exists but durable journal is missing.' }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal -Journal $journal -ControllerPid $controller.Id -ControllerStartTicks $controllerStartTicks

    Write-Host "Journal phase        : $($journal.Phase)"
    Write-Host "Journal generation   : $($journal.Generation)"
    Write-Host "Journal controller   : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "Journal target       : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

    if ((Get-ServiceProcessId) -ne $servicePidBefore) { throw 'Watchdog PID changed before M5C fault boundary.' }
    if ((Get-ProcessStartTicks -ProcessId $servicePidBefore) -ne $serviceStartTicksBefore) { throw 'Watchdog creation time changed before M5C fault boundary.' }

    $watchdogProcess = [System.Diagnostics.Process]::GetProcessById($servicePidBefore)
    if ($watchdogProcess.HasExited) { throw 'Original watchdog exited before M5C fault injection.' }

    $watchdogHandleStartTicks = [long]$watchdogProcess.StartTime.ToUniversalTime().Ticks
    if ($watchdogHandleStartTicks -ne $serviceStartTicksBefore) {
        throw 'M5C watchdog process handle does not match the validated original creation time.'
    }

    $failsafe.Refresh()
    if ($failsafe.HasExited) {
        throw 'M5C delayed safety fallback exited before the fault boundary.'
    }

    if (Test-FailsafeTakeover) { throw 'M5C failsafe fired before the fault boundary.' }

    Write-Host ''
    Write-Host 'Step 5: DOUBLE-KILL original watchdog then exact controller...' -ForegroundColor Yellow

    $faultWatch = [Diagnostics.Stopwatch]::StartNew()
    $watchdogKillTick = [Diagnostics.Stopwatch]::GetTimestamp()
    $watchdogProcess.Kill()
    $controllerKillTick = [Diagnostics.Stopwatch]::GetTimestamp()
    $controller.Kill()

    $killDeltaMs = (($controllerKillTick - $watchdogKillTick) * 1000.0) / [Diagnostics.Stopwatch]::Frequency
    Write-Host ("Kill issue delta      : {0:N3} ms" -f $killDeltaMs)

    if ($killDeltaMs -gt $MaxKillDeltaMs) {
        throw "M5C kill issue delta $([Math]::Round($killDeltaMs,3)) ms exceeds $MaxKillDeltaMs ms."
    }

    $deadDeadline = (Get-Date).AddSeconds(2)
    while ((-not $watchdogProcess.HasExited -or -not $controller.HasExited) -and (Get-Date) -lt $deadDeadline) {
        Start-Sleep -Milliseconds 10
        $watchdogProcess.Refresh()
        $controller.Refresh()
    }

    if (-not $watchdogProcess.HasExited -or -not $controller.HasExited) {
        throw 'M5C did not confirm both original processes dead within 2 s.'
    }

    if (-not (Wait-ServiceAbsent -Milliseconds 1200)) {
        throw 'M5C never observed the watchdog service absent before replacement startup.'
    }

    Write-Host "Original watchdog dead: PID=$servicePidBefore startTicks=$serviceStartTicksBefore"
    Write-Host "Original controller dead: PID=$($controller.Id) startTicks=$controllerStartTicks"

    Write-Host ''
    Write-Host 'Step 6: prove both original recovery domains are gone before replacement...' -ForegroundColor Cyan

    if ((Get-ServiceProcessId) -ne 0) { throw 'Replacement watchdog appeared before pre-restart proof.' }

    $postDeath = Read-8C40Setpoint
    $preRestartElapsedMs = [int][Math]::Round($faultWatch.Elapsed.TotalMilliseconds)

    Write-Host "Post-death EC        : $($postDeath.Raw)"
    Write-Host "Pre-restart elapsed  : $preRestartElapsedMs ms"

    if ($preRestartElapsedMs -ge ($RestartDelayMs - 500)) {
        throw "Pre-restart evidence was collected too late for the configured SCM delay."
    }
    if ($postDeath.Cpu -ne 30 -or $postDeath.Gpu -ne 30) {
        throw "Expected real owned 30/30 while both originals were dead; observed $($postDeath.Cpu)/$($postDeath.Gpu)."
    }
    if (-not (Test-Path $journalPath)) { throw 'Durable OWNED journal disappeared before replacement startup.' }

    $retained = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-OwnedJournal -Journal $retained -ControllerPid $controller.Id -ControllerStartTicks $controllerStartTicks

    if ((Get-ServiceProcessId) -ne 0) { throw 'Replacement watchdog appeared during pre-restart EC/journal proof.' }
    if (Test-FailsafeTakeover) { throw 'Emergency fallback fired; autonomous SCM M5C qualification is not proven.' }

    Write-Host 'Pre-restart proof     : both originals dead + service absent + EC 30/30 + exact OWNED journal retained.' -ForegroundColor Green

    Write-Host ''
    Write-Host 'Step 7: require distinct SCM replacement + startup recovery...' -ForegroundColor Cyan

    $replacement = Wait-ReplacementRecovery -OriginalPid $servicePidBefore -Seconds $RecoveryTimeoutSeconds
    if ($null -eq $replacement) {
        throw "No distinct replacement watchdog reached RestoredFirmware within $RecoveryTimeoutSeconds s."
    }

    $servicePidAfter = [int]$replacement.Pid
    Write-Host "Replacement PID      : $servicePidAfter"
    Write-Host "Replacement startTicks: $($replacement.StartTicks)"
    Write-Host "Startup recovery     : $($replacement.Status.RecoveryDisposition)"
    Write-Host "Startup detail       : $($replacement.Status.Detail)"

    if ($servicePidAfter -eq $servicePidBefore) { throw 'Replacement watchdog reused original PID.' }
    if (-not (Wait-ForJournalGone -Seconds 3)) { throw 'Replacement reported RestoredFirmware but journal remains.' }

    $final = Read-8C40Setpoint
    Write-Host "Independent final EC : $($final.Raw)"

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        throw "M5C final EC is $($final.Cpu)/$($final.Gpu), expected FF/FF."
    }
    if (Test-FailsafeTakeover) { throw 'Emergency fallback fired; run is safe but not an M5C PASS.' }

    $newLog = @()
    if (Test-Path $serviceLog) {
        $allLog = @(Get-Content $serviceLog)
        $newLog = @($allLog | Select-Object -Skip $logLineBoundary)
    }

    Write-Host ''
    Write-Host 'M5C watchdog/service evidence:' -ForegroundColor Cyan
    $newLog | Select-Object -Last 100

    $startupRecoveryEvidence = $newLog | Where-Object {
        $_ -match 'M4 STARTUP RECOVERY disposition=RestoredFirmware' -and
        $_ -match 'journalRetained=False'
    }

    if (-not $startupRecoveryEvidence) {
        throw 'Final state is safe but fresh service log lacks startup RestoredFirmware/journalRetained=False evidence.'
    }

    $pass = $true
    Write-Host ''
    Write-Host 'PASS: HP 8C40 M5C OWNED double-death recovery completed.' -ForegroundColor Green
    Write-Host 'Proven: OWNED 30/30 -> watchdog death -> exact controller death within bound -> both absent while EC remained 30/30 + journal retained -> distinct SCM replacement RestoredFirmware -> journal cleared -> final FF/FF.' -ForegroundColor Green
}
catch {
    $failure = $_.Exception.Message
    Show-Diagnostics
}
finally {
    if ($failsafe -and -not $failsafe.HasExited) {
        Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
        try { [void]$failsafe.WaitForExit(3000) } catch {}
    }

    if ($watchdogProcess) { $watchdogProcess.Dispose() }

    if ($controller -and -not $controller.HasExited) {
        Write-Warning 'M5C cleanup is terminating the exact qualification controller.'
        Stop-Process -Id $controller.Id -Force -ErrorAction SilentlyContinue
        try { [void]$controller.WaitForExit(3000) } catch {}
    }

    if (Test-Path $journalPath) {
        if ((Get-ServiceProcessId) -le 0) {
            Write-Warning 'Cleanup: journal remains and no watchdog is running; starting the already-qualified LocalSystem recovery service.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
        [void](Wait-ForJournalGone -Seconds $RecoveryTimeoutSeconds)
    }

    $firmwareSafe = $false
    if (-not (Test-Path $journalPath)) {
        try {
            $cleanup = Read-8C40Setpoint
            Write-Host "Post-test EC check     : $($cleanup.Raw)"
            $firmwareSafe = $cleanup.Cpu -eq 255 -and $cleanup.Gpu -eq 255
        }
        catch {
            Write-Warning "Final read-only EC check failed: $($_.Exception.Message)"
        }
    }

    if ($firmwareSafe) {
        try {
            Restore-M4Baseline
            Write-Host 'M4 qualification service baseline restored: Manual/stopped; temporary M5C SCM actions removed.' -ForegroundColor Green
        }
        catch {
            if ($pass) {
                $pass = $false
                $failure = "M5C recovery passed, but service-baseline cleanup failed: $($_.Exception.Message)"
            }
            else {
                Write-Warning "Firmware is safe, but service-baseline cleanup failed: $($_.Exception.Message)"
            }
        }
    }
    elseif (Test-Path $journalPath) {
        Write-Host ''
        Write-Host 'CRITICAL: durable M5C ownership evidence remains. It was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Do not reinstall the service or run another fan-write gate until this state is inspected/recovered.' -ForegroundColor Red
    }
}

if (-not $pass) {
    throw "M5C FAILED: $failure"
}
