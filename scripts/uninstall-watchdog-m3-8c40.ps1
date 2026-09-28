$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogM3'
$installRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM3'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'This script must be run from an elevated PowerShell.' }

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
    }
    & sc.exe delete $serviceName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }
}

if (Test-Path $installRoot) {
    Remove-Item -Recurse -Force $installRoot
    Write-Host "Removed $installRoot"
}

Write-Host 'HP 8C40 M3 restore-only watchdog service removed.' -ForegroundColor Green
