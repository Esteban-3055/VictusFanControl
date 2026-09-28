param(
    [ValidateRange(10, 30)]
    [int]$RecoveryTimeoutSeconds = 15
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
$readyPath = Join-Path $localRoot 'm5a-8c40-controller.ready.json'

$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'
$requiredToken = '8C40-M5A-CONTROLLER-DEATH30'

$controller = $null
$readyReached = $false
$pass = $false
$failure = $null
$servicePidBefore = 0
$logLineBoundary = 0

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'M5A must be run from an elevated PowerShell.'
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

function Wait-M4Ready {
    param([int]$Seconds = 20)

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statusPath) {
            try {
                $status = Get-Content $statusPath -Raw | ConvertFrom-Json

                if ($status.Ready -and
                    -not $status.Blocked -and
                    [int]$status.SessionId -eq 0 -and
                    $status.AccountName -match 'SYSTEM$' -and
                    $status.TargetProfileId -ceq 'HP-8C40-9D0R1LA-F18' -and
                    $status.PipeName -ceq 'VictusFanControl.Watchdog.M4.8C40.v2') {
                    return $status
                }
            }
            catch {
            }
        }

        Start-Sleep -Milliseconds 200
    }

    throw 'Timed out waiting for the HP 8C40 M4 service Ready state.'
}

function Get-ServiceProcessId {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.State -ne 'Running') {
        return 0
    }

    return [int]$svc.ProcessId
}

function Wait-ForFile {
    param(
        [string]$Path,
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while (-not (Test-Path $Path) -and
           (Get-Date) -lt $deadline) {
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

function Show-Diagnostics {
    Write-Host ''

    if (Test-Path $readyPath) {
        Write-Host 'M5A READY marker:' -ForegroundColor Cyan
        Get-Content $readyPath
    }

    if (Test-Path $statusPath) {
        Write-Host ''
        Write-Host 'M4 service status:' -ForegroundColor Cyan
        Get-Content $statusPath
    }

    if (Test-Path $journalPath) {
        Write-Host ''
        Write-Host 'M4 durable journal:' -ForegroundColor Yellow
        Get-Content $journalPath
    }

    if (Test-Path $serviceLog) {
        Write-Host ''
        Write-Host 'Recent M4 log:' -ForegroundColor Cyan
        Get-Content $serviceLog | Select-Object -Last 120
    }

    Write-Host ''
    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M5A CONTROLLER-DEATH RECOVERY' -ForegroundColor Cyan
Write-Host ''
Write-Host 'M5A proves that the still-running LocalSystem watchdog restores firmware' -ForegroundColor Yellow
Write-Host 'after the exact watchdog-owned controller process is force-killed.' -ForegroundColor Yellow
Write-Host 'The parent PowerShell does NOT issue a direct HP/WMI restore command.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "Refusing M5A while process '$name' is running. Close it and retry."
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
    throw "M5A requires firmware-owned FF/FF baseline; observed $($baseline.Cpu)/$($baseline.Gpu)."
}

if (Test-Path $journalPath) {
    Write-Host 'Durable lease evidence already exists. M5A will not delete it.' -ForegroundColor Red
    Get-Content $journalPath
    throw "M5A requires an absent journal before installation: $journalPath"
}

Write-Host ''
Write-Host 'Step 3: install/start isolated LocalSystem M4 recovery service...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')

Remove-Item $statusPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $localRoot | Out-Null
Remove-Item $readyPath -Force -ErrorAction SilentlyContinue

Start-Service -Name $serviceName
$status = Wait-M4Ready

$servicePidBefore = Get-ServiceProcessId
if ($servicePidBefore -le 0) {
    throw 'Could not resolve the running M4 service PID.'
}

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
Write-Host 'ACTIVE M5A FAILURE-INJECTION BOUNDARY' -ForegroundColor Yellow
Write-Host 'A child controller will acquire watchdog-protected 30/30 and then be force-killed.' -ForegroundColor Yellow
Write-Host 'Do not suspend/hibernate, close the lid, start a workload, kill the watchdog, or close this shell.' -ForegroundColor Yellow
Write-Host ''
$token = Read-Host "Type exactly $requiredToken to continue"
if ($token -cne $requiredToken) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    throw 'M5A cancelled before any fan write.'
}

try {
    Write-Host ''
    Write-Host 'Step 4: launch the exact-target M5A controller and wait for durable OWNED 30/30...' -ForegroundColor Cyan

    $controller = Start-Process -FilePath 'dotnet' -ArgumentList @(
        $cli,
        '--8c40-m5a-controller-death-arm',
        '--8c40-m5a-token',
        $requiredToken,
        '--8c40-m5a-ready-path',
        $readyPath,
        '--modules-dir',
        $modulesDir
    ) -PassThru -NoNewWindow

    $deadline = (Get-Date).AddSeconds(45)

    while (-not (Test-Path $readyPath)) {
        if ($controller.HasExited) {
            throw "M5A controller exited before READY. ExitCode=$($controller.ExitCode)."
        }

        if ((Get-Date) -gt $deadline) {
            throw 'Timed out waiting for M5A controller READY.'
        }

        Start-Sleep -Milliseconds 100
    }

    $readyReached = $true
    $ready = Get-Content $readyPath -Raw | ConvertFrom-Json

    Write-Host 'M5A READY marker:' -ForegroundColor Green
    Get-Content $readyPath

    $controllerStartTicks =
        [long]$controller.StartTime.ToUniversalTime().Ticks

    if ([int]$ready.SchemaVersion -ne 1 -or
        $ready.Gate -cne 'M5A' -or
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
        throw 'M5A READY marker did not prove exact controller identity plus healthy backend EC+tachs+watchdog OWNED 30/30.'
    }

    if (-not (Test-Path $journalPath)) {
        throw 'M5A READY exists but the durable watchdog journal is missing.'
    }

    $journal = Get-Content $journalPath -Raw | ConvertFrom-Json
    $journalOwned = Test-JournalOwnedPhase -Phase $journal.Phase

    Write-Host "Journal phase      : $($journal.Phase)"
    Write-Host "Journal generation : $($journal.Generation)"
    Write-Host "Journal controller : PID=$($journal.Controller.ProcessId) startTicks=$($journal.Controller.ProcessStartUtcTicks)"
    Write-Host "Journal target     : $($journal.Owned.Cpu)/$($journal.Owned.Gpu)"

    if ([int]$journal.SchemaVersion -ne 2 -or
        $journal.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not $journalOwned -or
        [int]$journal.Controller.ProcessId -ne $controller.Id -or
        [long]$journal.Controller.ProcessStartUtcTicks -ne $controllerStartTicks -or
        [int]$journal.Owned.Cpu -ne 30 -or
        [int]$journal.Owned.Gpu -ne 30) {
        throw 'M5A fault boundary is not backed by durable OWNED 30/30 bound to the exact controller PID + creation time.'
    }

    if ($controller.HasExited) {
        throw 'M5A controller exited before the intentional force-kill.'
    }

    if ((Get-ServiceProcessId) -ne $servicePidBefore) {
        throw 'M5A watchdog service PID changed before controller-death injection.'
    }

    # No independent EC probe is opened while the controller owns Custom.
    # READY was written only after backend EC + dual-tach ACK and durable Commit.
    Write-Host ''
    Write-Host "Step 5: FORCE-KILL exact controller PID $($controller.Id)..." -ForegroundColor Yellow

    $recoveryWatch = [Diagnostics.Stopwatch]::StartNew()

    Stop-Process -Id $controller.Id -Force
    try {
        [void]$controller.WaitForExit(5000)
    }
    catch {
    }

    if (-not $controller.HasExited) {
        throw 'M5A controller process did not terminate after force-kill.'
    }

    Write-Host 'Controller is dead. The parent shell will not issue any HP restore command.' -ForegroundColor Yellow

    Write-Host ''
    Write-Host 'Step 6: wait only for LocalSystem watchdog owner-loss recovery...' -ForegroundColor Cyan

    if (-not (Wait-ForJournalGone -Seconds $RecoveryTimeoutSeconds)) {
        throw "M5A watchdog did not clear the durable journal within $RecoveryTimeoutSeconds s after controller death."
    }

    $recoveryWatch.Stop()

    Start-Sleep -Milliseconds 400

    $final = Read-8C40Setpoint
    Write-Host "Independent final EC : $($final.Raw)"
    Write-Host ("Recovery elapsed     : {0:N3} s" -f $recoveryWatch.Elapsed.TotalSeconds)

    if ($final.Cpu -ne 255 -or
        $final.Gpu -ne 255) {
        throw "M5A journal cleared but independent EC is not FF/FF: $($final.Cpu)/$($final.Gpu)."
    }

    $servicePidAfter = Get-ServiceProcessId

    if ($servicePidAfter -ne $servicePidBefore) {
        throw "M5A service PID changed during controller-death recovery ($servicePidBefore -> $servicePidAfter). Service-restart recovery belongs to later M5 gates."
    }

    $newLog = @()
    if (Test-Path $serviceLog) {
        $allLog = @(Get-Content $serviceLog)
        $newLog = @($allLog | Select-Object -Skip $logLineBoundary)
    }

    Write-Host ''
    Write-Host 'M5A watchdog evidence:' -ForegroundColor Cyan
    $newLog | Select-Object -Last 60

    $restoreEvidence =
        $newLog |
        Where-Object {
            (
                $_ -match 'WATCHDOG OWNER LOSS:' -or
                $_ -match 'M4 RECOVERY disposition='
            ) -and
            $_ -match 'RestoredFirmware' -and
            $_ -match 'controller'
        }

    if (-not $restoreEvidence) {
        throw 'M5A reached FF/FF but the service log did not causally record controller-death RestoredFirmware recovery.'
    }

    if (Test-Path $journalPath) {
        throw 'M5A final journal unexpectedly reappeared after recovery.'
    }

    $pass = $true

    Write-Host ''
    Write-Host 'PASS: HP 8C40 M5A controller-death recovery completed.' -ForegroundColor Green
    Write-Host 'Proven: durable OWNED 30/30 -> exact controller force-kill -> same LocalSystem watchdog PID -> verified FF/FF -> journal cleared.' -ForegroundColor Green
    Write-Host 'M5B watchdog-death and M5C double-death remain separate gates.' -ForegroundColor Yellow
}
catch {
    $failure = $_.Exception.Message
    Show-Diagnostics
}
finally {
    if ($controller -and -not $controller.HasExited) {
        Write-Warning 'M5A did not reach its intended kill boundary cleanly; force-killing the exact test controller so watchdog recovery owns cleanup.'
        Stop-Process -Id $controller.Id -Force -ErrorAction SilentlyContinue
        try {
            [void]$controller.WaitForExit(5000)
        }
        catch {
        }
    }

    if ($readyReached -and (Test-Path $journalPath)) {
        Write-Host 'Cleanup: waiting for normal watchdog recovery...' -ForegroundColor Cyan
        [void](Wait-ForJournalGone -Seconds $RecoveryTimeoutSeconds)
    }

    if (Test-Path $journalPath) {
        Write-Warning 'Cleanup: durable journal remains. Requesting service-stop recovery without deleting evidence.'

        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            try {
                $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
            }
            catch {
            }
        }

        if (Test-Path $journalPath) {
            Write-Warning 'Cleanup: journal survived service stop. Starting the same qualification service for startup recovery.'
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            [void](Wait-ForJournalGone -Seconds $RecoveryTimeoutSeconds)
        }
    }

    $firmwareSafe = $false

    if (-not (Test-Path $journalPath)) {
        try {
            $cleanupState = Read-8C40Setpoint
            Write-Host "Post-test EC check     : $($cleanupState.Raw)"
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
        Write-Host 'CRITICAL: durable M5A ownership evidence remains. It was NOT deleted.' -ForegroundColor Red
        Write-Host "Journal: $journalPath" -ForegroundColor Red
        Write-Host 'Do not reinstall the service or run another fan-write gate until this retained state is inspected/recovered.' -ForegroundColor Red
    }
}

if (-not $pass) {
    throw "M5A FAILED: $failure"
}
