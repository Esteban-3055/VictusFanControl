param(
    [ValidateRange(30, 300)]
    [int]$RecommendedSleepSeconds = 60,

    [ValidateRange(180, 300)]
    [int]$FailsafeDelaySeconds = 300,

    [ValidateRange(60, 300)]
    [int]$ResultTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM4'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath = Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath = Join-Path $serviceRoot 'state\lease.json'
$serviceLog = Join-Path $serviceRoot ("logs\watchdog-m4-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$appRoot = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$appLogPath = Join-Path $appRoot ("logs\events-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$readyPath = Join-Path $appRoot 'm6-modern-standby.ready'
$preSleepPath = Join-Path $appRoot 'm6-modern-standby.presleep'
$resumeGatePath = Join-Path $appRoot 'm6-modern-standby.resume-gate'
$reentryPath = Join-Path $appRoot 'm6-modern-standby.reentry'
$resultPath = Join-Path $appRoot 'm6-modern-standby.result'

$appExe = Join-Path $repoRoot 'src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'

$failsafeScript = Join-Path $PSScriptRoot 'watchdog-m5c-service-failsafe-8c40.ps1'
$failsafeLog = Join-Path $serviceRoot ("logs\watchdog-m6-failsafe-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd-HHmmss'))

$diagnosticRoot = Join-Path $repoRoot ("logs\modern-standby-m6_{0}" -f (Get-Date -Format 'yyyy-MM-dd_HHmmss'))

$token = '8C40-M6-MODERN-STANDBY30'
$restartDelayMs = 5000

$app = $null
$failsafe = $null
$serviceSetupTouched = $false
$servicePidBefore = 0
$serviceStartTicksBefore = 0L
$logLineBoundary = 0
$pass = $false
$failure = $null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'M6 physical lifecycle gate must run from an elevated PowerShell.'
    }
}

function Assert-Exact8C40Target {
    $board = Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios = Get-CimInstance Win32_BIOS -ErrorAction Stop

    $sku = ([string]$system.SystemSKUNumber).Trim()
    $skuBase = ($sku -split '#', 2)[0].Trim()
    $biosText = @(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    if (([string]$board.Manufacturer).Trim() -cne 'HP' -or
        ([string]$board.Product).Trim() -cne '8C40' -or
        ([string]$board.Version).Trim() -cne '63.43' -or
        ([string]$system.Manufacturer).Trim() -cne 'HP' -or
        ([string]$system.Model).Trim() -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
        $skuBase -cne '9D0R1LA' -or
        $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)') {
        throw 'M6 exact-target fingerprint mismatch.'
    }

    Write-Host "Target: HP 8C40 / 63.43 / $sku / BIOS $biosText" -ForegroundColor Green
}

function Read-8C40Setpoint {
    $output = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    $line = ($output -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1)

    if (-not $line) {
        throw "Could not parse HP 8C40 setpoint probe. Raw output: $output"
    }

    $match = [regex]::Match($line, '^setpoint CPU=(\d+) GPU=(\d+)$')
    if (-not $match.Success) {
        throw "Could not parse setpoint line: $line"
    }

    [pscustomobject]@{
        Cpu = [int]$match.Groups[1].Value
        Gpu = [int]$match.Groups[2].Value
        Raw = $line
    }
}

function Wait-StableFirmwareAuto {
    param(
        [string]$Label,
        [int]$MaxSamples = 8
    )

    $consecutive = 0
    $unexpectedCpu = -1
    $unexpectedGpu = -1
    $unexpectedSamples = 0
    $last = $null

    for ($sample = 1; $sample -le $MaxSamples; $sample++) {
        $last = Read-8C40Setpoint
        Write-Host ("{0} sample {1}/{2}: {3}" -f $Label, $sample, $MaxSamples, $last.Raw)

        if ($last.Cpu -eq 255 -and $last.Gpu -eq 255) {
            $consecutive++
            $unexpectedCpu = -1
            $unexpectedGpu = -1
            $unexpectedSamples = 0

            if ($consecutive -ge 2) {
                return [pscustomobject]@{
                    Verified = $true
                    State = $last
                }
            }
        }
        else {
            $consecutive = 0

            if ($last.Cpu -eq $unexpectedCpu -and
                $last.Gpu -eq $unexpectedGpu) {
                $unexpectedSamples++
            }
            else {
                $unexpectedCpu = $last.Cpu
                $unexpectedGpu = $last.Gpu
                $unexpectedSamples = 1
            }

            if ($unexpectedSamples -ge 2) {
                return [pscustomobject]@{
                    Verified = $false
                    State = $last
                }
            }
        }

        if ($sample -lt $MaxSamples) {
            Start-Sleep -Milliseconds 75
        }
    }

    return [pscustomobject]@{
        Verified = $false
        State = $last
    }
}

function Get-ServiceState {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-ServiceProcessId {
    $svc = Get-ServiceState
    if (-not $svc -or $svc.State -ne 'Running') { return 0 }
    return [int]$svc.ProcessId
}

function Get-ProcessStartTicks {
    param([int]$ProcessId)

    $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)
    try {
        return [long]$process.StartTime.ToUniversalTime().Ticks
    }
    finally {
        $process.Dispose()
    }
}

function Wait-ServiceReady {
    param(
        [int]$ExpectedPid,
        [long]$ExpectedStartTicks,
        [int]$Seconds = 20
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statusPath) {
            try {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json
                $currentPid = Get-ServiceProcessId

                if ($status.Ready -and
                    -not $status.Blocked -and
                    [int]$status.ProcessId -eq $ExpectedPid -and
                    $currentPid -eq $ExpectedPid -and
                    [int]$status.SessionId -eq 0 -and
                    $status.AccountName -match 'SYSTEM$' -and
                    $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                    $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2' -and
                    (Get-ProcessStartTicks -ProcessId $ExpectedPid) -eq $ExpectedStartTicks) {
                    return $status
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw 'Timed out waiting for exact original M6 watchdog Ready state.'
}

function Wait-ForFile {
    param(
        [string]$Path,
        [int]$Seconds,
        [System.Diagnostics.Process]$Process
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while (-not (Test-Path $Path) -and
           (Get-Date) -lt $deadline) {
        if ($Process -and $Process.HasExited) {
            return $false
        }

        Start-Sleep -Milliseconds 100
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

function Configure-RecoveryPolicy {
    & sc.exe failure $serviceName reset= 86400 actions= "restart/$restartDelayMs/restart/5000/restart/10000" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not configure M6 SCM recovery actions; sc.exe exit=$LASTEXITCODE."
    }

    & sc.exe failureflag $serviceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enable M6 SCM failure actions; sc.exe exit=$LASTEXITCODE."
    }
}

function Restore-M4Baseline {
    if (Test-Path $journalPath) {
        throw 'M6 refuses M4 reinstall while durable journal evidence remains.'
    }

    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')

    $svc = Get-Service -Name $serviceName -ErrorAction Stop

    if ($svc.Status -ne 'Stopped' -or
        $svc.StartType -ne 'Manual') {
        throw "M6 M4 baseline restore failed: StartType=$($svc.StartType) Status=$($svc.Status)."
    }
}

function Start-DelayedFailsafe {
    Remove-Item $failsafeLog -Force -ErrorAction SilentlyContinue

    $process = Start-Process powershell.exe -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy','Bypass',
        '-File',$failsafeScript,
        '-DelaySeconds',$FailsafeDelaySeconds,
        '-LogPath',$failsafeLog
    ) -WindowStyle Hidden -PassThru

    Start-Sleep -Milliseconds 250
    $process.Refresh()

    if ($process.HasExited) {
        throw 'M6 delayed safety fallback exited before the lifecycle controller was launched.'
    }

    return $process
}

function Test-FailsafeTakeover {
    if (-not (Test-Path $failsafeLog)) {
        return $false
    }

    $text = Get-Content $failsafeLog -Raw

    return ($text -match 'M5C FAILSAFE TAKEOVER:' -or
            $text -match 'M5C FAILSAFE CONTROLLER-KILL:' -or
            $text -match 'M5C FAILSAFE SERVICE-START:' -or
            $text -match 'M5C FAILSAFE SERVICE-RESTART:' -or
            $text -match 'M5C FAILSAFE STARTED:' -or
            $text -match 'M5C FAILSAFE RECOVERED:')
}

function Read-OwnedJournal {
    if (-not (Test-Path $journalPath)) {
        throw 'M6 READY exists but durable journal is missing.'
    }

    return Get-Content $journalPath -Raw | ConvertFrom-Json
}

function Assert-Owned30Journal {
    param(
        $Journal,
        [int]$ControllerPid,
        [long]$ControllerStartTicks
    )

    $phaseOwned =
        ([string]$Journal.Phase -ceq 'Owned') -or
        ([string]$Journal.Phase -ceq '2') -or
        ([int]$Journal.Phase -eq 2)

    if ([int]$Journal.SchemaVersion -ne 2 -or
        $Journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not $phaseOwned -or
        [long]$Journal.Generation -ne 3 -or
        [int]$Journal.Controller.ProcessId -ne $ControllerPid -or
        [long]$Journal.Controller.ProcessStartUtcTicks -ne $ControllerStartTicks -or
        [int]$Journal.Owned.Cpu -ne 30 -or
        [int]$Journal.Owned.Gpu -ne 30) {
        throw 'M6 journal is not exact schema-v2 generation-3 OWNED 30/30 bound to the exact GUI process.'
    }
}

function Get-M6PowerTransitionDisqualifier {
    param(
        [datetime]$StartTime,
        [long]$AfterRecordId
    )

    $events = @(
        Get-WinEvent -FilterHashtable @{
            LogName='System'
            ProviderName='Microsoft-Windows-Kernel-Power'
            StartTime=$StartTime
        } -ErrorAction SilentlyContinue |
        Where-Object {
            $_.RecordId -gt $AfterRecordId -and
            ($_.Id -eq 42 -or
             $_.Id -eq 507 -or
             $_.Id -eq 524)
        } |
        Sort-Object TimeCreated
    )

    $criticalBattery = $events |
        Where-Object { $_.Id -eq 524 } |
        Select-Object -First 1

    if ($criticalBattery) {
        return ("Kernel-Power 524 critical-battery trigger at {0:O}" -f $criticalBattery.TimeCreated)
    }

    $batterySleep = $events |
        Where-Object {
            $_.Id -eq 42 -and
            $_.Message -match '(?i)(battery|bater[ií]a)'
        } |
        Select-Object -First 1

    if ($batterySleep) {
        return ("Kernel-Power 42 battery-triggered sleep at {0:O}: {1}" -f
            $batterySleep.TimeCreated,
            (($batterySleep.Message -replace '\s+', ' ').Trim()))
    }

    $hibernateResume = $events |
        Where-Object {
            $_.Id -eq 507 -and
            $_.Message -match '(?i)hibern'
        } |
        Select-Object -First 1

    if ($hibernateResume) {
        return ("Kernel-Power 507 reports hibernate resume at {0:O}: {1}" -f
            $hibernateResume.TimeCreated,
            (($hibernateResume.Message -replace '\s+', ' ').Trim()))
    }

    return $null
}
function Wait-ModernStandbyKernelEvidence {
    param(
        [datetime]$StartTime,
        [datetime]$DisplayOnTime,
        [long]$AfterRecordId,
        [int]$Seconds = 30
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    $latestAllowedResume = $DisplayOnTime.AddSeconds(3)

    while ((Get-Date) -lt $deadline) {
        $events = @(
            Get-WinEvent -FilterHashtable @{
                LogName='System'
                ProviderName='Microsoft-Windows-Kernel-Power'
                StartTime=$StartTime
            } -ErrorAction SilentlyContinue |
            Where-Object {
                $_.RecordId -gt $AfterRecordId -and
                ($_.Id -eq 506 -or $_.Id -eq 507) -and
                $_.TimeCreated -le $latestAllowedResume
            } |
            Sort-Object TimeCreated
        )

        $sleep = $events |
            Where-Object { $_.Id -eq 506 } |
            Select-Object -First 1

        $resume = $events |
            Where-Object {
                $_.Id -eq 507 -and
                $sleep -and
                $_.TimeCreated -ge $sleep.TimeCreated
            } |
            Select-Object -Last 1

        if ($sleep -and $resume) {
            return [pscustomobject]@{
                Sleep = $sleep
                Resume = $resume
                Events = $events
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Get-KernelPowerRecordBoundary {
    $event =
        Get-WinEvent -FilterHashtable @{
            LogName='System'
            ProviderName='Microsoft-Windows-Kernel-Power'
        } -MaxEvents 1 -ErrorAction SilentlyContinue

    if ($null -eq $event) {
        return 0L
    }

    return [long]$event.RecordId
}

function Parse-MarkerDateTimeOffsetField {
    param(
        [string]$Text,
        [string]$Name
    )

    $prefix = $Name + '='
    $field =
        $Text -split '\|' |
        Where-Object {
            $_.StartsWith(
                $prefix,
                [StringComparison]::Ordinal)
        } |
        Select-Object -First 1

    if (-not $field) {
        throw "Marker has no '$Name' field: $Text"
    }

    return [DateTimeOffset]::Parse(
        $field.Substring($prefix.Length),
        [Globalization.CultureInfo]::InvariantCulture)
}
function Parse-MarkerTimestamp {
    param([string]$Text)

    $parts = $Text -split '\|'
    if ($parts.Length -lt 2) {
        throw "Marker has no timestamp: $Text"
    }

    return [DateTimeOffset]::Parse(
        $parts[1],
        [Globalization.CultureInfo]::InvariantCulture)
}

function Show-Diagnostics {
    Write-Host ''

    foreach ($item in @(
        @{ Name='READY'; Path=$readyPath },
        @{ Name='PRE-SLEEP'; Path=$preSleepPath },
        @{ Name='RESUME-GATE'; Path=$resumeGatePath },
        @{ Name='REENTRY'; Path=$reentryPath },
        @{ Name='RESULT'; Path=$resultPath }
    )) {
        if (Test-Path $item.Path) {
            Write-Host ("M6 {0}:" -f $item.Name) -ForegroundColor Cyan
            Get-Content $item.Path
            Write-Host ''
        }
    }

    if (Test-Path $appLogPath) {
        Write-Host 'Recent VFC application log:' -ForegroundColor Cyan
        Get-Content $appLogPath | Select-Object -Last 220
        Write-Host ''
    }

    if (Test-Path $statusPath) {
        Write-Host 'M4 status:' -ForegroundColor Cyan
        Get-Content $statusPath
    }

    if (Test-Path $journalPath) {
        Write-Host 'M4 journal:' -ForegroundColor Yellow
        Get-Content $journalPath
    }

    if (Test-Path $failsafeLog) {
        Write-Host 'M6 delayed-failsafe log:' -ForegroundColor Cyan
        Get-Content $failsafeLog
    }

    if (Test-Path $serviceLog) {
        Write-Host 'Recent M4 service log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 160
    }
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M6 MODERN STANDBY LIFECYCLE QUALIFICATION' -ForegroundColor Cyan
Write-Host ''
Write-Host 'M6 uses SESSION_DISPLAY_STATUS Off as the proactive release boundary and On as the only accepted user-visible resume boundary.' -ForegroundColor Yellow
Write-Host 'This test writes real 30/30 before sleep and again once after fully validated resume, then restores HP firmware each time.' -ForegroundColor Yellow
Write-Host 'The script itself does NOT call SetSuspendState. You must choose Windows Start -> Power -> Sleep manually.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "M6 refused while '$name' is running. Close it and retry."
    }
}

Write-Host 'Step 1: exact target + build + lifecycle regressions...' -ForegroundColor Cyan
Assert-Exact8C40Target

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($invariant in @(
    'test-watchdog-m5d-write-armed-invariants.ps1',
    'test-watchdog-m5e-write-armed-double-death-invariants.ps1',
    'test-8c40-modern-standby-m6-invariants.ps1'
)) {
    & (Join-Path $PSScriptRoot $invariant)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet run --project .\src\VictusFanControl.ModernStandbyProbe -c Release --no-build -- --self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m4-8c40-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --safety-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --control-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --hp-backend-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path $appExe)) {
    throw "M6 app executable is missing: $appExe"
}

Write-Host ''
Write-Host 'Step 2: clean durable/firmware baseline...' -ForegroundColor Cyan

if (Test-Path $journalPath) {
    Get-Content $journalPath
    throw 'M6 refuses an existing durable journal.'
}

$baseline = Wait-StableFirmwareAuto -Label 'Baseline EC'
if (-not $baseline.Verified) {
    throw "M6 requires stable FF/FF baseline; last=$($baseline.State.Raw)."
}

Write-Host ''
Write-Host 'Step 3: install/start M4 LocalSystem service with temporary crash recovery...' -ForegroundColor Cyan

try {
    $serviceSetupTouched = $true
    & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')
    Configure-RecoveryPolicy

    Remove-Item $statusPath -Force -ErrorAction SilentlyContinue
    Start-Service -Name $serviceName

    $deadline = (Get-Date).AddSeconds(15)
    while ($servicePidBefore -le 0 -and (Get-Date) -lt $deadline) {
        $servicePidBefore = Get-ServiceProcessId
        if ($servicePidBefore -le 0) {
            Start-Sleep -Milliseconds 100
        }
    }

    if ($servicePidBefore -le 0) {
        throw 'M6 could not resolve the original M4 watchdog PID.'
    }

    $serviceStartTicksBefore = Get-ProcessStartTicks -ProcessId $servicePidBefore
    $status = Wait-ServiceReady -ExpectedPid $servicePidBefore -ExpectedStartTicks $serviceStartTicksBefore

    Write-Host "Watchdog PID       : $servicePidBefore"
    Write-Host "Watchdog startTicks: $serviceStartTicksBefore"
    Write-Host "Account            : $($status.AccountName)"
    Write-Host "Target             : $($status.TargetProfileId)"

    $logLineBoundary = if (Test-Path $serviceLog) {
        @(Get-Content $serviceLog).Count
    } else {
        0
    }

    Write-Host ''
    Write-Host 'ACTIVE M6 LIFECYCLE BOUNDARY' -ForegroundColor Yellow
    Write-Host 'Save unrelated work now. The computer will enter Modern Standby when YOU choose Sleep.' -ForegroundColor Yellow
    Write-Host "Keep it asleep for about $RecommendedSleepSeconds seconds, then wake it normally with the keyboard/mouse/power button." -ForegroundColor Yellow
    Write-Host 'Do not close the lid for this first M6 cycle and do not manually terminate any VFC process.' -ForegroundColor Yellow

    $confirm = Read-Host "Type exactly $token to continue"
    if ($confirm -cne $token) {
        throw 'M6 cancelled before any fan write.'
    }

    Write-Host ''
    Write-Host 'Step 4: arm delayed owner-loss safety fallback and launch M6 GUI...' -ForegroundColor Cyan

    $failsafe = Start-DelayedFailsafe
    Write-Host "Failsafe PID       : $($failsafe.Id)"
    Write-Host "Failsafe delay     : $FailsafeDelaySeconds s"

    foreach ($path in @($readyPath,$preSleepPath,$resumeGatePath,$reentryPath,$resultPath)) {
        Remove-Item $path -Force -ErrorAction SilentlyContinue
    }

    $app = Start-Process -FilePath $appExe -ArgumentList @(
        '--8c40-m6-modern-standby-test',
        '--8c40-m6-test-token',
        $token,
        '--modules-dir',
        $modulesDir
    ) -PassThru

    if (-not (Wait-ForFile -Path $readyPath -Seconds 45 -Process $app)) {
        if ($app.HasExited) {
            try { $app.WaitForExit(); $app.Refresh() } catch {}
            throw "M6 GUI exited before READY. ExitCode=$($app.ExitCode)."
        }

        throw 'Timed out waiting for M6 READY marker.'
    }

    $readyText = Get-Content $readyPath -Raw
    Write-Host "READY: $readyText" -ForegroundColor Green

    $guiStartTicks = [long]$app.StartTime.ToUniversalTime().Ticks

    if ($readyText -notmatch '^READY\|' -or
        $readyText -notmatch 'authority=Custom' -or
        $readyText -notmatch 'cpu=30\|gpu=30' -or
        $readyText -notmatch 'ack=backend-ec\+tachs\+watchdog-owned' -or
        $readyText -notmatch 'journal=Owned30' -or
        $readyText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
        $readyText -notmatch ("watchdogStartTicks={0}" -f $serviceStartTicksBefore) -or
        $readyText -notmatch ("guiPid={0}" -f $app.Id) -or
        $readyText -notmatch ("guiStartTicks={0}" -f $guiStartTicks) -or
        $readyText -notmatch 'resumePolicy=session-display-on-only') {
        throw 'M6 READY marker is incomplete or not bound to the exact watchdog/GUI identities.'
    }

    $journal = Read-OwnedJournal
    Assert-Owned30Journal -Journal $journal -ControllerPid $app.Id -ControllerStartTicks $guiStartTicks

    [void](Wait-ServiceReady -ExpectedPid $servicePidBefore -ExpectedStartTicks $serviceStartTicksBefore)

    $failsafe.Refresh()
    if ($failsafe.HasExited -or (Test-FailsafeTakeover)) {
        throw 'M6 delayed fallback is not clean/alive before the sleep transition.'
    }

    $eventWindowStart = (Get-Date).AddSeconds(-2)
    $kernelPowerRecordBoundary =
        Get-KernelPowerRecordBoundary

    Write-Host "Kernel-Power record boundary: $kernelPowerRecordBoundary"

    Write-Host ''
    Write-Host 'Step 5: PERFORM THE REAL MODERN STANDBY TRANSITION NOW.' -ForegroundColor Yellow
    Write-Host 'Choose: Start -> Power -> Sleep.' -ForegroundColor Yellow
    Write-Host "Leave the notebook sleeping for about $RecommendedSleepSeconds seconds, then wake it normally." -ForegroundColor Yellow
    [void](Read-Host 'Press Enter now, then immediately choose Sleep from Windows')

    $resultDeadline = (Get-Date).AddSeconds($ResultTimeoutSeconds)

    while (-not (Test-Path $resultPath) -and
           (Get-Date) -lt $resultDeadline) {
        if ($app.HasExited -and -not (Test-Path $resultPath)) {
            try { $app.WaitForExit(); $app.Refresh() } catch {}
            throw "M6 GUI exited without a result marker. ExitCode=$($app.ExitCode)."
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not (Test-Path $resultPath)) {
        throw "Timed out after $ResultTimeoutSeconds s waiting for M6 result after resume."
    }

    Write-Host ''
    Write-Host 'Step 6: validate display-aware handoff/resume/re-entry markers...' -ForegroundColor Cyan

    $powerDisqualifier = Get-M6PowerTransitionDisqualifier -StartTime $eventWindowStart -AfterRecordId $kernelPowerRecordBoundary
    if ($powerDisqualifier) {
        throw "M6 requested Modern Standby cycle was contaminated by a disqualifying power transition: $powerDisqualifier"
    }

    foreach ($requiredPath in @($preSleepPath,$resumeGatePath,$reentryPath)) {
        if (-not (Test-Path $requiredPath)) {
            throw "M6 required marker is missing: $requiredPath"
        }
    }

    $preSleepText = Get-Content $preSleepPath -Raw
    $resumeText = Get-Content $resumeGatePath -Raw
    $reentryText = Get-Content $reentryPath -Raw
    $resultText = Get-Content $resultPath -Raw

    Write-Host "Pre-sleep : $preSleepText"
    Write-Host "Resume gate: $resumeText"
    Write-Host "Re-entry  : $reentryText"
    Write-Host "Result    : $resultText"

    if ($preSleepText -notmatch '^PASS\|' -or
        $preSleepText -notmatch 'source=GUID_SESSION_DISPLAY_STATUS/Off' -or
        $preSleepText -notmatch 'restoreTrigger=registered-WM_POWERBROADCAST/PBT_APMSUSPEND' -or
        $preSleepText -notmatch 'displayOffAt=' -or
        $preSleepText -notmatch 'primaryDisplaySignal=True' -or
        $preSleepText -notmatch 'wasCustom=True' -or
        $preSleepText -notmatch 'backendAck=True' -or
        $preSleepText -notmatch 'authority=Firmware' -or
        $preSleepText -notmatch 'localFirmwareAck=True' -or
        $preSleepText -notmatch 'watchdogRelease=True' -or
        $preSleepText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
        $preSleepText -notmatch ("watchdogStartTicks={0}" -f $serviceStartTicksBefore) -or
        $preSleepText -notmatch 'journal=absent' -or
        $preSleepText -notmatch 'ec=255/255' -or
        $preSleepText -notmatch 'ecProof=stable-two-sample-FF/FF') {
        throw 'M6 pre-sleep marker does not prove proactive display-Off firmware/watchdog handoff.'
    }

    if ($resumeText -notmatch '^GATED\|' -or
        $resumeText -notmatch 'source=GUID_SESSION_DISPLAY_STATUS/On' -or
        $resumeText -notmatch 'acceptedUserResumes=1' -or
        $resumeText -notmatch 'pbtSuspendObserved=True' -or
        $resumeText -notmatch 'authority=Firmware' -or
        $resumeText -notmatch 'ec=255/255' -or
        $resumeText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
        $resumeText -notmatch 'journal=absent') {
        throw 'M6 display-On resume-gate marker is incomplete.'
    }

    if ($resumeText -notmatch 'resumeAutomaticWhileOff=True' -and
        $resumeText -notmatch 'resumeSuspendWhileOff=True') {
        throw 'M6 did not prove that at least one PBT resume notification was deferred while the session display remained Off.'
    }

    if ($reentryText -notmatch '^REENTRY\|' -or
        $reentryText -notmatch 'authority=Custom' -or
        $reentryText -notmatch 'cpu=30\|gpu=30' -or
        $reentryText -notmatch 'ack=backend-ec\+tachs\+watchdog-owned' -or
        $reentryText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
        $reentryText -notmatch ("watchdogStartTicks={0}" -f $serviceStartTicksBefore) -or
        $reentryText -notmatch ("guiPid={0}" -f $app.Id) -or
        $reentryText -notmatch ("guiStartTicks={0}" -f $guiStartTicks)) {
        throw 'M6 controlled post-resume re-entry marker is incomplete.'
    }

    if ($resultText -notmatch '^PASS\|' -or
        $resultText -notmatch ("watchdogPid={0}" -f $servicePidBefore) -or
        $resultText -notmatch ("watchdogStartTicks={0}" -f $serviceStartTicksBefore) -or
        $resultText -notmatch ("guiPid={0}" -f $app.Id) -or
        $resultText -notmatch 'primaryDisplayOff=True' -or
        $resultText -notmatch 'pbtSuspend=True' -or
        $resultText -notmatch 'displayOn=True' -or
        $resultText -notmatch 'acceptedUserResumes=1') {
        throw 'M6 final application result is incomplete.'
    }

    Write-Host ''
    Write-Host 'Step 7: require Windows Kernel-Power Modern Standby evidence...' -ForegroundColor Cyan

    $preSleepTimestamp = Parse-MarkerTimestamp -Text $preSleepText
    $displayOffTimestamp =
        Parse-MarkerDateTimeOffsetField -Text $preSleepText -Name 'displayOffAt'
    $displayOnTimestamp = Parse-MarkerTimestamp -Text $resumeText

    $power = Wait-ModernStandbyKernelEvidence -StartTime $eventWindowStart -DisplayOnTime $displayOnTimestamp.LocalDateTime -AfterRecordId $kernelPowerRecordBoundary

    if ($null -eq $power) {
        throw 'M6 app lifecycle passed, but no Kernel-Power 506 -> 507 Modern Standby evidence was found between the armed boundary and user-visible display-On resume.'
    }

    Write-Host ("Kernel-Power first 506 : {0:O}" -f $power.Sleep.TimeCreated)
    Write-Host ("Kernel-Power final 507 : {0:O}" -f $power.Resume.TimeCreated)
    Write-Host ("Display-Off boundary    : {0:O}" -f $displayOffTimestamp)
    Write-Host ("Pre-sleep completion    : {0:O}" -f $preSleepTimestamp)
    Write-Host ("Display-On resume gate  : {0:O}" -f $displayOnTimestamp)

    if ($displayOffTimestamp.LocalDateTime -lt $eventWindowStart.AddSeconds(-1)) {
        throw 'M6 primary session-display Off boundary occurred before the explicitly armed user sleep window; the requested sleep cycle is not causally isolated.'
    }

    if ($displayOffTimestamp.UtcDateTime -gt $power.Sleep.TimeCreated.ToUniversalTime().AddSeconds(1)) {
        throw 'M6 primary session-display Off boundary occurred too late to correspond to the first Kernel-Power 506 entry boundary.'
    }

    if ($preSleepTimestamp.UtcDateTime -gt $displayOnTimestamp.UtcDateTime) {
        throw 'M6 registered pre-suspend completion marker was not durable before the accepted display-On resume boundary.'
    }

    if ($power.Resume.TimeCreated.ToUniversalTime() -gt $displayOnTimestamp.UtcDateTime.AddSeconds(3)) {
        throw 'M6 final Kernel-Power 507 occurs too late to correspond to the accepted display-On wake boundary.'
    }

    $standbyWindowSeconds = ($power.Resume.TimeCreated - $power.Sleep.TimeCreated).TotalSeconds
    Write-Host ("Modern Standby evidence window: {0:N1} s" -f $standbyWindowSeconds)

    if ($standbyWindowSeconds -lt 15) {
        throw "M6 Modern Standby evidence window was only $([Math]::Round($standbyWindowSeconds,1)) s; require >= 15 s to exclude an accidental immediate wake."
    }

    Write-Host ''
    Write-Host 'Step 8: final process/service/journal/EC proof...' -ForegroundColor Cyan

    try {
        [void]$app.WaitForExit(15000)
        $app.Refresh()
    }
    catch {
    }

    if (-not $app.HasExited) {
        throw 'M6 result was published but the qualification GUI did not exit.'
    }

    [void](Wait-ServiceReady -ExpectedPid $servicePidBefore -ExpectedStartTicks $serviceStartTicksBefore)

    if (Test-Path $journalPath) {
        throw 'M6 final application PASS returned with a durable journal still present.'
    }

    $finalEc = Wait-StableFirmwareAuto -Label 'Independent final EC'
    if (-not $finalEc.Verified) {
        throw "M6 final independent EC proof is not stable FF/FF; last=$($finalEc.State.Raw)."
    }

    if (Test-FailsafeTakeover) {
        throw 'M6 delayed fallback intervened; autonomous normal lifecycle qualification is not proven.'
    }

    $newServiceLog = @()
    if (Test-Path $serviceLog) {
        $newServiceLog = @(@(Get-Content $serviceLog) | Select-Object -Skip $logLineBoundary)
    }

    Write-Host ''
    Write-Host 'M4 service log during M6:' -ForegroundColor Cyan
    $newServiceLog | Select-Object -Last 140

    foreach ($forbidden in @(
        'OWNED heartbeat timeout',
        'WRITE_ARMED operation deadline',
        'RESTORING takeover deadline',
        'OwnershipAmbiguous',
        'WATCHDOG OWNER LOSS:'
    )) {
        if ($newServiceLog | Select-String -SimpleMatch $forbidden -Quiet) {
            throw "M6 normal lifecycle produced forbidden watchdog recovery/failure evidence: '$forbidden'."
        }
    }

    $pass = $true

    Write-Host ''
    Write-Host 'PASS: HP 8C40 M6 Modern Standby lifecycle qualification completed.' -ForegroundColor Green
    Write-Host 'Proven: proactive SESSION_DISPLAY_STATUS Off release -> real Modern Standby -> PBT resume deferred while display Off -> display On single resume gate -> five-snapshot Healthy recovery -> controlled watchdog-backed re-entry -> final FF/FF + journal absent.' -ForegroundColor Green
}
catch {
    $failure = $_.Exception.Message
    Show-Diagnostics
}
finally {
    if (-not $pass -and
        $app -and
        -not $app.HasExited) {
        Write-Warning "M6 failed while GUI PID $($app.Id) is alive; force-killing only that exact controller so the qualified watchdog can recover any retained lease."
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
        try { [void]$app.WaitForExit(5000) } catch {}
    }

    if (Test-Path $journalPath) {
        if ((Get-ServiceProcessId) -le 0) {
            Write-Warning 'M6 cleanup: retained journal with watchdog absent; starting the already-qualified recovery service.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }

        [void](Wait-ForJournalGone -Seconds 25)
    }

    $firmwareSafe = $false

    if (-not (Test-Path $journalPath)) {
        try {
            $cleanupEc = Wait-StableFirmwareAuto -Label 'Post-test EC'
            $firmwareSafe = [bool]$cleanupEc.Verified
        }
        catch {
            Write-Warning "M6 cleanup could not prove stable FF/FF: $($_.Exception.Message)"
        }
    }

    if ($firmwareSafe -and
        $failsafe) {
        $failsafe.Refresh()

        if (-not $failsafe.HasExited) {
            Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
            try { [void]$failsafe.WaitForExit(3000) } catch {}

            Write-Host 'M6 emergency fallback cancelled only after journal absence + stable independent FF/FF proof.' -ForegroundColor Green
        }
        elseif ($pass) {
            $pass = $false
            $failure = 'M6 reached its PASS boundary but the delayed safety fallback had already exited/reached takeover time.'
        }
    }
    elseif ($failsafe -and
            -not $firmwareSafe) {
        Write-Warning "Firmware safety is not independently proven. M6 fallback PID $($failsafe.Id) remains armed; do not close PowerShell or power-cycle during its safety window."
    }

    if ($firmwareSafe -and
        $serviceSetupTouched) {
        try {
            Restore-M4Baseline
            Write-Host 'M4 baseline restored: Manual/stopped; temporary M6 SCM recovery actions removed.' -ForegroundColor Green
        }
        catch {
            if ($pass) {
                $pass = $false
                $failure = "M6 lifecycle passed, but M4 baseline cleanup failed: $($_.Exception.Message)"
            }
            else {
                Write-Warning "M6 firmware is safe, but M4 baseline cleanup failed: $($_.Exception.Message)"
            }
        }
    }
    elseif (Test-Path $journalPath) {
        Write-Host 'CRITICAL: M6 durable journal remains. It was NOT deleted.' -ForegroundColor Red
        Get-Content $journalPath
    }

    if ($firmwareSafe) {
        New-Item -ItemType Directory -Force -Path $diagnosticRoot | Out-Null

        try {
            & (Join-Path $PSScriptRoot 'collect-power-transition-diagnostics.ps1') -OutputDirectory $diagnosticRoot
        }
        catch {
            Write-Warning "M6 post-transition diagnostics collection failed: $($_.Exception.Message)"
        }

        Write-Host "M6 diagnostics directory: $diagnosticRoot"
    }
    else {
        Write-Warning 'M6 power diagnostics were deferred because firmware safety has not yet been independently proven.'
    }
}

if (-not $pass) {
    throw "M6 FAILED: $failure"
}
