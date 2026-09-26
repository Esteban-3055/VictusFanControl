param(
    [ValidateRange(300, 600)]
    [int]$FailsafeDelaySeconds = 600,

    [ValidateRange(20, 120)]
    [int]$WakeAfterSeconds = 30
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$targetCycles = 5
$serviceName = 'VictusFanControlWatchdog'
$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$finalResultPath = Join-Path $root 'gate-g2.result'
$failsafeLog = Join-Path $root 'watchdog-gate-g2-failsafe.log'
$failsafeScript = Join-Path $PSScriptRoot 'watchdog-gate-b-failsafe.ps1'

$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogService'
$serviceStatusPath = Join-Path $serviceRoot 'state\gate-d.status.json'
$journalPath = Join-Path $serviceRoot 'state\lease.json'
$serviceLog = Join-Path $serviceRoot ("logs\watchdog-gate-d-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$app = Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$watchdogDll = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\bin\Release\net8.0-windows\VictusFanControl.Watchdog.dll'
$appLog = Join-Path (Join-Path $root 'logs') ("events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$proc = $null
$failsafe = $null
$pass = $false
$failure = $null
$servicePidBefore = 0
$guiProcessId = 0
$guiStartTicks = 0
$logLineBoundary = 0

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Gate G2 must be run from an elevated PowerShell.'
    }
}

function Assert-DefaultWatchdogOutputUnlocked {
    $defaultWatchdogDll = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\bin\Release\net8.0-windows\VictusFanControl.Watchdog.dll'

    if (-not (Test-Path $defaultWatchdogDll)) {
        return
    }

    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $defaultWatchdogDll,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch {
        throw "The repository watchdog DLL is locked by another process. Close the entire PowerShell window, open a fresh elevated PowerShell, return to the repository, git pull, and rerun Gate G2. Locked file: $defaultWatchdogDll"
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Read-EcState {
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

    [pscustomobject]@{
        Cpu = [int]$match.Groups[1].Value
        Gpu = [int]$match.Groups[2].Value
        Manual = $match.Groups[3].Value.ToUpperInvariant()
        Countdown = [int]$match.Groups[4].Value
        CpuRpm = [int]$match.Groups[5].Value
        GpuRpm = [int]$match.Groups[6].Value
        Raw = $line
    }
}

function Wait-ForFile {
    param(
        [string]$Path,
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Path $Path) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
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

function Get-ServiceStatusMarker {
    if (-not (Test-Path $serviceStatusPath)) {
        throw "Watchdog status marker is missing: $serviceStatusPath"
    }

    Get-Content $serviceStatusPath -Raw | ConvertFrom-Json
}

function Assert-ProductionServiceReady {
    param([int]$ExpectedPid = 0)

    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop

    if ($svc.State -cne 'Running') {
        throw "Watchdog service is not Running; state=$($svc.State)."
    }

    if ($svc.StartMode -notin @('Auto', 'Automatic')) {
        throw "Watchdog service is not Automatic; StartMode=$($svc.StartMode)."
    }

    if ($svc.StartName -notmatch 'LocalSystem|SYSTEM') {
        throw "Watchdog service is not LocalSystem; StartName=$($svc.StartName)."
    }

    $serviceProcessId = [int]$svc.ProcessId
    if ($serviceProcessId -le 0) {
        throw 'Running watchdog service has no valid PID.'
    }

    if ($ExpectedPid -gt 0 -and $serviceProcessId -ne $ExpectedPid) {
        throw "Watchdog PID changed during Gate G2: $ExpectedPid -> $serviceProcessId."
    }

    $status = Get-ServiceStatusMarker

    if (-not $status.Ready -or
        $status.Blocked -or
        [int]$status.SessionId -ne 0 -or
        $status.AccountName -notmatch 'SYSTEM$' -or
        [int]$status.ProcessId -ne $serviceProcessId) {
        throw "Watchdog status is not stable Ready/LocalSystem/Session0 for PID $serviceProcessId."
    }

    [pscustomobject]@{
        Pid = $serviceProcessId
        Service = $svc
        Status = $status
    }
}

function Assert-ScmRecoveryPolicy {
    $qfailure = (& sc.exe qfailure $serviceName 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not query watchdog SCM recovery policy; sc.exe exit=$LASTEXITCODE."
    }

    if ($qfailure -notmatch '(?s)1000\s*ms.*5000\s*ms.*10000\s*ms') {
        throw "Watchdog SCM recovery policy is not the required ordered 1 s / 5 s / 10 s sequence. Raw output: $qfailure"
    }

    Write-Host 'SCM recovery        : 1 s / 5 s / 10 s' -ForegroundColor Green
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

function Get-NewServiceLogLines {
    if (-not (Test-Path $serviceLog)) {
        return @()
    }

    $all = @(Get-Content $serviceLog)
    return @($all | Select-Object -Skip $logLineBoundary)
}

function Get-PowerEvents {
    param([datetime]$StartTime)

    @(
        Get-WinEvent -FilterHashtable @{
            LogName = 'System'
            Id = 42, 107
            StartTime = $StartTime.AddSeconds(-2)
        } -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProviderName -eq 'Microsoft-Windows-Kernel-Power'
        } |
        Sort-Object TimeCreated
    )
}

function Wait-ForPowerCycleEvidence {
    param(
        [datetime]$StartTime,
        [int]$Seconds = 12
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    $events = @()
    $sleepEvent = $null
    $resumeEvent = $null

    do {
        $events = @(Get-PowerEvents -StartTime $StartTime)
        $sleepEvent = @($events | Where-Object Id -eq 42) | Select-Object -First 1
        $resumeEvent = @($events | Where-Object Id -eq 107) | Select-Object -Last 1

        if ($null -ne $sleepEvent -and
            $null -ne $resumeEvent -and
            $resumeEvent.TimeCreated -ge $sleepEvent.TimeCreated) {
            break
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $sleepEvent -or
        $null -eq $resumeEvent -or
        $resumeEvent.TimeCreated -lt $sleepEvent.TimeCreated) {
        throw 'Gate G2 requires ordered Kernel-Power 42 -> 107 evidence for every cycle.'
    }

    [pscustomobject]@{
        Events = $events
        Sleep = $sleepEvent
        Resume = $resumeEvent
    }
}

function Assert-GuiIdentity {
    if ($null -eq $proc -or $proc.HasExited) {
        throw "Gate G2 GUI exited unexpectedly; expected persistent PID $guiProcessId."
    }

    if ([int]$proc.Id -ne $guiProcessId) {
        throw "Gate G2 GUI PID changed unexpectedly: $guiProcessId -> $($proc.Id)."
    }

    $currentStartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks
    if ($currentStartTicks -ne $guiStartTicks) {
        throw "Gate G2 GUI creation time changed unexpectedly for PID $guiProcessId."
    }
}

Assert-Administrator

Write-Host 'VictusFanControl - WATCHDOG GATE G2 (5/5 SAME-SESSION SUSPEND/RESUME)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test WILL suspend Windows five consecutive times.' -ForegroundColor Yellow
Write-Host 'The same VictusFanControl GUI process and the same LocalSystem watchdog process must survive all five normal cycles.' -ForegroundColor Yellow
Write-Host 'Automatic fan policy remains OFF; only the dedicated bounded test writes 30/30.' -ForegroundColor Yellow
Write-Host 'No parent-shell HP fan restore is part of the PASS path.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Save unrelated work. Keep OMEN Gaming Hub open with the normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and any normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        Write-Error "Refusing Gate G2 while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: verify S3, stale-shell locks, build and regressions...' -ForegroundColor Cyan
Assert-DefaultWatchdogOutputUnlocked

$availableSleep = (& powercfg.exe /a 2>&1 | Out-String)
Write-Host $availableSleep
if ($availableSleep -notmatch '\(S3\)') {
    throw 'Gate G2 requires classic S3 sleep on this validated target.'
}

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-gate-g0-clock-invariants.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-gate-g1-invariants.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-gate-g2-invariants.ps1')
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
Write-Host 'Step 2: read-only hardware baseline...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$baseline = Read-EcState
Write-Host "EC baseline         : $($baseline.Raw)"

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    throw "Gate G2 requires firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu)."
}

if (Test-Path $journalPath) {
    throw "Gate G2 requires the durable journal absent before service reinstall/start; found $journalPath."
}

Write-Host 'Durable journal     : absent before service reinstall' -ForegroundColor Green

Write-Host ''
$pre = Read-Host 'Type UNDERVOLT-OK after verifying the normal CPU undervolt in OMEN Gaming Hub'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Step 3: install/start production watchdog once for all five cycles...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-gate-d.ps1')

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
    'Running',
    [TimeSpan]::FromSeconds(15))

if (-not (Wait-ForFile -Path $serviceStatusPath -Seconds 15)) {
    throw 'Watchdog did not publish its startup status.'
}

$serviceReady = Assert-ProductionServiceReady
$servicePidBefore = [int]$serviceReady.Pid

Write-Host "Service PID         : $servicePidBefore"
Write-Host "Service Ready       : $($serviceReady.Status.Ready)"
Write-Host "Service Session     : $($serviceReady.Status.SessionId)"
Write-Host "Service Account     : $($serviceReady.Status.AccountName)"
Write-Host "Startup detail      : $($serviceReady.Status.Detail)"
Assert-ScmRecoveryPolicy

if (Test-Path $journalPath) {
    throw "Gate G2 baseline requires journal absent; found $journalPath"
}

$serviceBaselineEc = Read-EcState
Write-Host "EC after service    : $($serviceBaselineEc.Raw)"

if ($serviceBaselineEc.Cpu -ne 255 -or
    $serviceBaselineEc.Gpu -ne 255) {
    throw "Watchdog baseline is not firmware-owned FF/FF: $($serviceBaselineEc.Cpu)/$($serviceBaselineEc.Gpu)."
}

Write-Host ''
Write-Host 'The next confirmation authorizes five lifecycle cycles. Each cycle has one initial 30/30 ownership and one controlled post-recovery 30/30 re-entry.' -ForegroundColor Yellow
$confirm = Read-Host 'Type GATEG2 to continue'
if ($confirm -cne 'GATEG2') {
    Write-Host 'Cancelled before any Gate G2 fan write.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Get-ChildItem -Path $root -Filter 'gate-g2.*' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item $failsafeLog -Force -ErrorAction SilentlyContinue

$logLineBoundary = if (Test-Path $serviceLog) {
    @(Get-Content $serviceLog).Count
} else {
    0
}

Write-Host ''
Write-Host 'Step 4: arm one independent delayed emergency fallback before Gate G2 Custom ownership...' -ForegroundColor Cyan
$failsafe = Start-Process powershell.exe -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $failsafeScript,
    '-Cli', $cli,
    '-RepoRoot', $repoRoot,
    '-LogPath', $failsafeLog,
    '-DelaySeconds', $FailsafeDelaySeconds
) -WindowStyle Hidden -PassThru

Write-Host "Emergency fallback : PID=$($failsafe.Id), delay=$FailsafeDelaySeconds s"

try {
    Write-Host ''
    Write-Host 'Step 5: launch ONE Gate G2 GUI for all five cycles...' -ForegroundColor Cyan

    $proc = Start-Process -FilePath $app -ArgumentList @(
        '--gate-g2-suspend-repeat-test',
        '--gate-g2-test-token',
        '88F8-GATEG2-30'
    ) -PassThru

    $guiProcessId = [int]$proc.Id
    $guiStartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks

    Write-Host "GUI identity        : PID=$guiProcessId startTicks=$guiStartTicks"

    for ($cycle = 1; $cycle -le $targetCycles; $cycle++) {
        $readyPath = Join-Path $root ("gate-g2.cycle-{0}.ready" -f $cycle)
        $preSleepPath = Join-Path $root ("gate-g2.cycle-{0}.presleep" -f $cycle)
        $reentryPath = Join-Path $root ("gate-g2.cycle-{0}.reentry" -f $cycle)
        $cycleResultPath = Join-Path $root ("gate-g2.cycle-{0}.result" -f $cycle)

        Write-Host ''
        Write-Host ("===== GATE G2 CYCLE {0}/{1} =====" -f $cycle, $targetCycles) -ForegroundColor Cyan

        Assert-GuiIdentity

        if (-not (Wait-ForFile -Path $readyPath -Seconds 90)) {
            $finalDetail = if (Test-Path $finalResultPath) {
                Get-Content $finalResultPath -Raw
            } else {
                'no final Gate G2 result marker'
            }

            throw "Timed out waiting for Gate G2 cycle $cycle READY. $finalDetail"
        }

        $readyText = Get-Content $readyPath -Raw
        Write-Host "READY               : $readyText" -ForegroundColor Green

        if ($readyText -notmatch '^READY\|' -or
            $readyText -notmatch ("cycle={0}/{1}" -f $cycle, $targetCycles) -or
            $readyText -notmatch 'ack=backend-ec\+tachs\+watchdog-owned' -or
            $readyText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
            $readyText -notmatch ("guiPid={0}" -f $guiProcessId)) {
            throw "Gate G2 cycle $cycle READY marker is incomplete or identifies the wrong process."
        }

        if (-not (Test-Path $journalPath)) {
            throw "Gate G2 cycle $cycle READY exists but durable OWNED journal is missing."
        }

        $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
        $journalOwned = Test-JournalOwnedPhase -Phase $journal.Phase

        Write-Host "Journal phase       : $($journal.Phase)"
        Write-Host "Journal generation  : $($journal.Generation)"
        Write-Host "Journal controller  : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
        Write-Host "Journal target      : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

        if (-not $journalOwned -or
            [int]$journal.Controller.ProcessId -ne $guiProcessId -or
            [long]$journal.Controller.ProcessStartUtcTicks -ne $guiStartTicks -or
            [int]$journal.Owned.Cpu -ne 30 -or
            [int]$journal.Owned.Gpu -ne 30) {
            throw "Gate G2 cycle $cycle READY is not backed by exact durable OWNED 30/30 bound to the persistent GUI identity."
        }

        [void](Assert-ProductionServiceReady -ExpectedPid $servicePidBefore)

        # IMPORTANT: no parent Read-EcState call occurs from READY until the
        # external S3 dispatch. Custom ownership is verified through the
        # production backend ACK plus exact durable OWNED journal instead.
        $eventWindowStart = Get-Date

        Write-Host ("Requesting S3 for cycle {0}/{1}; wake request about {2} s..." -f $cycle, $targetCycles, $WakeAfterSeconds) -ForegroundColor Cyan

        $sleepOutput = ('' | & dotnet $watchdogDll --gate-g0-clock-probe --wake-after-seconds $WakeAfterSeconds --minimum-excluded-seconds 5 --gate-g0-auto-s3-token '88F8-G0-AUTO-S3' 2>&1 | Out-String)
        $sleepExit = $LASTEXITCODE

        Write-Host $sleepOutput

        if ($sleepExit -ne 0 -or
            $sleepOutput -notmatch 'SUSPEND_DISPATCH' -or
            $sleepOutput -notmatch 'RESUME_RETURN' -or
            $sleepOutput -notmatch 'Gate G0 production clock measurement: PASS') {
            throw "Gate G2 cycle $cycle external S3 helper did not complete a validated suspend/resume return. ExitCode=$sleepExit"
        }

        if (-not (Wait-ForFile -Path $preSleepPath -Seconds 20)) {
            throw "Gate G2 cycle $cycle did not persist its pre-sleep handoff marker."
        }

        $preSleepText = Get-Content $preSleepPath -Raw
        Write-Host "Pre-sleep           : $preSleepText"

        $handlerMatch = [regex]::Match(
            $preSleepText,
            'handlerMs=([0-9]+(?:\.[0-9]+)?)\|budgetMs=1800')

        if ($preSleepText -notmatch '^PASS\|' -or
            $preSleepText -notmatch ("cycle={0}/{1}" -f $cycle, $targetCycles) -or
            $preSleepText -notmatch 'wasCustom=True' -or
            $preSleepText -notmatch 'backendAck=True' -or
            $preSleepText -notmatch 'authority=Firmware' -or
            $preSleepText -notmatch 'ec=255/255' -or
            $preSleepText -notmatch 'ecProof=production-backend-restore-ack' -or
            $preSleepText -notmatch 'watchdogRelease=True' -or
            $preSleepText -notmatch 'journalProof=watchdog-release-response' -or
            $preSleepText -notmatch 'journal=absent' -or
            $preSleepText -notmatch 'telemetry=Suspended' -or
            $preSleepText -notmatch 'resumeObservedBeforeProof=False' -or
            $preSleepText -notmatch 'acceptedResumesBeforeProof=0' -or
            -not $handlerMatch.Success -or
            [double]$handlerMatch.Groups[1].Value -gt 1800 -or
            $preSleepText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
            $preSleepText -notmatch ("guiPid={0}" -f $guiProcessId)) {
            throw "Gate G2 cycle $cycle pre-sleep marker does not prove a complete in-budget handoff before any accepted resume."
        }

        if (-not (Wait-ForFile -Path $cycleResultPath -Seconds 120)) {
            throw "Timed out waiting for Gate G2 cycle $cycle result."
        }

        $cycleResult = Get-Content $cycleResultPath -Raw
        Write-Host "Cycle result        : $cycleResult"

        if ($cycleResult -notmatch '^PASS\|' -or
            $cycleResult -notmatch ("cycle={0}/{1}" -f $cycle, $targetCycles) -or
            $cycleResult -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
            $cycleResult -notmatch ("guiPid={0}" -f $guiProcessId) -or
            $cycleResult -notmatch 'exactly one resume was accepted' -or
            $cycleResult -notmatch 'telemetry recovered to Healthy' -or
            $cycleResult -notmatch 'final authority=Firmware' -or
            $cycleResult -notmatch 'journal absent') {
            throw "Gate G2 cycle $cycle did not publish a complete PASS result."
        }

        if (-not (Test-Path $reentryPath)) {
            throw "Gate G2 cycle $cycle PASS is missing its controlled re-entry marker."
        }

        $reentryText = Get-Content $reentryPath -Raw
        Write-Host "Re-entry            : $reentryText"

        if ($reentryText -notmatch '^REENTRY\|' -or
            $reentryText -notmatch ("cycle={0}/{1}" -f $cycle, $targetCycles) -or
            $reentryText -notmatch 'authority=Custom' -or
            $reentryText -notmatch 'cpu=30\|gpu=30' -or
            $reentryText -notmatch 'ack=backend-ec\+tachs\+watchdog-owned' -or
            $reentryText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
            $reentryText -notmatch ("guiPid={0}" -f $guiProcessId)) {
            throw "Gate G2 cycle $cycle controlled re-entry marker is incomplete."
        }

        $power = Wait-ForPowerCycleEvidence -StartTime $eventWindowStart
        Write-Host ("Kernel-Power        : 42 at {0}; 107 at {1}" -f $power.Sleep.TimeCreated, $power.Resume.TimeCreated)

        [void](Assert-ProductionServiceReady -ExpectedPid $servicePidBefore)

        if ($cycle -lt $targetCycles) {
            Assert-GuiIdentity
            Write-Host ("Cycle {0}/{1}: PASS; same GUI PID {2}, same watchdog PID {3}. Continuing without restart." -f $cycle, $targetCycles, $guiProcessId, $servicePidBefore) -ForegroundColor Green
        }
    }

    if (-not (Wait-ForFile -Path $finalResultPath -Seconds 30)) {
        throw 'Gate G2 final result marker is missing after cycle 5.'
    }

    $finalResult = Get-Content $finalResultPath -Raw
    Write-Host ''
    Write-Host "Final Gate G2 result: $finalResult"

    if ($finalResult -notmatch '^PASS\|' -or
        $finalResult -notmatch 'cycles=5/5' -or
        $finalResult -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
        $finalResult -notmatch ("guiPid={0}" -f $guiProcessId)) {
        throw 'Gate G2 final result marker does not prove 5/5 on the original processes.'
    }

    $appExited = $false
    try {
        $appExited = $proc.WaitForExit(15000)
    }
    catch {
        $appExited = $proc.HasExited
    }

    if (-not $appExited) {
        throw 'Gate G2 published final PASS but the GUI did not exit within 15 s; refusing final out-of-band EC verification.'
    }

    [void](Assert-ProductionServiceReady -ExpectedPid $servicePidBefore)
    Assert-ScmRecoveryPolicy

    if (Test-Path $journalPath) {
        throw 'Gate G2 final PASS returned with a durable journal still present.'
    }

    $finalEc = Read-EcState
    Write-Host "Independent final EC: $($finalEc.Raw)"

    if ($finalEc.Cpu -ne 255 -or
        $finalEc.Gpu -ne 255) {
        throw "Gate G2 final EC is not FF/FF: $($finalEc.Cpu)/$($finalEc.Gpu)."
    }

    $newServiceLog = Get-NewServiceLogLines
    Write-Host ''
    Write-Host 'Watchdog log during all Gate G2 cycles:' -ForegroundColor Cyan
    $newServiceLog | Select-Object -Last 160 | ForEach-Object { Write-Host $_ }

    $forbidden = @(
        'OWNED heartbeat timeout',
        'WRITE_ARMED operation deadline',
        'RESTORING takeover deadline',
        'OwnershipAmbiguous',
        'GATE D FATAL',
        'GATE D MONITOR FAIL',
        'WATCHDOG OWNER LOSS:',
        'GATE D RECOVERY'
    )

    foreach ($pattern in $forbidden) {
        if ($newServiceLog | Select-String -SimpleMatch $pattern -Quiet) {
            throw "Gate G2 normal lifecycle produced forbidden watchdog recovery/failure evidence: '$pattern'."
        }
    }

    if ($failsafe.HasExited) {
        $failsafeDetail = if (Test-Path $failsafeLog) {
            Get-Content $failsafeLog -Raw
        } else {
            'no fallback log'
        }

        throw "Emergency fallback reached its execution boundary before Gate G2 safety was independently proven. $failsafeDetail"
    }

    $pass = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if (-not $pass -and
        $proc -and
        -not $proc.HasExited) {
        Write-Warning "Gate G2 failed while persistent GUI PID $($proc.Id) is alive; force-killing only that exact GUI so the independent production watchdog owns any required crash cleanup."
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { [void]$proc.WaitForExit(5000) } catch {}
    }

    if (Test-Path $journalPath) {
        Write-Host 'Waiting up to 20 s for watchdog cleanup of any retained Gate G2 lease...' -ForegroundColor Cyan
        [void](Wait-ForJournalGone -Seconds 20)
    }

    $firmwareSafe = $false

    if (-not (Test-Path $journalPath)) {
        try {
            $cleanupEc = Read-EcState
            Write-Host "Post-test EC check  : $($cleanupEc.Raw)"
            $firmwareSafe =
                $cleanupEc.Cpu -eq 255 -and
                $cleanupEc.Gpu -eq 255
        }
        catch {
            Write-Warning "Could not complete final read-only EC check: $($_.Exception.Message)"
        }
    }

    if ($firmwareSafe -and
        $failsafe -and
        -not $failsafe.HasExited) {
        Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
        Write-Host 'Emergency fallback cancelled only after journal absence + independent EC FF/FF were proven.' -ForegroundColor Green
    }
    elseif ($failsafe -and
            -not $firmwareSafe -and
            -not $failsafe.HasExited) {
        Write-Warning "Firmware safety is not yet independently proven. Emergency fallback PID $($failsafe.Id) remains armed; do not close PowerShell or power-cycle during its $FailsafeDelaySeconds s window."
    }
}

if (-not $pass) {
    Write-Host ''
    Write-Host 'Gate G2 did NOT pass.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $finalResultPath) {
        Write-Host "Final application result: $(Get-Content $finalResultPath -Raw)"
    }

    Get-ChildItem -Path $root -Filter 'gate-g2.*' -File -ErrorAction SilentlyContinue |
        Sort-Object Name |
        ForEach-Object {
            Write-Host ("{0}: {1}" -f $_.Name, (Get-Content $_.FullName -Raw))
        }

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent watchdog log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 180
    }

    if (Test-Path $appLog) {
        Write-Host ''
        Write-Host 'Recent Gate G lifecycle log:' -ForegroundColor Cyan
        Get-Content $appLog |
            Select-String -Pattern 'GATE G1|GATE G2|Fan authority|Suspend detected|Resume detected|Recovery completed|Duplicate resume' |
            Select-Object -Last 220 |
            ForEach-Object { Write-Host $_.Line }
    }

    exit 121
}

Write-Host ''
Write-Host 'Step 6: verify OMEN Gaming Hub undervolt after all five cycles...' -ForegroundColor Cyan
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
Write-Host 'PASS: Gate G2 completed 5/5 consecutive full watchdog suspend/resume cycles without intentional GUI/service restart.' -ForegroundColor Green
Write-Host "Stable identities: GUI PID=$guiProcessId startTicks=$guiStartTicks; watchdog PID=$servicePidBefore." -ForegroundColor Green
Write-Host 'Every cycle proved exact OWNED 30/30, pre-sleep Firmware + FF/FF + journal absent, one accepted resume, Healthy recovery, controlled re-entry and final Firmware handoff.' -ForegroundColor Green
Write-Host 'No watchdog timeout/recovery/fatal evidence occurred, fallback did not run, final journal is absent, final EC is FF/FF, SCM remains 1/5/10 and OGH undervolt is SAME.' -ForegroundColor Green
exit 0
