param(
    [ValidateRange(3000, 10000)]
    [int]$RestartDelayMs = 5000
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM4'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$statusPath = Join-Path $serviceRoot 'state\m4-8c40.status.json'
$journalPath = Join-Path $serviceRoot 'state\lease.json'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'M5C preflight must be run from an elevated PowerShell.'
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

    $m = [regex]::Match($line, '^setpoint CPU=(\d+) GPU=(\d+)$')
    if (-not $m.Success) {
        throw "Could not parse HP 8C40 setpoints from: $line"
    }

    [pscustomobject]@{
        Cpu = [int]$m.Groups[1].Value
        Gpu = [int]$m.Groups[2].Value
        Raw = $line
    }
}

function Get-ServicePid {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.State -ne 'Running') {
        return 0
    }

    return [int]$svc.ProcessId
}

function Wait-Ready {
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

    throw 'Timed out waiting for exact-target M4 Ready state.'
}

function Restore-M4Baseline {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')

    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    if ($svc.StartType -ne 'Manual') {
        throw "M4 baseline restore failed: StartType=$($svc.StartType), expected Manual."
    }

    if ($svc.Status -ne 'Stopped') {
        throw "M4 baseline restore failed: Status=$($svc.Status), expected Stopped."
    }
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M5C DOUBLE-DEATH PREFLIGHT' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This stage performs NO fan-level write and NO fault injection.' -ForegroundColor Yellow
Write-Host 'It validates the exact target, clean durable state, isolated LocalSystem service,' -ForegroundColor Yellow
Write-Host 'and the temporary SCM restart policy required by the later M5C physical gate.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "M5C preflight refused while '$name' is running."
    }
}

Write-Host 'Step 1: build + current watchdog/safety/backend regressions...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-m5a-8c40-invariants.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'test-watchdog-m5b-8c40-invariants.ps1')
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
Write-Host 'Step 2: clean firmware/durable-state preflight...' -ForegroundColor Cyan
$baseline = Read-8C40Setpoint
Write-Host $baseline.Raw

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    throw "M5C preflight requires firmware-owned FF/FF; observed $($baseline.Cpu)/$($baseline.Gpu)."
}

if (Test-Path $journalPath) {
    Write-Host 'Existing durable lease evidence:' -ForegroundColor Red
    Get-Content $journalPath
    throw 'M5C preflight refuses to overwrite or delete retained durable ownership evidence.'
}

Write-Host 'Durable journal      : ABSENT' -ForegroundColor Green

$preflightSucceeded = $false
$baselineRestored = $false

try {
    Write-Host ''
    Write-Host 'Step 3: install isolated M4 service and validate exact-target identity...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'install-watchdog-m4-8c40.ps1')

    Remove-Item $statusPath -Force -ErrorAction SilentlyContinue
    Start-Service -Name $serviceName

    $pidDeadline = (Get-Date).AddSeconds(10)
    $servicePid = 0

    while ($servicePid -le 0 -and (Get-Date) -lt $pidDeadline) {
        $servicePid = Get-ServicePid
        if ($servicePid -le 0) {
            Start-Sleep -Milliseconds 100
        }
    }

    if ($servicePid -le 0) {
        throw 'M5C preflight could not resolve the LocalSystem service PID.'
    }

    $status = Wait-Ready -ExpectedPid $servicePid

    Write-Host "Ready              : $($status.Ready)"
    Write-Host "Session            : $($status.SessionId)"
    Write-Host "Account            : $($status.AccountName)"
    Write-Host "Target             : $($status.TargetProfileId)"
    Write-Host "Pipe               : $($status.PipeName)"
    Write-Host "Service PID        : $servicePid"
    Write-Host "Startup recovery   : $($status.RecoveryDisposition)"

    Write-Host ''
    Write-Host 'Step 4: validate temporary M5C SCM recovery configuration...' -ForegroundColor Cyan

    & sc.exe failure $serviceName reset= 86400 actions= "restart/$RestartDelayMs/restart/5000/restart/10000" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not configure temporary M5C recovery policy; sc.exe exit=$LASTEXITCODE."
    }

    & sc.exe failureflag $serviceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enable failure actions; sc.exe exit=$LASTEXITCODE."
    }

    $qfailure = (& sc.exe qfailure $serviceName 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not query temporary M5C recovery policy; sc.exe exit=$LASTEXITCODE."
    }

    if ($qfailure -notmatch ("(?s){0}\s*ms.*5000\s*ms.*10000\s*ms" -f $RestartDelayMs)) {
        throw "Temporary M5C recovery policy does not contain ordered $RestartDelayMs/5000/10000 ms restarts. Raw output: $qfailure"
    }

    Write-Host "Temporary SCM policy: $RestartDelayMs ms / 5000 ms / 10000 ms" -ForegroundColor Green
    Write-Host 'No fan-level write has been issued.' -ForegroundColor Green
    $preflightSucceeded = $true
}
finally {
    Write-Host ''
    Write-Host 'Step 5: restore ordinary M4 qualification baseline...' -ForegroundColor Cyan

    if (Test-Path $journalPath) {
        Write-Host 'CRITICAL: a durable journal appeared during a no-write preflight. It will NOT be deleted or overwritten.' -ForegroundColor Red
        Get-Content $journalPath
    }
    else {
        Restore-M4Baseline
        $baselineRestored = $true
    }
}

if (-not $preflightSucceeded) {
    throw 'M5C preflight did not complete its no-write validation sequence.'
}

if (-not $baselineRestored) {
    throw 'M5C preflight could not restore the ordinary M4 service baseline without risking retained durable evidence.'
}

$final = Read-8C40Setpoint
Write-Host "Final EC            : $($final.Raw)"

if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
    throw "M5C preflight final EC is not FF/FF: $($final.Cpu)/$($final.Gpu)."
}

if (Test-Path $journalPath) {
    throw 'M5C preflight unexpectedly created a durable lease journal.'
}

Write-Host ''
Write-Host 'PASS: HP 8C40 M5C no-write preflight completed.' -ForegroundColor Green
Write-Host 'Validated: exact target + LocalSystem/Session0 + isolated pipe + clean FF/FF + absent journal + temporary SCM restart policy + restored Manual/stopped service baseline.' -ForegroundColor Green
