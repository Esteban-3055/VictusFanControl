param(
    [ValidateRange(90, 300)]
    [int]$FailsafeDelaySeconds = 120
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdog'
$root = Join-Path $env:LOCALAPPDATA 'VictusFanControl'
$readyPath = Join-Path $root 'gate-d.ready'
$resultPath = Join-Path $root 'gate-d.result'
$failsafeLog = Join-Path $root 'watchdog-gate-d-failsafe.log'
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
$pass = $false
$failure = $null
$servicePidBefore = 0
$logLineBoundary = 0

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This Gate D hardware test must be run from an elevated PowerShell.'
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

function Get-ServiceProcessId {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
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

Assert-Administrator

Write-Host 'VictusFanControl - WATCHDOG GATE D (REAL LEASE + FORCED GUI KILL)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This test uses the real LocalSystem watchdog service and the real HP backend.' -ForegroundColor Yellow
Write-Host 'The parent PowerShell NEVER invokes --restore-hp-auto.' -ForegroundColor Yellow
Write-Host 'An independent delayed emergency fallback is armed before 30/30, but a PASS' -ForegroundColor Yellow
Write-Host 'requires the watchdog service to restore and clear its journal first.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Keep OMEN Gaming Hub open with your normal undervolt.'
Write-Host 'Close OmenMon, OmenMon-Reborn and any normal VictusFanControl GUI.'
Write-Host 'Do not run a game, benchmark or stress test.'
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        Write-Error "Refusing Gate D while process '$name' is running. Close it and retry."
        exit 1
    }
}

Write-Host 'Step 1: build + all synthetic regressions...' -ForegroundColor Cyan
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

$baseline = Read-EcState
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    Write-Error "Gate D requires firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu)."
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
Write-Host 'Step 3: install and start the persistent LocalSystem watchdog...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-gate-d.ps1')

Start-Service -Name $serviceName
$service = Get-Service -Name $serviceName
$service.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))

if (-not (Wait-ForFile -Path $serviceStatusPath -Seconds 15)) {
    throw 'Gate D service did not publish its startup status.'
}

$status = Get-Content $serviceStatusPath -Raw | ConvertFrom-Json
Write-Host "Service Ready       : $($status.Ready)"
Write-Host "Service Blocked     : $($status.Blocked)"
Write-Host "Service Session     : $($status.SessionId)"
Write-Host "Service Account     : $($status.AccountName)"
Write-Host "Startup recovery    : $($status.RecoveryDisposition)"
Write-Host "Startup detail      : $($status.Detail)"

if (-not $status.Ready -or
    $status.Blocked -or
    [int]$status.SessionId -ne 0 -or
    $status.AccountName -notmatch 'SYSTEM$') {
    throw 'Gate D service is not Ready under LocalSystem/Session 0.'
}

$servicePidBefore = Get-ServiceProcessId
if ($servicePidBefore -le 0) {
    throw 'Could not resolve the running Gate D service PID.'
}

Write-Host "Service PID         : $servicePidBefore"

Write-Host ''
Write-Host 'Gate D will now acquire a durable lease, apply validated 30/30 and force-kill ONLY the GUI.' -ForegroundColor Yellow
$confirm = Read-Host 'Type GATED30 to continue'
if ($confirm -cne 'GATED30') {
    Write-Host 'Cancelled before any fan write.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Remove-Item $readyPath, $resultPath, $failsafeLog -Force -ErrorAction SilentlyContinue

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
    Write-Host 'Step 5: launch watchdog-protected GUI and wait for backend+lease OWNED...' -ForegroundColor Cyan

    $proc = Start-Process -FilePath $app -ArgumentList @(
        '--gate-d-custom-test',
        '--gate-d-test-token',
        '88F8-GATED30'
    ) -PassThru

    $deadline = (Get-Date).AddSeconds(75)
    while (-not (Test-Path $readyPath)) {
        if ($proc.HasExited) {
            $detail = if (Test-Path $resultPath) {
                Get-Content $resultPath -Raw
            } else {
                'no Gate D result marker'
            }

            throw "VictusFanControl.App exited before Gate D READY. ExitCode=$($proc.ExitCode). $detail"
        }

        if ((Get-Date) -gt $deadline) {
            throw 'Timed out waiting for watchdog-protected Gate D READY.'
        }

        Start-Sleep -Milliseconds 250
    }

    $readyReached = $true
    Write-Host 'Gate D READY marker:' -ForegroundColor Green
    Get-Content $readyPath

    if (-not (Test-Path $journalPath)) {
        throw 'GUI reported READY but the watchdog durable lease journal is missing.'
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $journalOwned = Test-JournalOwnedPhase -Phase $journal.Phase
    $procStartTicks = [long]$proc.StartTime.ToUniversalTime().Ticks
    $journalPhaseDisplay = if ($journalOwned) {
        "$($journal.Phase) (Owned)"
    } else {
        "$($journal.Phase)"
    }

    Write-Host "Journal phase        : $journalPhaseDisplay"
    Write-Host "Journal generation   : $($journal.Generation)"
    Write-Host "Journal controller   : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "GUI identity         : PID=$($proc.Id) startTicks=$procStartTicks"
    Write-Host "Journal owned target : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

    if (-not $journalOwned -or
        [int]$journal.Controller.ProcessId -ne $proc.Id -or
        [long]$journal.Controller.ProcessStartUtcTicks -ne $procStartTicks -or
        [int]$journal.Owned.Cpu -ne 30 -or
        [int]$journal.Owned.Gpu -ne 30) {
        throw 'READY marker is not backed by a durable watchdog OWNED 30/30 journal bound to the exact GUI PID + creation time.'
    }

    if ((Get-Service -Name $serviceName).Status -ne 'Running') {
        throw 'Watchdog service stopped before the forced-kill boundary.'
    }

    $logLineBoundary = if (Test-Path $serviceLog) {
        @(Get-Content $serviceLog).Count
    } else {
        0
    }

    Write-Host ''
    Write-Host "Step 6: FORCE-KILL exact GUI PID $($proc.Id). No managed GUI cleanup can run..." -ForegroundColor Yellow
    Stop-Process -Id $proc.Id -Force
    $proc.WaitForExit()

    Write-Host 'GUI terminated. Parent shell will NOT send any HP restore command.' -ForegroundColor Yellow

    Write-Host ''
    Write-Host 'Step 7: wait only for watchdog-owned journal recovery...' -ForegroundColor Cyan

    if (-not (Wait-ForJournalGone -Seconds 15)) {
        throw 'Watchdog did not clear the durable lease within 15 s after GUI death.'
    }

    Start-Sleep -Milliseconds 400

    $final = Read-EcState
    Write-Host "Independent final EC : $($final.Raw)"

    if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
        throw "Watchdog journal cleared but EC is not FF/FF: $($final.Cpu)/$($final.Gpu)."
    }

    $servicePidAfter = Get-ServiceProcessId
    Write-Host "Service PID after    : $servicePidAfter"

    if ($servicePidAfter -ne $servicePidBefore) {
        throw "Gate D service process changed PID during the test ($servicePidBefore -> $servicePidAfter); isolate service-restart recovery in Gate F instead."
    }

    $newLog = @()
    if (Test-Path $serviceLog) {
        $allLog = @(Get-Content $serviceLog)
        $newLog = @($allLog | Select-Object -Skip $logLineBoundary)
    }

    Write-Host ''
    Write-Host 'Gate D service evidence after GUI kill:' -ForegroundColor Cyan
    $newLog | Select-Object -Last 30

    $restoreEvidence = $newLog |
        Where-Object {
            (
                $_ -match 'WATCHDOG OWNER LOSS:' -or
                $_ -match 'GATE D RECOVERY'
            ) -and
            $_ -match 'disposition=RestoredFirmware'
        }

    if (-not $restoreEvidence) {
        throw 'FF/FF was recovered but the Gate D service log did not record RestoredFirmware evidence from either the pipe owner-loss path or the independent process monitor.'
    }

    $pass = $true
}
catch {
    $failure = $_.Exception.Message
}
finally {
    # There is intentionally no dotnet --restore-hp-auto call in this script.
    # If the GUI remains alive after an earlier test failure, terminate only
    # that exact test process so the watchdog becomes responsible for cleanup.
    if ($proc -and -not $proc.HasExited) {
        Write-Warning 'Gate D test failed while the test GUI is still alive; force-killing the exact GUI PID so watchdog/fallback recovery owns cleanup.'
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { $proc.WaitForExit(5000) | Out-Null } catch {}
    }

    $firmwareSafe = $false

    if ($readyReached -and (Test-Path $journalPath)) {
        Write-Host 'Waiting up to 15 s for watchdog recovery after cleanup kill...' -ForegroundColor Cyan
        [void](Wait-ForJournalGone -Seconds 15)
    }

    if (-not (Test-Path $journalPath)) {
        try {
            $cleanupState = Read-EcState
            Write-Host "Post-test EC check   : $($cleanupState.Raw)"
            $firmwareSafe =
                $cleanupState.Cpu -eq 255 -and
                $cleanupState.Gpu -eq 255
        }
        catch {
            Write-Warning "Could not perform the final read-only EC check: $($_.Exception.Message)"
        }
    }

    if ($firmwareSafe -and $failsafe -and -not $failsafe.HasExited) {
        Stop-Process -Id $failsafe.Id -Force -ErrorAction SilentlyContinue
        Write-Host 'Emergency fallback cancelled only after watchdog journal was gone and EC FF/FF was independently verified.' -ForegroundColor Green
    }
    elseif ($failsafe -and -not $firmwareSafe) {
        Write-Warning "FF/FF is not yet independently proven. Emergency fallback PID $($failsafe.Id) remains armed; do not close PowerShell or power-cycle during its $FailsafeDelaySeconds s window."
    }
}

if (-not $pass) {
    Write-Host ''
    Write-Host 'Gate D did NOT pass.' -ForegroundColor Red
    Write-Host "Failure: $failure"

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent Gate D watchdog log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 60
    }

    exit 101
}

Write-Host ''
Write-Host 'Step 8: verify OMEN Gaming Hub undervolt...' -ForegroundColor Cyan
$post = Read-Host 'Type SAME if the CPU undervolt is unchanged, or CHANGED if it changed'

if ($post -cne 'SAME') {
    if ($post -ceq 'CHANGED') {
        Write-Warning 'Undervolt preservation FAILED/CHANGED.'
        exit 102
    }

    Write-Warning 'Undervolt preservation was not confirmed.'
    exit 103
}

Write-Host ''
Write-Host 'PASS: Gate D real lease survived forced GUI death and the independent LocalSystem watchdog restored HP firmware authority.' -ForegroundColor Green
Write-Host 'Verified: durable OWNED 30/30 -> exact GUI kill -> service owner-loss recovery -> FF/FF -> journal cleared.' -ForegroundColor Green
Write-Host 'Parent PowerShell issued no HP restore command; emergency fallback did not fire.' -ForegroundColor Green
Write-Host 'OMEN Gaming Hub undervolt: SAME (user-confirmed).' -ForegroundColor Green
exit 0
