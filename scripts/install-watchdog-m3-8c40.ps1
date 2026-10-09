$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogM3'
$displayName = 'VictusFanControl Watchdog M3 - HP 8C40 Restore Only'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj'
$sourceModule = Join-Path $repoRoot 'modules\LpcACPIEC.bin'

$installRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM3'
$binDir = Join-Path $installRoot 'bin'
$modulesDir = Join-Path $installRoot 'modules'
$logsDir = Join-Path $installRoot 'logs'
$stateDir = Join-Path $installRoot 'state'
$resultPath = Join-Path $stateDir 'm3-8c40.result.json'
$handoffPath = Join-Path $stateDir 'm3-8c40.handoff.json'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'This script must be run from an elevated PowerShell.' }
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
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed for $Name with exit code $LASTEXITCODE." }
    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for service '$Name' to be deleted."
}

Assert-Administrator

foreach ($legacyName in @('VictusFanControlWatchdogGateA','VictusFanControlWatchdogGateB','VictusFanControlWatchdog')) {
    if (Get-Service -Name $legacyName -ErrorAction SilentlyContinue) {
        throw ("M3 isolation refused because historical 88F8 service '$legacyName' is installed. Run .\scripts\cleanup-watchdog-88f8-services.ps1 first.")
    }
}

$m2 = Get-Service -Name 'VictusFanControlWatchdogM2' -ErrorAction SilentlyContinue
if ($m2 -and $m2.Status -ne 'Stopped') { throw 'M3 isolation refused while VictusFanControlWatchdogM2 is running.' }

if (-not (Test-Path $sourceModule)) { throw "Required signed module not found: $sourceModule" }

Write-Host 'Publishing HP 8C40 M3 restore-only LocalSystem service...' -ForegroundColor Cyan

$publishTemp = Join-Path ([IO.Path]::GetTempPath()) ('VFC-Watchdog-M3-8C40-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $publishTemp | Out-Null
    dotnet publish $project -c Release -o $publishTemp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    Remove-ServiceIfPresent -Name $serviceName
    if (Test-Path $installRoot) { Remove-Item -Recurse -Force $installRoot }
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null

    & icacls.exe $installRoot /grant:r '*S-1-5-18:(OI)(CI)F' /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls SYSTEM root grant failed with exit code $LASTEXITCODE." }
    & icacls.exe $installRoot /grant:r '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls Administrators root grant failed with exit code $LASTEXITCODE." }
    & icacls.exe $installRoot /inheritance:r /Q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "icacls root inheritance hardening failed with exit code $LASTEXITCODE." }

    New-Item -ItemType Directory -Force -Path $binDir, $modulesDir, $logsDir, $stateDir | Out-Null
    Copy-Item -Path (Join-Path $publishTemp '*') -Destination $binDir -Recurse -Force
    Copy-Item -Path $sourceModule -Destination (Join-Path $modulesDir 'LpcACPIEC.bin') -Force

    Remove-Item $resultPath -Force -ErrorAction SilentlyContinue
    Remove-Item $handoffPath -Force -ErrorAction SilentlyContinue
    Get-ChildItem $stateDir -Filter 'm3-8c40.handoff.json.claimed.*.json' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $binDir 'VictusFanControl.Watchdog.exe'
    $q = [char]34
    $binPath = "$q$exe$q --service-name $serviceName --modules-dir $q$modulesDir$q --result-path $q$resultPath$q --log-dir $q$logsDir$q --m3-handoff-path $q$handoffPath$q --m3-8c40-restore-only"

    & sc.exe create $serviceName binPath= $binPath start= demand obj= LocalSystem displayname= $displayName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
    & sc.exe description $serviceName 'HP 8C40 M3 one-shot restore-only qualification. Accepts only a fresh live VFC 30/30 handoff, then issues FF/FF release -> LegacyDefault and verifies EC FF/FF. No ordinary fan-level writes or watchdog lease.' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE." }
    & sc.exe sidtype $serviceName unrestricted | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe sidtype failed with exit code $LASTEXITCODE." }

    Write-Host ''
    Write-Host "Installed service: $serviceName" -ForegroundColor Green
    Write-Host 'Account          : LocalSystem'
    Write-Host 'Startup          : Demand/manual'
    Write-Host "Install root     : $installRoot"
    Write-Host "Handoff path     : $handoffPath"
    Write-Host "Result path      : $resultPath"
    Write-Host 'The installer leaves the service STOPPED.'
    Write-Host 'M3 can only restore a fresh VFC-owned 30/30 handoff to firmware FF/FF.'
} finally {
    Remove-Item -Recurse -Force $publishTemp -ErrorAction SilentlyContinue
}
