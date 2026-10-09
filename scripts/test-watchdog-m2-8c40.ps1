param(
    [ValidateRange(1, 3)]
    [int]$RestartCycles = 2
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$serviceName = 'VictusFanControlWatchdogM2'
$serviceRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM2'
$resultPath = Join-Path $serviceRoot 'state\m2-8c40.result.json'
$journalPath = Join-Path $serviceRoot 'state\lease.json'
$logPath = Join-Path $serviceRoot ("logs\watchdog-m2-8c40-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$modulesDir = Join-Path $repoRoot 'modules'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This M2 hardware gate must be run from an elevated PowerShell.'
    }
}

function Read-8C40Setpoint {
    $output = (& dotnet $cli --probe-8c40-setpoint --modules-dir $modulesDir 2>&1 | Out-String)
    Write-Host $output.TrimEnd()

    $line = ($output -split "[\r\n]+" |
        Where-Object { $_ -match '^setpoint CPU=' } |
        Select-Object -Last 1)

    if (-not $line) {
        throw "Could not parse HP 8C40 read-only setpoint probe. Raw output: $output"
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

function Wait-ForFile {
    param(
        [string]$Path,
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Path $Path) -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }

    return (Test-Path $Path)
}

function Show-Diagnostics {
    if (Test-Path $resultPath) {
        Write-Host ''
        Write-Host 'M2 result marker:' -ForegroundColor Cyan
        Get-Content $resultPath
    }

    if (Test-Path $logPath) {
        Write-Host ''
        Write-Host 'Recent M2 service log:' -ForegroundColor Cyan
        Get-Content $logPath | Select-Object -Last 50
    }

    Write-Host ''
    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 WATCHDOG M2 (SESSION-0 READ-ONLY)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Purpose: prove the exact HP 8C40 dependencies are readable from a real'
Write-Host 'LocalSystem Windows service in Session 0:'
Write-Host '  - exact HP-8C40-9D0R1LA-F18 fingerprint'
Write-Host '  - PawnIO/LpcACPIEC narrow EC ownership read (0x34/0x35)'
Write-Host '  - HP BIOS/WMI GetFanLevel read'
Write-Host ''
Write-Host 'NO SetFanLevel, FF/FF release, LegacyDefault, watchdog lease, fan policy,' -ForegroundColor Yellow
Write-Host 'or EC register-value write is issued by M2.' -ForegroundColor Yellow
Write-Host ''

foreach ($name in @('OmenMon', 'OmenMon-Reborn', 'VictusFanControl.App')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "Refusing M2 while process '$name' is running. Close it and retry."
    }
}

Write-Host 'Step 1: build + synthetic M2/BIOS contract regression...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m2-8c40-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project .\src\VictusFanControl -c Release --no-build -- --bios-contract-self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Step 2: interactive read-only 8C40 baseline...' -ForegroundColor Cyan
$baseline = Read-8C40Setpoint

if ($baseline.Cpu -ne 255 -or $baseline.Gpu -ne 255) {
    throw "M2 requires a clean firmware-owned FF/FF baseline; read $($baseline.Cpu)/$($baseline.Gpu). No write was issued."
}

Write-Host ''
Write-Host 'Step 3: install isolated LocalSystem M2 service...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-m2-8c40.ps1')

for ($cycle = 1; $cycle -le $RestartCycles; $cycle++) {
    Write-Host ''
    Write-Host "=== M2 service cycle $cycle/$RestartCycles ===" -ForegroundColor Cyan

    Remove-Item $resultPath -Force -ErrorAction SilentlyContinue

    try {
        Start-Service -Name $serviceName
    }
    catch {
        Write-Warning "Start-Service reported: $($_.Exception.Message)"
    }

    if (-not (Wait-ForFile -Path $resultPath -Seconds 20)) {
        Show-Diagnostics
        throw 'Timed out waiting for the M2 service result marker.'
    }

    $result = Get-Content $resultPath -Raw | ConvertFrom-Json

    Write-Host "Success          : $($result.Success)"
    Write-Host "RunId            : $($result.RunId)"
    Write-Host "PID              : $($result.ProcessId)"
    Write-Host "Session          : $($result.SessionId)"
    Write-Host "Account          : $($result.AccountName)"
    Write-Host "SID              : $($result.UserSid)"
    Write-Host "TargetProfileId  : $($result.TargetProfileId)"
    Write-Host "Target matched   : $($result.TargetMatched)"
    Write-Host "EC read          : attempted=$($result.EcReadAttempted) success=$($result.EcReadSucceeded)"
    Write-Host "EC setpoint      : $($result.CpuSetpoint)/$($result.GpuSetpoint)"
    Write-Host "WMI read         : attempted=$($result.WmiReadAttempted) success=$($result.WmiReadSucceeded)"
    Write-Host "GetFanLevel      : $($result.BiosCpuCurrentLevel)/$($result.BiosGpuCurrentLevel)"

    if (-not $result.Success) {
        Write-Host ''
        Write-Host "Failure: $($result.Failure)" -ForegroundColor Red
        Show-Diagnostics
        exit 121
    }

    if ([int]$result.SessionId -ne 0) {
        Show-Diagnostics
        throw "M2 did not execute in Session 0; observed SessionId=$($result.SessionId)."
    }

    if ($result.UserSid -cne 'S-1-5-18') {
        Show-Diagnostics
        throw "M2 did not execute as LocalSystem; observed SID=$($result.UserSid)."
    }

    if ($result.TargetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
        -not $result.TargetMatched) {
        Show-Diagnostics
        throw 'M2 exact HP 8C40 target identity was not proven.'
    }

    if (-not $result.EcReadSucceeded -or
        -not $result.WmiReadSucceeded) {
        Show-Diagnostics
        throw 'M2 did not prove both read-only hardware dependencies.'
    }

    if ([int]$result.CpuSetpoint -ne 255 -or
        [int]$result.GpuSetpoint -ne 255) {
        Show-Diagnostics
        throw "M2 service observed non-firmware EC ownership: $($result.CpuSetpoint)/$($result.GpuSetpoint). No write was issued."
    }

    if (Test-Path $journalPath) {
        Show-Diagnostics
        throw 'M2 unexpectedly created a watchdog lease journal.'
    }

    $service = Get-Service -Name $serviceName
    if ($service.Status -ne 'Running') {
        Show-Diagnostics
        throw "M2 result passed but the SCM service is not staying Running; state=$($service.Status)."
    }

    Stop-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus(
        'Stopped',
        [TimeSpan]::FromSeconds(10))

    Write-Host "Cycle ${cycle}: PASS (LocalSystem/Session 0 read-only)." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Step 4: final EC non-mutation verification...' -ForegroundColor Cyan
$final = Read-8C40Setpoint

if ($final.Cpu -ne 255 -or $final.Gpu -ne 255) {
    throw "M2 final non-mutation check did not read FF/FF: $($final.Raw)"
}

if (Test-Path $journalPath) {
    throw 'M2 left an unexpected watchdog lease journal after final verification.'
}

Write-Host ''
Write-Host 'Step 5: final service/log configuration...' -ForegroundColor Cyan
if (Test-Path $logPath) {
    Get-Content $logPath | Select-Object -Last 50
}

Write-Host ''
& sc.exe qc $serviceName | Out-Host

Write-Host ''
Write-Host 'PASS: HP 8C40 M2 proved LocalSystem/Session-0 read access without fan-control authority.' -ForegroundColor Green
Write-Host 'Verified: exact target + EC 0x34/0x35 read + GetFanLevel + final FF/FF + no lease journal.' -ForegroundColor Green
Write-Host "Service is installed but STOPPED: $serviceName"
Write-Host ''
Write-Host 'To remove M2 later:'
Write-Host '  .\scripts\uninstall-watchdog-m2-8c40.ps1'
