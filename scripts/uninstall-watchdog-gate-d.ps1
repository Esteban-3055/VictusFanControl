$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdog'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell.'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host 'Gate D watchdog service is not installed.'
    exit 0
}

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
}

& sc.exe delete $serviceName | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe delete failed with exit code $LASTEXITCODE."
}

Write-Host 'Gate D service removed. ProgramData journal/logs under VictusFanControl\WatchdogService were intentionally preserved.'
