param(
    [ValidateRange(10, 100)][int]$MaxKillDeltaMs = 50,
    [ValidateRange(3000, 10000)][int]$RestartDelayMs = 5000,
    [ValidateRange(90, 300)][int]$FailsafeDelaySeconds = 120,
    [ValidateRange(10, 40)][int]$RecoveryTimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM4'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath = Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath = Join-Path $serviceRoot 'state\lease.json'
$serviceLog = Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$failsafeLog = Join-Path $serviceRoot ("logs\watchdog-m5e-failsafe-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd-HHmmss'))
$localRoot = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath = Join-Path $localRoot 'm5e-8c40-write-armed.ready.json'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'
$failsafeScript = Join-Path $PSScriptRoot 'watchdog-m5e-service-failsafe-8c40.ps1'
$userToken = '8C40-M5E-WRITE-ARMED-DOUBLE-DEATH30'
$childToken = '8C40-M5D-WRITE-ARMED-CRASH30'

$controller = $null
$watchdogProcess = $null
$failsafe = $null
$pass = $false
$failure = $null
$servicePidBefore = 0
$serviceStartTicksBefore = 0L
$serviceSetupTouched = $false
$logLineBoundary = 0

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'M5E must be run from an elevated PowerShell.'
    }
}

function Read-8C40Setpoint {
    $output = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    $line = ($output -split "[\r\n]+" | Where-Object { $_ -match '^setpoint CPU=' } | Select-Object -Last 1)
    if (-not $line) { throw "Could not parse HP 8C40 setpoint probe. Raw output: $output" }
    $match = [regex]::Match($line, '^setpoint CPU=(\d+) GPU=(\d+)$')
    if (-not $match.Success) { throw "Could not parse HP 8C40 setpoints from: $line" }
    [pscustomobject]@{ Cpu=[int]$match.Groups[1].Value; Gpu=[int]$match.Groups[2].Value; Raw=$line }
}

function Get-ServiceProcessId {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.State -ne 'Running') { return 0 }
    return [int]$svc.ProcessId
}

function Get-ProcessStartTicks {
    param([int]$ProcessId)
    $candidate = [System.Diagnostics.Process]::GetProcessById($ProcessId)
    try { return [long]$candidate.StartTime.ToUniversalTime().Ticks }
    finally { $candidate.Dispose() }
}

function Test-WriteArmedPhase {
    param($Phase)
    if ($null -eq $Phase) { return $false }
    if ($Phase -is [string]) { return ($Phase -ceq 'WriteArmed' -or $Phase -ceq '1') }
    try { return ([int]$Phase -eq 1) } catch { return $false }
}

function Assert-WriteArmedJournal {
    param($Journal,[int]$ControllerProcessId,[long]$ControllerStartTicks)
    if ([int]$Journal.SchemaVersion -ne 2 -or
        $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not (Test-WriteArmedPhase -Phase $Journal.Phase) -or
        [long]$Journal.Generation -ne 2 -or
        [int]$Journal.Controller.ProcessId -ne $ControllerProcessId -or
        [long]$Journal.Controller.ProcessStartUtcTicks -ne $ControllerStartTicks -or
        $null -ne $Journal.PreviousOwned -or
        [int]$Journal.Pending.Cpu -ne 30 -or
        [int]$Journal.Pending.Gpu -ne 30 -or
        $null -ne $Journal.Owned) {
        throw 'M5E journal is not exact generation-2 WRITE_ARMED pending 30/30 with null PreviousOwned/Owned and exact controller identity.'
    }
}

function Wait-InitialReady {
    param([int]$ExpectedPid)
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statusPath) {
            try {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json
                if ([int]$status.ProcessId -eq $ExpectedPid -and $status.Ready -and -not $status.Blocked -and
                    [int]$status.SessionId -eq 0 -and $status.AccountName -match 'SYSTEM$' -and
                    $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                    $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                    $status.RecoveryDisposition -ceq 'Ready') { return $status }
            } catch {}
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Timed out waiting for original exact-target M4 Ready state.'
}

function Wait-ServiceAbsent {
    param([int]$Milliseconds)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $Milliseconds) {
        if ((Get-ServiceProcessId) -eq 0) { return $true }
        Start-Sleep -Milliseconds 25
    }
    return $false
}

function Wait-ReplacementRecovery {
    param([int]$OriginalPid,[int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $currentPid = Get-ServiceProcessId
        if ($currentPid -gt 0 -and $currentPid -ne $OriginalPid -and (Test-Path $statusPath)) {
            try {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json
                if ([int]$status.ProcessId -eq $currentPid -and $status.Ready -and -not $status.Blocked -and
                    [int]$status.SessionId -eq 0 -and $status.AccountName -match 'SYSTEM$' -and
                    $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                    $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                    $status.RecoveryDisposition -ceq 'RestoredFirmware') {
                    Start-Sleep -Milliseconds 300
                    if ((Get-ServiceProcessId) -eq $currentPid) {
                        return [pscustomobject]@{ Pid=$currentPid; StartTicks=(Get-ProcessStartTicks -ProcessId $currentPid); Status=$status }
                    }
                }
            } catch {}
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Wait-ForJournalGone {
    param([int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Test-Path $journalPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    return (-not (Test-Path $journalPath))
}

function Configure-M5ERecoveryPolicy {
    & sc.exe failure $serviceName reset= 86400 actions= "restart/$RestartDelayMs/restart/5000/restart/10000" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not configure M5E SCM recovery actions; sc.exe exit=$LASTEXITCODE." }
    & sc.exe failureflag $serviceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not enable M5E SCM failure actions; sc.exe exit=$LASTEXITCODE." }
    $qfailure = (& sc.exe qfailure $serviceName 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Could not query M5E SCM recovery policy; sc.exe exit=$LASTEXITCODE." }
    if ($qfailure -notmatch ("(?s){0}\s*ms.*5000\s*ms.*10000\s*ms" -f $RestartDelayMs)) {
        throw "M5E SCM policy does not contain ordered $RestartDelayMs/5000/10000 ms restarts."
    }
}

function Start-DelayedFailsafe {
    if (-not (Test-Path $failsafeScript)) { throw "M5E delayed failsafe is missing: $failsafeScript" }
    Remove-Item $failsafeLog -Force -ErrorAction SilentlyContinue
    $process = Start-Process powershell.exe -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,'-LogPath',$failsafeLog
    ) -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) { throw 'M5E delayed failsafe exited before controller launch.' }
    return $process
}

function Test-FailsafeTakeover {
    if (-not (Test-Path $failsafeLog)) { return $false }
    $text = Get-Content $failsafeLog -Raw
    return ($text -match 'M5E FAILSAFE TAKEOVER:' -or $text -match 'M5E FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M5E FAILSAFE SERVICE-START:' -or $text -match 'M5E FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M5E FAILSAFE STARTED:' -or $text -match 'M5E FAILSAFE RECOVERED:')
}

function Restore-M4Baseline {
    if (Test-Path $journalPath) { throw 'M5E refuses service reinstall while durable lease evidence remains.' }
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')
    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    if ($svc.StartType -ne 'Manual' -or $svc.Status -ne 'Stopped') {
        throw "M5E baseline restore failed: StartType=$($svc.StartType) Status=$($svc.Status)."
    }
}

function Show-Diagnostics {
    Write-Host ''
    if (Test-Path $readyPath) { Write-Host 'M5E READY:' -ForegroundColor Cyan; Get-Content $readyPath }
    if (Test-Path $statusPath) { Write-Host 'M4 status:' -ForegroundColor Cyan; Get-Content $statusPath }
    if (Test-Path $journalPath) { Write-Host 'M4 journal:' -ForegroundColor Yellow; Get-Content $journalPath }
    if (Test-Path $failsafeLog) { Write-Host 'M5E failsafe:' -ForegroundColor Cyan; Get-Content $failsafeLog }
    if (Test-Path $serviceLog) { Write-Host 'M4 log:' -ForegroundColor Cyan; Get-Content $serviceLog | Select-Object -Last 140 }
    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator
Write-Host 'VictusFanControl - HP 8C40 M5E WRITE_ARMED DOUBLE-DEATH RECOVERY' -ForegroundColor Cyan
Write-Host 'Real WMI+EC+dual-tach ACK is held before Commit; watchdog then controller are force-killed back-to-back.' -ForegroundColor Yellow
Write-Host 'Parent shell has no direct HP/WMI restore authority.' -ForegroundColor Yellow

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) { throw "M5E refused while '$name' is running." }
}

Write-Host 'Step 1: build + regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($invariant in @(
    'test-watchdog-m5a-8c40-invariants.ps1',
    'test-watchdog-m5b-8c40-invariants.ps1',
    'test-watchdog-m5c-preflight-invariants.ps1',
    'test-watchdog-m5c-double-death-invariants.ps1',
    'test-watchdog-m5d-write-armed-invariants.ps1'
)) {
    & (Join-Path $PSScriptRoot $invariant)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

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

Write-Host 'Step 2: clean baseline...' -ForegroundColor Cyan
$baseline = Read-8C40Setpoint
Write-Host $baseline.Raw
if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) { throw "M5E requires FF/FF; observed $($baseline.Cpu)/$($baseline.Gpu)." }
if (Test-Path $journalPath) { Get-Content $journalPath; throw 'M5E refuses an existing durable journal.' }

try {
    Write-Host 'Step 3: install service + temporary 5s SCM recovery window...' -ForegroundColor Cyan
    $serviceSetupTouched = $true
    & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')
    Configure-M5ERecoveryPolicy

    Remove-Item $statusPath -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $localRoot | Out-Null
    Remove-Item $readyPath -Force -ErrorAction SilentlyContinue
    Start-Service -Name $serviceName

    $deadline = (Get-Date).AddSeconds(15)
    while ($servicePidBefore -le 0 -and (Get-Date) -lt $deadline) {
        $servicePidBefore = Get-ServiceProcessId
        if ($servicePidBefore -le 0) { Start-Sleep -Milliseconds 100 }
    }
    if ($servicePidBefore -le 0) { throw 'M5E could not resolve original watchdog PID.' }

    $serviceStartTicksBefore = Get-ProcessStartTicks -ProcessId $servicePidBefore
    $initialStatus = Wait-InitialReady -ExpectedPid $servicePidBefore
    Write-Host "Service PID=$servicePidBefore startTicks=$serviceStartTicksBefore account=$($initialStatus.AccountName) target=$($initialStatus.TargetProfileId)"
    $logLineBoundary = if (Test-Path $serviceLog) { @(Get-Content $serviceLog).Count } else { 0 }

    Write-Host 'ACTIVE M5E BOUNDARY. Do not suspend, close lid, start load, close shell, or kill anything manually.' -ForegroundColor Yellow
    $confirm = Read-Host "Type exactly $userToken to continue"
    if ($confirm -cne $userToken) { throw 'M5E cancelled before any fan write.' }

    Write-Host 'Step 4: arm fallback and launch M5D pre-Commit child...' -ForegroundColor Cyan
    $failsafe = Start-DelayedFailsafe
    Write-Host "Emergency fallback PID=$($failsafe.Id), delay=$FailsafeDelaySeconds s"

    $controller = Start-Process -FilePath 'dotnet' -ArgumentList @(
        $cli,'--8c40-m5d-write-armed-crash-controller','--8c40-m5d-token',$childToken,
        '--8c40-m5d-ready-path',$readyPath,'--modules-dir',$modulesDir
    ) -PassThru -NoNewWindow

    $readyDeadline = (Get-Date).AddSeconds(45)
    while (-not (Test-Path $readyPath)) {
        if ($controller.HasExited) { $controller.WaitForExit(); $controller.Refresh(); throw "M5E child exited before READY. ExitCode=$($controller.ExitCode)." }
        if ((Get-Date) -gt $readyDeadline) { throw 'Timed out waiting for M5E READY.' }
        Start-Sleep -Milliseconds 50
    }

    $ready = Get-Content $readyPath -Raw | ConvertFrom-Json
    $controllerStartTicks = [long]$controller.StartTime.ToUniversalTime().Ticks
    Get-Content $readyPath

    if ([int]$ready.SchemaVersion -ne 1 -or $ready.Gate -cne 'M5D' -or
        $ready.Stage -cne 'WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT' -or
        $ready.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        [int]$ready.ProcessId -ne $controller.Id -or [long]$ready.ProcessStartUtcTicks -ne $controllerStartTicks -or
        [int]$ready.CpuSetpoint -ne 30 -or [int]$ready.GpuSetpoint -ne 30 -or
        [int]$ready.CpuRpm -le 0 -or [int]$ready.GpuRpm -le 0 -or
        [int]$ready.MaxFan -ne 0 -or [int]$ready.FanSwitch -ne 0 -or
        $ready.Ack -cne 'real-wmi+ec+tachs;watchdog-commit-not-dispatched') {
        throw 'M5E READY does not prove exact post-WMI/pre-Commit 30/30.'
    }

    if (-not (Test-Path $journalPath)) { throw 'M5E READY exists but journal is missing.' }
    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-WriteArmedJournal -Journal $journal -ControllerProcessId $controller.Id -ControllerStartTicks $controllerStartTicks
    Write-Host "Journal phase=$($journal.Phase) generation=$($journal.Generation) pending=$($journal.Pending.Cpu)/$($journal.Pending.Gpu) PreviousOwned=null Owned=null"

    if ((Get-ServiceProcessId) -ne $servicePidBefore) { throw 'Watchdog PID changed before M5E fault boundary.' }
    if ((Get-ProcessStartTicks -ProcessId $servicePidBefore) -ne $serviceStartTicksBefore) { throw 'Watchdog creation time changed before M5E fault boundary.' }

    $preKillEc = Read-8C40Setpoint
    Write-Host "Independent pre-kill EC: $($preKillEc.Raw)"
    if ($preKillEc.Cpu -ne 30 -or $preKillEc.Gpu -ne 30) { throw 'M5E EC is not real 30/30 at WRITE_ARMED boundary.' }

    $preKillJournal = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-WriteArmedJournal -Journal $preKillJournal -ControllerProcessId $controller.Id -ControllerStartTicks $controllerStartTicks

    $watchdogProcess = [System.Diagnostics.Process]::GetProcessById($servicePidBefore)
    if ($watchdogProcess.HasExited) { throw 'Original watchdog exited before M5E injection.' }
    if ([long]$watchdogProcess.StartTime.ToUniversalTime().Ticks -ne $serviceStartTicksBefore) { throw 'Watchdog process handle creation time mismatch.' }
    if ($controller.HasExited) { throw 'M5E controller exited before injection.' }

    $failsafe.Refresh()
    if ($failsafe.HasExited) { throw 'M5E delayed fallback exited before injection.' }
    if (Test-FailsafeTakeover) { throw 'M5E delayed fallback fired before intentional double death.' }

    Write-Host 'Step 5: DOUBLE-KILL watchdog then controller with no sleep/probe between calls...' -ForegroundColor Yellow
    $faultWatch = [Diagnostics.Stopwatch]::StartNew()
    $watchdogKillTick = [Diagnostics.Stopwatch]::GetTimestamp()
    $watchdogProcess.Kill()
    $controllerKillTick = [Diagnostics.Stopwatch]::GetTimestamp()
    $controller.Kill()

    $killDeltaMs = (($controllerKillTick - $watchdogKillTick) * 1000.0) / [Diagnostics.Stopwatch]::Frequency
    Write-Host ("Kill issue delta: {0:N3} ms" -f $killDeltaMs)
    if ($killDeltaMs -gt $MaxKillDeltaMs) { throw "M5E kill delta exceeds $MaxKillDeltaMs ms." }

    $deadDeadline = (Get-Date).AddSeconds(2)
    while ((-not $watchdogProcess.HasExited -or -not $controller.HasExited) -and (Get-Date) -lt $deadDeadline) {
        Start-Sleep -Milliseconds 10
        $watchdogProcess.Refresh()
        $controller.Refresh()
    }
    if (-not $watchdogProcess.HasExited -or -not $controller.HasExited) { throw 'M5E did not confirm both originals dead.' }
    if (-not (Wait-ServiceAbsent -Milliseconds 1200)) { throw 'M5E never observed service absent before replacement.' }

    Write-Host 'Step 6: pre-restart causal proof...' -ForegroundColor Cyan
    if ((Get-ServiceProcessId) -ne 0) { throw 'Replacement appeared before pre-restart proof.' }

    $postDeath = Read-8C40Setpoint
    $preRestartElapsedMs = [int][Math]::Round($faultWatch.Elapsed.TotalMilliseconds)
    Write-Host "Post-death EC: $($postDeath.Raw); elapsed=$preRestartElapsedMs ms"

    if ($preRestartElapsedMs -ge ($RestartDelayMs - 500)) { throw 'M5E pre-restart proof was collected too late.' }
    if ($postDeath.Cpu -ne 30 -or $postDeath.Gpu -ne 30) { throw 'M5E expected 30/30 while both originals were dead.' }
    if (-not (Test-Path $journalPath)) { throw 'WRITE_ARMED journal disappeared before replacement startup.' }

    $retained = Get-Content $journalPath -Raw | ConvertFrom-Json
    Assert-WriteArmedJournal -Journal $retained -ControllerProcessId $controller.Id -ControllerStartTicks $controllerStartTicks
    if ((Get-ServiceProcessId) -ne 0) { throw 'Replacement appeared during pre-restart proof.' }
    if (Test-FailsafeTakeover) { throw 'Emergency fallback fired; autonomous SCM recovery not proven.' }

    Write-Host 'Pre-restart proof: both originals dead + service absent + EC 30/30 + exact WRITE_ARMED gen2 journal retained.' -ForegroundColor Green

    Write-Host 'Step 7: require distinct SCM replacement startup recovery...' -ForegroundColor Cyan
    $replacement = Wait-ReplacementRecovery -OriginalPid $servicePidBefore -Seconds $RecoveryTimeoutSeconds
    if ($null -eq $replacement) { throw 'No distinct replacement watchdog reached RestoredFirmware.' }
    Write-Host "Replacement PID=$($replacement.Pid) startTicks=$($replacement.StartTicks) recovery=$($replacement.Status.RecoveryDisposition)"
    Write-Host "Startup detail: $($replacement.Status.Detail)"
    if ([int]$replacement.Pid -eq $servicePidBefore) { throw 'M5E replacement reused original PID.' }
    if (-not (Wait-ForJournalGone -Seconds 3)) { throw 'Replacement reports RestoredFirmware but journal remains.' }

    $final = Read-8C40Setpoint
    Write-Host "Independent final EC: $($final.Raw)"
    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) { throw 'M5E final EC is not FF/FF.' }
    if (Test-FailsafeTakeover) { throw 'Emergency fallback fired; run cannot PASS.' }

    $newLog = @()
    if (Test-Path $serviceLog) { $newLog = @(@(Get-Content $serviceLog) | Select-Object -Skip $logLineBoundary) }
    $newLog | Select-Object -Last 120

    $prepare = $newLog | Where-Object { $_ -match ("WATCHDOG PREPARE ACK controller PID={0}" -f $controller.Id) }
    $intent = $newLog | Where-Object { $_ -match ("WATCHDOG WRITE_INTENT ACK controller PID={0}" -f $controller.Id) -and $_ -match 'target=30/30' }
    $commit = $newLog | Where-Object { $_ -match ("WATCHDOG COMMIT ACK controller PID={0}" -f $controller.Id) }
    $startup = $newLog | Where-Object { $_ -match 'M4 STARTUP RECOVERY disposition=RestoredFirmware' -and $_ -match 'journalRetained=False' -and $_ -match 'WRITE_ARMED' }

    if (-not $prepare -or -not $intent) { throw 'M5E fresh PREPARE/WRITE_INTENT evidence missing.' }
    if ($commit) { throw 'M5E found COMMIT for killed controller; pre-Commit boundary was not preserved.' }
    if (-not $startup) { throw 'M5E startup RestoredFirmware WRITE_ARMED evidence missing.' }

    $pass = $true
    Write-Host 'PASS: HP 8C40 M5E WRITE_ARMED double-death startup recovery completed.' -ForegroundColor Green
}
catch {
    $failure = $_.Exception.Message
    Show-Diagnostics
}
finally {
    if ($controller -and -not $controller.HasExited) {
        Write-Warning 'M5E cleanup: killing exact qualification controller so watchdog recovery owns cleanup.'
        Stop-Process -Id $controller.Id -Force -ErrorAction SilentlyContinue
        try { [void]$controller.WaitForExit(3000) } catch {}
    }

    if ($watchdogProcess) { $watchdogProcess.Dispose() }

    if (Test-Path $journalPath) {
        if ((Get-ServiceProcessId) -le 0) {
            Write-Warning 'M5E cleanup: journal remains and watchdog is absent; starting qualified recovery service.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
        [void](Wait-ForJournalGone -Seconds $RecoveryTimeoutSeconds)
    }

    $firmwareSafe = $false
    if (-not (Test-Path $journalPath)) {
        try {
            $cleanup = Read-8C40Setpoint
            Write-Host "Post-test EC check: $($cleanup.Raw)"
            $firmwareSafe = $cleanup.Cpu -eq 255 -and $cleanup.Gpu -eq 255
        } catch {
            Write-Warning "Final EC check failed: $($_.Exception.Message)"
        }
    }

    if ($firmwareSafe -and $failsafe) {
        $failsafe.Refresh()
        if (-not $failsafe.HasExited) {
            Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
            try { [void]$failsafe.WaitForExit(3000) } catch {}
            Write-Host 'Emergency M5E fallback cancelled only after FF/FF + cleared journal proof.' -ForegroundColor Green
        } elseif ($pass) {
            $pass = $false
            $failure = 'M5E reached safe state, but delayed fallback had already exited/reached its delay.'
        }
    } elseif ($failsafe -and -not $firmwareSafe) {
        Write-Warning "Firmware safety is not independently proven. M5E fallback PID $($failsafe.Id) remains armed."
    }

    if ($firmwareSafe -and $serviceSetupTouched) {
        try {
            Restore-M4Baseline
            Write-Host 'M4 baseline restored: Manual/stopped; temporary M5E SCM actions removed.' -ForegroundColor Green
        } catch {
            if ($pass) {
                $pass = $false
                $failure = "M5E recovery passed, but baseline cleanup failed: $($_.Exception.Message)"
            } else {
                Write-Warning "Firmware is safe, but baseline cleanup failed: $($_.Exception.Message)"
            }
        }
    } elseif (Test-Path $journalPath) {
        Write-Host 'CRITICAL: durable M5E WRITE_ARMED evidence remains. Do not run another fan-write gate.' -ForegroundColor Red
        Get-Content $journalPath
    }
}

if (-not $pass) { throw "M5E FAILED: $failure" }
