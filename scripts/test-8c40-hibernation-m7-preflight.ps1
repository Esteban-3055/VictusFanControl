$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM4'
$journalPath = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4\state\lease.json'
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'M7 preflight must run from an elevated PowerShell.'
    }
}

function Assert-Exact8C40Target {
    $board = Get-CimInstance Win32_BaseBoard -ErrorAction Stop
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios = Get-CimInstance Win32_BIOS -ErrorAction Stop

    $boardManufacturer = ([string]$board.Manufacturer).Trim()
    $boardProduct = ([string]$board.Product).Trim()
    $boardVersion = ([string]$board.Version).Trim()
    $systemManufacturer = ([string]$system.Manufacturer).Trim()
    $systemModel = ([string]$system.Model).Trim()
    $sku = ([string]$system.SystemSKUNumber).Trim()
    $skuBase = ($sku -split '#', 2)[0].Trim()
    $biosText = @(
        ([string]$bios.SMBIOSBIOSVersion).Trim(),
        ([string]$bios.Version).Trim()
    ) -join ' | '

    Write-Host ("Board : {0} / {1} / {2}" -f $boardManufacturer, $boardProduct, $boardVersion)
    Write-Host ("System: {0} / {1}" -f $systemManufacturer, $systemModel)
    Write-Host ("SKU   : {0}" -f $sku)
    Write-Host ("BIOS  : {0}" -f $biosText)

    if ($boardManufacturer -cne 'HP' -or
        $boardProduct -cne '8C40' -or
        $boardVersion -cne '63.43' -or
        $systemManufacturer -cne 'HP' -or
        $systemModel -cne 'Victus by HP Gaming Laptop 15-fa1xxx' -or
        $skuBase -cne '9D0R1LA' -or
        $biosText -notmatch '(^|[^A-Za-z0-9])F\.18([^A-Za-z0-9]|$)') {
        throw 'M7 preflight exact-target fingerprint mismatch.'
    }
}

function Assert-HibernationAvailable {
    $powerA = (powercfg /a 2>&1 | Out-String)
    Write-Host $powerA

    if ($powerA -notmatch '(?im)^\s*(Hibernate|Hibernar)\s*$') {
        throw 'M7 preflight requires Windows hibernation to be available according to powercfg /a.'
    }

    $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($battery) {
        Write-Host ("Battery: status={0}, charge={1}%" -f $battery.BatteryStatus, $battery.EstimatedChargeRemaining)

        if ($null -ne $battery.EstimatedChargeRemaining -and
            [int]$battery.EstimatedChargeRemaining -lt 20) {
            throw "M7 preflight requires at least 20% reported battery charge; observed $($battery.EstimatedChargeRemaining)%."
        }
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

    $match = [regex]::Match($line, '^setpoint CPU=(\d+) GPU=(\d+)$')

    if (-not $match.Success) {
        throw "Could not parse HP 8C40 setpoint line: $line"
    }

    [pscustomobject]@{
        Cpu = [int]$match.Groups[1].Value
        Gpu = [int]$match.Groups[2].Value
        Raw = $line
    }
}

function Assert-StableFirmwareBaseline {
    $consecutive = 0
    $last = $null

    for ($sample = 1; $sample -le 8; $sample++) {
        $last = Read-8C40Setpoint
        Write-Host ("EC sample {0}/8: {1}" -f $sample, $last.Raw)

        if ($last.Cpu -eq 255 -and
            $last.Gpu -eq 255) {
            $consecutive++

            if ($consecutive -ge 2) {
                return
            }
        }
        else {
            $consecutive = 0
        }

        Start-Sleep -Milliseconds 75
    }

    throw "M7 preflight could not prove two consecutive FF/FF samples. Last=$($last.Raw)"
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 M7 HIBERNATION NO-WRITE PREFLIGHT' -ForegroundColor Cyan
Write-Host ''
Write-Host 'This preflight performs no fan write, no firmware restore, no watchdog lease, no service start/stop and no sleep transition.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon','OmenMon-Reborn','VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "M7 preflight refused while '$name' is running."
    }
}

Write-Host 'Step 1: exact HP 8C40 target fingerprint + hibernation capability...' -ForegroundColor Cyan
Assert-Exact8C40Target
Assert-HibernationAvailable

Write-Host ''
Write-Host 'Step 2: durable/service baseline without mutation...' -ForegroundColor Cyan

if (Test-Path $journalPath) {
    Get-Content $journalPath
    throw 'M7 preflight refuses retained watchdog journal evidence.'
}

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($svc) {
    Write-Host "M4 service: StartType=$($svc.StartType), Status=$($svc.Status)"

    if ($svc.Status -ne 'Stopped') {
        throw 'M7 no-write preflight requires the qualification service stopped; it will not stop it automatically.'
    }

    if ($svc.StartType -ne 'Manual') {
        throw "M7 no-write preflight requires M4 Manual startup; observed $($svc.StartType)."
    }
}
else {
    Write-Host 'M4 service: not currently installed (acceptable for no-write preflight).'
}

Write-Host ''
Write-Host 'Step 3: build + complete static/synthetic regressions...' -ForegroundColor Cyan

dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($invariant in @(
    'test-watchdog-m5a-8c40-invariants.ps1',
    'test-watchdog-m5b-8c40-invariants.ps1',
    'test-watchdog-m5c-preflight-invariants.ps1',
    'test-watchdog-m5c-double-death-invariants.ps1',
    'test-watchdog-m5d-write-armed-invariants.ps1',
    'test-watchdog-m5e-write-armed-double-death-invariants.ps1',
    'test-8c40-modern-standby-m6-invariants.ps1',
    'test-8c40-hibernation-m7-invariants.ps1',
    'test-8c40-hibernation-m7-harness-invariants.ps1'
)) {
    & (Join-Path $PSScriptRoot $invariant)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet run --project .\src\VictusFanControl.ModernStandbyProbe -c Release --no-build -- --self-test
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
Write-Host 'Step 4: Windows sleep capability snapshot...' -ForegroundColor Cyan
$powerA = (powercfg /a 2>&1 | Out-String)
Write-Host $powerA

if ([string]::IsNullOrWhiteSpace($powerA)) {
    throw 'powercfg /a returned no capability information.'
}

Write-Host 'Current power requests:' -ForegroundColor Cyan
powercfg /requests | Out-Host

Write-Host ''
Write-Host 'Step 5: independent stable firmware-owned EC baseline...' -ForegroundColor Cyan
Assert-StableFirmwareBaseline

if (Test-Path $journalPath) {
    throw 'M7 no-write preflight unexpectedly created a watchdog journal.'
}

$svcAfter = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($svc -and
    ($svcAfter.Status -ne 'Stopped' -or
     $svcAfter.StartType -ne 'Manual')) {
    throw "M7 no-write preflight mutated M4 service state: StartType=$($svcAfter.StartType), Status=$($svcAfter.Status)."
}

Write-Host ''
Write-Host 'PASS: HP 8C40 M7 no-write hibernation preflight completed.' -ForegroundColor Green
Write-Host 'No fan write, watchdog lease, service mutation, fault injection or sleep transition was performed.' -ForegroundColor Green
