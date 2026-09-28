$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogM4'
$displayName = 'VictusFanControl Watchdog M4 - HP 8C40 Lease Qualification'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj'
$sourceModule = Join-Path $repoRoot 'modules\LpcACPIEC.bin'

$installRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM4'
$binDir = Join-Path $installRoot 'bin'
$modulesDir = Join-Path $installRoot 'modules'
$logsDir = Join-Path $installRoot 'logs'
$stateDir = Join-Path $installRoot 'state'
$resultPath = Join-Path $stateDir 'm4-8c40.status.json'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell.'
    }
}

function Stop-ServiceIfRunning {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
}

function Remove-ServiceIfPresent {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) { return }

    Stop-ServiceIfRunning -Name $Name
    & sc.exe delete $Name | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed for $Name with exit code $LASTEXITCODE."
    }

    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for service '$Name' to be deleted."
}

Assert-Administrator

foreach ($legacyName in @(
    'VictusFanControlWatchdogGateA',
    'VictusFanControlWatchdogGateB',
    'VictusFanControlWatchdog'
)) {
    if (Get-Service -Name $legacyName -ErrorAction SilentlyContinue) {
        throw "M4 isolation refused because historical 88F8 service '$legacyName' is installed."
    }
}

foreach ($otherName in @(
    'VictusFanControlWatchdogM2',
    'VictusFanControlWatchdogM3'
)) {
    $other = Get-Service -Name $otherName -ErrorAction SilentlyContinue
    if ($other -and $other.Status -ne 'Stopped') {
        throw "M4 isolation refused while '$otherName' is running."
    }
}

if (-not (Test-Path $sourceModule)) {
    throw "Required signed module not found: $sourceModule"
}

$publishTemp = Join-Path ([IO.Path]::GetTempPath()) ('VFC-Watchdog-M4-8C40-' + [Guid]::NewGuid().ToString('N'))

try {
    Write-Host 'Publishing HP 8C40 M4 target-bound lease service...' -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $publishTemp | Out-Null

    dotnet publish $project -c Release -o $publishTemp
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Remove-ServiceIfPresent -Name $serviceName

    if (Test-Path $installRoot) {
        Remove-Item -Recurse -Force $installRoot
    }

    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null

    & icacls.exe $installRoot /grant:r '*S-1-5-18:(OI)(CI)F' /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls SYSTEM grant failed: $LASTEXITCODE" }

    & icacls.exe $installRoot /grant:r '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls Administrators grant failed: $LASTEXITCODE" }

    & icacls.exe $installRoot /inheritance:r /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls inheritance hardening failed: $LASTEXITCODE" }

    New-Item -ItemType Directory -Force -Path $binDir, $modulesDir, $logsDir, $stateDir | Out-Null

    Copy-Item -Path (Join-Path $publishTemp '*') -Destination $binDir -Recurse -Force
    Copy-Item -Path $sourceModule -Destination (Join-Path $modulesDir 'LpcACPIEC.bin') -Force

    Remove-Item (Join-Path $stateDir 'lease.json') -Force -ErrorAction SilentlyContinue
    Remove-Item $resultPath -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $binDir 'VictusFanControl.Watchdog.exe'
    $q = [char]34
    $binPath = "$q$exe$q --service-name $serviceName --modules-dir $q$modulesDir$q --result-path $q$resultPath$q --log-dir $q$logsDir$q --m4-8c40-lease-service"

    & sc.exe create $serviceName binPath= $binPath start= demand obj= LocalSystem displayname= $displayName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed: $LASTEXITCODE" }

    & sc.exe description $serviceName 'HP 8C40 M4 qualification-only target-bound durable lease service. Service can read EC ownership and perform the M3-qualified firmware restore only; it cannot issue ordinary fan targets.' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed: $LASTEXITCODE" }

    & sc.exe sidtype $serviceName unrestricted | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe sidtype failed: $LASTEXITCODE" }

    Write-Host ''
    Write-Host "Installed service: $serviceName" -ForegroundColor Green
    Write-Host 'Account          : LocalSystem'
    Write-Host 'Startup          : Demand/manual'
    Write-Host "Install root     : $installRoot"
    Write-Host "Status path      : $resultPath"
    Write-Host 'Pipe             : VictusFanControl.Watchdog.M4.8C40.v2'
    Write-Host 'Service-side ordinary SetFanLevel authority: NONE'
}
finally {
    Remove-Item -Recurse -Force $publishTemp -ErrorAction SilentlyContinue
}
