param(
    [ValidateRange(120, 600)]
    [int]$FailsafeDelaySeconds = 240,

    [ValidateRange(20, 120)]
    [int]$WakeAfterSeconds = 30
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdog'
$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath = Join-Path $root 'gate-g1.ready'
$preSleepPath = Join-Path $root 'gate-g1.presleep'
$reentryPath = Join-Path $root 'gate-g1.reentry'
$resultPath = Join-Path $root 'gate-g1.result'
$failsafeLog = Join-Path $root 'watchdog-gate-g1-failsafe.log'
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
$readyReached = $false
$pass = $false
$failure = $null
$servicePidBefore = 0
$logLineBoundary = 0
$eventWindowStart = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Gate G1 must be run from an elevated PowerShell.'
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
        throw "The repository watchdog DLL is locked by another process. This can happen if the old reflection-based G0 probe was run in the current Windows PowerShell process. Close the ENTIRE PowerShell window, open a fresh elevated PowerShell, return to the repository, git pull, and rerun Gate G1. Locked file: $defaultWatchdogDll"
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
    param(
        [int]$ExpectedPid = 0
    )

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

    # PowerShell variable names are case-insensitive and PID is a
    # built-in read-only automatic variable. Never use pid as a local name.
    $serviceProcessId = [int]$svc.ProcessId
    if ($serviceProcessId -le 0) {
        throw 'Running watchdog service has no valid PID.'
    }

    if ($ExpectedPid -gt 0 -and $serviceProcessId -ne $ExpectedPid) {
        throw "Watchdog PID changed during Gate G1: $ExpectedPid -> $serviceProcessId."
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

Assert-Administrator

Write-Host 'VictusFanControl - WATCHDOG GATE G1 (FULL SUSPEND/RESUME LIFECYCLE)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test WILL put Windows into sleep after explicit confirmation.' -ForegroundColor Yellow
Write-Host 'The automatic fan policy remains OFF.' -ForegroundColor Yellow
Write-Host 'The real LocalSystem watchdog, real HP backend, durable lease and real PBT_APMSUSPEND path are used.' -ForegroundColor Yellow
Write-Host 'No parent-shell HP fan restore is part of the PASS path.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Expected causal path:'
Write-Host '  baseline Ready + journal absent + EC FF/FF + Healthy'
Write-Host '  -> Prepare -> WRITE_ARMED -> WMI 30/30 -> EC+tachs ACK -> Commit -> OWNED'
Write-Host '  -> PBT_APMSUSPEND -> fence/cancel -> RestoreBegin -> local FF/FF -> LegacyDefault'
Write-Host '  -> watchdog Release/normalize -> journal absent -> Firmware -> NotifySuspend'
Write-Host '  -> sleep/resume -> same watchdog PID -> five complete telemetry samples -> Healthy'
Write-Host '  -> watchdog Ready/journal absent -> reopen fence -> one controlled 30/30 re-entry'
Write-Host '  -> final Firmware + FF/FF + journal absent'
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and any normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host 'Save unrelated work before continuing.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        Write-Error "Refusing Gate G1 while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: verify S3, stale-shell file locks, and build all code with warnings as errors...' -ForegroundColor Cyan
Assert-DefaultWatchdogOutputUnlocked

$availableSleep = (& powercfg.exe /a 2>&1 | Out-String)
Write-Host $availableSleep
if ($availableSleep -notmatch '\(S3\)') {
    throw 'Gate G1 requires classic S3 sleep on this validated target.'
}

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: run synthetic/invariant regressions...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-watchdog-gate-g0-clock-invariants.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'test-watchdog-gate-g1-invariants.ps1')
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
Write-Host 'Step 3: read-only hardware baseline...' -ForegroundColor Cyan
dotnet run --project .\src\VictusFanControl -c Release --no-build -- --probe-backends
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$baseline = Read-EcState
Write-Host "EC baseline         : $($baseline.Raw)"

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    throw "Gate G1 requires firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu)."
}

if (Test-Path $journalPath) {
    throw "Gate G1 requires the durable watchdog journal to be absent BEFORE service reinstall/start; found $journalPath. Preserve and investigate this state instead of clearing it implicitly."
}

Write-Host 'Durable journal     : absent before service reinstall' -ForegroundColor Green

Write-Host ''
Write-Host 'Verify the CPU undervolt shown in OMEN Gaming Hub.' -ForegroundColor Yellow
$pre = Read-Host 'Type UNDERVOLT-OK after checking it'
if ($pre -cne 'UNDERVOLT-OK') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

Write-Host ''
Write-Host 'Step 4: install/start the production LocalSystem watchdog...' -ForegroundColor Cyan
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
Write-Host "Startup recovery    : $($serviceReady.Status.RecoveryDisposition)"
Write-Host "Startup detail      : $($serviceReady.Status.Detail)"

Assert-ScmRecoveryPolicy

if (Test-Path $journalPath) {
    throw "Gate G1 baseline requires journal absent; found $journalPath"
}

$serviceBaselineEc = Read-EcState
Write-Host "EC after service    : $($serviceBaselineEc.Raw)"
if ($serviceBaselineEc.Cpu -ne 255 -or
    $serviceBaselineEc.Gpu -ne 255) {
    throw "Watchdog baseline is not firmware-owned FF/FF: $($serviceBaselineEc.Cpu)/$($serviceBaselineEc.Gpu)."
}

Write-Host ''
Write-Host 'The next confirmation permits two bounded 30/30 writes: one before sleep and one controlled re-entry after recovery.' -ForegroundColor Yellow
$confirm = Read-Host 'Type GATEG1 to continue'
if ($confirm -cne 'GATEG1') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Remove-Item $readyPath, $preSleepPath, $reentryPath, $resultPath, $failsafeLog -Force -ErrorAction SilentlyContinue

$logLineBoundary = if (Test-Path $serviceLog) {
    @(Get-Content $serviceLog).Count
} else {
    0
}

Write-Host ''
Write-Host 'Step 5: arm independent delayed emergency fallback before Custom 30/30...' -ForegroundColor Cyan
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
    Write-Host 'Step 6: launch dedicated Gate G1 GUI and wait for durable OWNED 30/30...' -ForegroundColor Cyan

    $proc = Start-Process -FilePath $app -ArgumentList @(
        '--gate-g1-suspend-test',
        '--gate-g1-test-token',
        '88F8-GATEG1-30'
    ) -PassThru

    if (-not (Wait-ForFile -Path $readyPath -Seconds 90)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $resultPath) {
                Get-Content $resultPath -Raw
            } else {
                'no Gate G1 result marker'
            }

            throw "VictusFanControl.App exited before Gate G1 READY. ExitCode=$($proc.ExitCode). $detail"
        }

        throw 'Timed out waiting for Gate G1 durable OWNED READY.'
    }

    $readyReached = $true
    $readyText = Get-Content $readyPath -Raw
    Write-Host "Gate G1 READY       : $readyText" -ForegroundColor Green

    if ($readyText -notmatch '^READY\|' -or
        $readyText -notmatch 'ack=backend-ec\+tachs\+watchdog-owned') {
        throw 'Gate G1 READY marker does not prove backend EC+tachs+watchdog-owned acknowledgement.'
    }

    if (-not (Test-Path $journalPath)) {
        throw 'Gate G1 READY exists but the durable watchdog lease journal is missing.'
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $journalOwned = Test-JournalOwnedPhase -Phase $journal.Phase
    $procStartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks

    Write-Host "Journal phase       : $($journal.Phase)"
    Write-Host "Journal generation  : $($journal.Generation)"
    Write-Host "Journal controller  : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "GUI identity        : PID=$($proc.Id) startTicks=$procStartTicks"
    Write-Host "Journal target      : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

    if (-not $journalOwned -or
        [int]$journal.Controller.ProcessId -ne $proc.Id -or
        [long]$journal.Controller.ProcessStartUtcTicks -ne $procStartTicks -or
        [int]$journal.Owned.Cpu -ne 30 -or
        [int]$journal.Owned.Gpu -ne 30) {
        throw 'Gate G1 READY is not backed by exact durable OWNED 30/30 bound to the GUI PID + creation time.'
    }

    [void](Assert-ProductionServiceReady -ExpectedPid $servicePidBefore)

    Write-Host ''
    Write-Host 'Step 7: request S3 from a separate read-only Gate G0 sleep helper...' -ForegroundColor Cyan
    Write-Host "Wake request        : about $WakeAfterSeconds s (manual wake is still allowed if firmware wakes earlier/later)."

    $eventWindowStart = Get-Date

    # Reuse the already validated G0 helper solely as the external S3 requester
    # and best-effort wake-timer owner. It dispatches before any watchdog service
    # host construction and has no EC/WMI/lease path. Gate G1 PASS is determined
    # by its own PBT_APMSUSPEND marker, Kernel-Power evidence and lifecycle state.
    $sleepOutput = ('' | & dotnet $watchdogDll --gate-g0-clock-probe --wake-after-seconds $WakeAfterSeconds --minimum-excluded-seconds 5 --gate-g0-auto-s3-token '88F8-G0-AUTO-S3' 2>&1 | Out-String)
    $sleepExit = $LASTEXITCODE

    Write-Host ''
    Write-Host 'External S3 helper output:' -ForegroundColor Cyan
    Write-Host $sleepOutput

    if ($sleepOutput -notmatch 'SUSPEND_DISPATCH' -or
        $sleepOutput -notmatch 'RESUME_RETURN') {
        throw "External S3 helper did not prove a suspend call returned through resume. ExitCode=$sleepExit"
    }

    Write-Host ''
    Write-Host 'Step 8: prove pre-sleep release marker, recovery, controlled re-entry and final handoff...' -ForegroundColor Cyan

    if (-not (Wait-ForFile -Path $preSleepPath -Seconds 15)) {
        throw 'Gate G1 did not persist its pre-sleep handoff marker.'
    }

    $preSleep = Get-Content $preSleepPath -Raw
    Write-Host "Pre-sleep marker    : $preSleep"

    if ($preSleep -notmatch '^PASS\|' -or
        $preSleep -notmatch 'authority=Firmware' -or
        $preSleep -notmatch 'ec=255/255' -or
        $preSleep -notmatch 'journal=absent' -or
        $preSleep -notmatch ("watchdogPid={0}" -f $servicePidBefore)) {
        throw 'Gate G1 pre-sleep marker does not prove Firmware + FF/FF + journal absent under the original watchdog PID.'
    }

    if (-not (Wait-ForFile -Path $resultPath -Seconds 120)) {
        throw 'Timed out waiting for Gate G1 post-resume result.'
    }

    $resultText = Get-Content $resultPath -Raw
    Write-Host "Gate G1 result      : $resultText"

    if ($resultText -notmatch '^PASS\|') {
        throw "Gate G1 application reported failure: $resultText"
    }

    if (-not (Test-Path $reentryPath)) {
        throw 'Gate G1 PASS is missing the controlled post-resume re-entry marker.'
    }

    $reentryText = Get-Content $reentryPath -Raw
    Write-Host "Re-entry marker     : $reentryText"

    if ($reentryText -notmatch '^REENTRY\|' -or
        $reentryText -notmatch 'authority=Custom' -or
        $reentryText -notmatch 'cpu=30\|gpu=30' -or
        $reentryText -notmatch 'ack=backend-ec\+tachs\+watchdog-owned' -or
        $reentryText -notmatch ("watchdogPid={0}" -f $servicePidBefore)) {
        throw 'Gate G1 controlled re-entry marker is incomplete or inconsistent.'
    }

    $appExited = $false
    try {
        $appExited = $proc.WaitForExit(15000)
    }
    catch {
        $appExited = $proc.HasExited
    }

    if (-not $appExited) {
        throw 'Gate G1 application published PASS but did not exit within 15 s; refusing an out-of-band final EC probe while the GUI/telemetry process is still alive.'
    }

    [void](Assert-ProductionServiceReady -ExpectedPid $servicePidBefore)
    if (Test-Path $journalPath) {
        throw 'Gate G1 application PASS returned with a durable journal still present.'
    }

    $finalEc = Read-EcState
    Write-Host "Independent final EC: $($finalEc.Raw)"

    if ($finalEc.Cpu -ne 255 -or
        $finalEc.Gpu -ne 255) {
        throw "Gate G1 final EC is not FF/FF: $($finalEc.Cpu)/$($finalEc.Gpu)."
    }

    $powerEvents = Get-PowerEvents -StartTime $eventWindowStart
    Write-Host ''
    Write-Host 'Kernel-Power evidence:' -ForegroundColor Cyan
    if ($powerEvents.Count -gt 0) {
        $powerEvents |
            Select-Object TimeCreated, Id, ProviderName, Message |
            Format-List |
            Out-String |
            Write-Host
    }

    $sleepEvent = @($powerEvents | Where-Object Id -eq 42) | Select-Object -First 1
    $resumeEvent = @($powerEvents | Where-Object Id -eq 107) | Select-Object -Last 1

    if ($null -eq $sleepEvent -or
        $null -eq $resumeEvent -or
        $resumeEvent.TimeCreated -lt $sleepEvent.TimeCreated) {
        throw 'Gate G1 requires ordered Kernel-Power 42 -> 107 evidence for the physical suspend/resume cycle.'
    }

    $newServiceLog = Get-NewServiceLogLines
    Write-Host ''
    Write-Host 'Watchdog log during Gate G1:' -ForegroundColor Cyan
    $newServiceLog | Select-Object -Last 80 | ForEach-Object { Write-Host $_ }

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
            throw "Gate G1 normal lifecycle produced forbidden watchdog recovery/failure evidence: '$pattern'."
        }
    }

    if ($failsafe.HasExited) {
        $failsafeDetail = if (Test-Path $failsafeLog) {
            Get-Content $failsafeLog -Raw
        } else {
            'no fallback log'
        }

        throw "Emergency fallback reached its execution boundary before Gate G1 safety was independently proven. $failsafeDetail"
    }

    $pass = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    # The parent harness never performs an HP restore. If a failure leaves the
    # exact test GUI alive, terminate only that process so the independent
    # production watchdog owns crash cleanup from its durable journal.
    if (-not $pass -and
        $proc -and
        -not $proc.HasExited) {
        Write-Warning "Gate G1 failed while GUI PID $($proc.Id) is alive; force-killing only that exact GUI so watchdog recovery owns cleanup."
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { [void]$proc.WaitForExit(5000) } catch {}
    }

    if (Test-Path $journalPath) {
        Write-Host 'Waiting up to 20 s for watchdog cleanup of any retained Gate G1 lease...' -ForegroundColor Cyan
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
    Write-Host 'Gate G1 did NOT pass.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $preSleepPath) {
        Write-Host "Pre-sleep evidence  : $(Get-Content $preSleepPath -Raw)"
    }

    if (Test-Path $resultPath) {
        Write-Host "Application result  : $(Get-Content $resultPath -Raw)"
    }

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent watchdog log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 100
    }

    if (Test-Path $appLog) {
        Write-Host ''
        Write-Host 'Recent application lifecycle log:' -ForegroundColor Cyan
        Get-Content $appLog |
            Select-String -Pattern 'GATE G1|Fan authority|Suspend detected|Resume detected|Recovery completed|Duplicate resume' |
            Select-Object -Last 120 |
            ForEach-Object { Write-Host $_.Line }
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
Write-Host 'PASS: Gate G1 full watchdog suspend/resume lifecycle completed with causal pre-sleep and post-resume evidence.' -ForegroundColor Green
Write-Host 'Verified: durable OWNED 30/30 -> PBT_APMSUSPEND -> RestoreBegin/local restore/Release -> pre-sleep FF/FF + journal absent -> real sleep/resume -> same watchdog PID -> Healthy -> controlled re-entry -> final Firmware FF/FF + journal absent.' -ForegroundColor Green
Write-Host 'No watchdog timeout/recovery/fatal evidence occurred, the emergency fallback did not run, and OMEN Gaming Hub undervolt is SAME.' -ForegroundColor Green
exit 0
