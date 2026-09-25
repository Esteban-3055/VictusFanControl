param(
    [ValidateSet('LocalService', 'LocalSystem')]
    [string]$Account = 'LocalService',

    [ValidateRange(1, 5)]
    [int]$RestartCycles = 3
)

$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogGateA'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repoRoot 'src\VictusFanControl\bin\Release\net8.0-windows\VictusFanControl.dll'
$resultPath = Join-Path $env:ProgramData 'VictusFanControl\Watchdog\gate-a.result.json'
$logPath = Join-Path $env:ProgramData ("VictusFanControl\Watchdog\logs\watchdog-gate-a-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This hardware gate must be run from an elevated PowerShell.'
    }
}

function Show-Diagnostics {
    if (Test-Path $resultPath) {
        Write-Host ''
        Write-Host 'Gate A result marker:' -ForegroundColor Cyan
        Get-Content $resultPath
    }

    if (Test-Path $logPath) {
        Write-Host ''
        Write-Host 'Recent Gate A service log:' -ForegroundColor Cyan
        Get-Content $logPath | Select-Object -Last 40
    }

    Write-Host ''
    & sc.exe queryex $serviceName | Out-Host
}

Assert-Administrator
Set-Location $repoRoot

Write-Host 'VictusFanControl - WATCHDOG GATE A (READ-ONLY WINDOWS SERVICE)' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Purpose: prove Session 0 service access to:'
Write-Host '  - exact HP 88F8 fingerprint'
Write-Host '  - PawnIO + LpcACPIEC.bin read-only EC state'
Write-Host '  - HP BIOS/WMI GetFanLevel read-only path'
Write-Host ''
Write-Host 'NO SetFanLevel, FF/FF, LegacyDefault, EC register write, or fan-policy command is issued.' -ForegroundColor Yellow
Write-Host "Requested service account: $Account"
Write-Host ''

Write-Host 'Step 1: build entire solution with warnings as errors...' -ForegroundColor Cyan
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host 'Step 2: read-only interactive EC preflight...' -ForegroundColor Cyan
$preflight = (& dotnet $cli --probe-88f8-ec-state 2>&1 | Out-String)
Write-Host $preflight.TrimEnd()

$stateLine = ($preflight -split "[\r\n]+" |
    Where-Object { $_ -match '^level CPU=' } |
    Select-Object -Last 1)

if (-not $stateLine) {
    throw 'Could not parse the interactive read-only EC preflight.'
}

$match = [regex]::Match($stateLine, '^level CPU=(\d+) GPU=(\d+)')
if (-not $match.Success) {
    throw "Could not parse EC setpoints from: $stateLine"
}

if ([int]$match.Groups[1].Value -ne 255 -or
    [int]$match.Groups[2].Value -ne 255) {
    throw "Gate A requires a clean firmware-owned FF/FF baseline; read $($match.Groups[1].Value)/$($match.Groups[2].Value)."
}

Write-Host ''
Write-Host 'Step 3: publish/install the read-only service...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'install-watchdog-gate-a.ps1') -Account $Account

Write-Host ''
Write-Host "Step 4: start/stop/restart service for $RestartCycles cycle(s)..." -ForegroundColor Cyan

for ($cycle = 1; $cycle -le $RestartCycles; $cycle++) {
    Write-Host ''
    Write-Host "=== Gate A service cycle $cycle/$RestartCycles ===" -ForegroundColor Cyan

    Remove-Item $resultPath -Force -ErrorAction SilentlyContinue

    try {
        Start-Service -Name $serviceName
    }
    catch {
        Write-Warning "Start-Service reported: $($_.Exception.Message)"
    }

    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $resultPath) -and
           (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if (-not (Test-Path $resultPath)) {
        Show-Diagnostics
        throw 'Timed out waiting for the Gate A service result marker.'
    }

    $result = Get-Content $resultPath -Raw | ConvertFrom-Json

    Write-Host "Result        : $($result.Success)"
    Write-Host "RunId         : $($result.RunId)"
    Write-Host "PID           : $($result.ProcessId)"
    Write-Host "Session       : $($result.SessionId)"
    Write-Host "Account       : $($result.AccountName)"
    Write-Host "Target matched: $($result.TargetMatched)"
    Write-Host "EC setpoint   : $($result.CpuSetpoint)/$($result.GpuSetpoint)"
    Write-Host "EC RPM        : $($result.CpuRpm)/$($result.GpuRpm)"
    Write-Host "WMI levels    : $($result.BiosCpuCurrentLevel)/$($result.BiosGpuCurrentLevel)"

    if (-not $result.Success) {
        Write-Host ''
        Write-Host 'Failure:' -ForegroundColor Red
        Write-Host $result.Failure
        Show-Diagnostics

        if ($Account -eq 'LocalService') {
            Write-Host ''
            Write-Host 'If failure is specifically PawnIO/WMI access denied, the controlled comparison is:' -ForegroundColor Yellow
            Write-Host '  .\scripts\test-watchdog-gate-a.ps1 -Account LocalSystem'
        }

        exit 81
    }

    if ([int]$result.SessionId -ne 0) {
        Show-Diagnostics
        throw "Gate A did not execute in Session 0; observed SessionId=$($result.SessionId)."
    }

    $service = Get-Service -Name $serviceName
    if ($service.Status -ne 'Running') {
        Show-Diagnostics
        throw "Gate A probe passed but service is not staying Running; state=$($service.Status)."
    }

    Stop-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus(
        'Stopped',
        [TimeSpan]::FromSeconds(10))

    Write-Host "Cycle $cycle: PASS (read-only)." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Step 5: final service/log verification...' -ForegroundColor Cyan
Get-Content $logPath | Select-Object -Last 40
Write-Host ''
& sc.exe qc $serviceName | Out-Host

Write-Host ''
Write-Host 'PASS: Watchdog Gate A completed all read-only Session 0 cycles.' -ForegroundColor Green
Write-Host "Service is installed but STOPPED: $serviceName"
Write-Host 'No fan write was issued by this Gate A service.'
Write-Host ''
Write-Host 'To remove the Gate A service later:'
Write-Host '  .\scripts\uninstall-watchdog-gate-a.ps1'
