param(
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogGateA'
$installRoot = Join-Path $env:ProgramData 'VictusFanControl\Watchdog'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell.'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
    }

    & sc.exe delete $serviceName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed with exit code $LASTEXITCODE."
    }
}

if ($RemoveFiles -and (Test-Path $installRoot)) {
    Remove-Item -Recurse -Force $installRoot
    Write-Host "Removed $installRoot"
}

Write-Host 'Gate A watchdog service removed.' -ForegroundColor Green
