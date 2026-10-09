param(
    [switch]$AuditOnly
)

$ErrorActionPreference = 'Stop'

$legacyServices = @(
    'VictusFanControlWatchdogGateA',
    'VictusFanControlWatchdogGateB',
    'VictusFanControlWatchdog'
)

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell.'
    }
}

function Wait-ServiceDeleted {
    param([string]$Name)

    for ($i = 0; $i -lt 80; $i++) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for legacy service '$Name' to be deleted."
}

Assert-Administrator

Write-Host 'VictusFanControl - HP 8C40 legacy 88F8 service isolation' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Historical ProgramData logs/journals are NOT deleted by this script.'
Write-Host ''

$found = @()

foreach ($name in $legacyServices) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
    if ($service) {
        $found += $service

        Write-Host (
            "{0}: State={1}; StartMode={2}; StartName={3}; Path={4}" -f
            $service.Name,
            $service.State,
            $service.StartMode,
            $service.StartName,
            $service.PathName)
    }
}

if ($found.Count -eq 0) {
    Write-Host 'PASS: no historical 88F8 watchdog services are installed.' -ForegroundColor Green
    exit 0
}

if ($AuditOnly) {
    Write-Warning 'Audit only: historical 88F8 services are installed. No changes were made.'
    exit 2
}

Write-Host ''
Write-Host 'Removing historical 88F8 service registrations...' -ForegroundColor Yellow

foreach ($serviceInfo in $found) {
    $name = $serviceInfo.Name
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue

    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }

    & sc.exe config $name start= disabled | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe config failed for '$name' with exit code $LASTEXITCODE."
    }

    & sc.exe delete $name | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed for '$name' with exit code $LASTEXITCODE."
    }

    Wait-ServiceDeleted -Name $name
    Write-Host "Removed service registration: $name" -ForegroundColor Green
}

Write-Host ''
Write-Host 'PASS: historical 88F8 watchdog services are no longer registered.' -ForegroundColor Green
Write-Host 'Preserved evidence roots (if present):'
Write-Host '  %ProgramData%\VictusFanControl\Watchdog'
Write-Host '  %ProgramData%\VictusFanControl\WatchdogService'
