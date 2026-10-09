$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogM2'
$displayName = 'VictusFanControl Watchdog M2 - HP 8C40 Read Only'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj'
$sourceModule = Join-Path $repoRoot 'modules\LpcACPIEC.bin'

$installRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogM2'
$binDir = Join-Path $installRoot 'bin'
$modulesDir = Join-Path $installRoot 'modules'
$logsDir = Join-Path $installRoot 'logs'
$stateDir = Join-Path $installRoot 'state'
$resultPath = Join-Path $stateDir 'm2-8c40.result.json'

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
    if (-not $service) {
        return
    }

    Stop-ServiceIfRunning -Name $Name

    & sc.exe delete $Name | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed for $Name with exit code $LASTEXITCODE."
    }

    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for service '$Name' to be deleted."
}

Assert-Administrator

$legacyServiceNames = @(
    'VictusFanControlWatchdogGateA',
    'VictusFanControlWatchdogGateB',
    'VictusFanControlWatchdog'
)

$installedLegacyServices = @(
    $legacyServiceNames |
        ForEach-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue } |
        Where-Object { $_ }
)

if ($installedLegacyServices.Count -gt 0) {
    $legacySummary =
        ($installedLegacyServices |
            ForEach-Object { "$($_.Name)[$($_.Status)]" }) -join ', '

    throw (
        "HP 8C40 M-series isolation refused because historical 88F8 watchdog " +
        "services are still installed: $legacySummary. Run " +
        ".\scripts\cleanup-watchdog-88f8-services.ps1 from an elevated " +
        "PowerShell, then retry. Historical ProgramData evidence is preserved."
    )
}

if (-not (Test-Path $sourceModule)) {
    throw "Required signed module not found: $sourceModule"
}

Write-Host 'Publishing HP 8C40 M2 read-only LocalSystem service...' -ForegroundColor Cyan

$publishTemp = Join-Path (
    [IO.Path]::GetTempPath()) (
    'VFC-Watchdog-M2-8C40-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $publishTemp | Out-Null

    dotnet publish $project -c Release -o $publishTemp
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    # M-series isolation was checked before publishing. Only the dedicated M2
    # service is touched here; legacy 88F8 services must be explicitly removed
    # rather than silently stopped and left eligible for a later reboot start.
    Stop-ServiceIfRunning -Name $serviceName
    Remove-ServiceIfPresent -Name $serviceName

    if (Test-Path $installRoot) {
        Remove-Item -Recurse -Force $installRoot
    }

    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null

    & icacls.exe $installRoot /grant:r '*S-1-5-18:(OI)(CI)F' /Q | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "icacls SYSTEM root grant failed with exit code $LASTEXITCODE."
    }

    & icacls.exe $installRoot /grant:r '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "icacls Administrators root grant failed with exit code $LASTEXITCODE."
    }

    & icacls.exe $installRoot /inheritance:r /Q | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "icacls root inheritance hardening failed with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Force -Path $binDir, $modulesDir, $logsDir, $stateDir | Out-Null

    Copy-Item -Path (Join-Path $publishTemp '*') -Destination $binDir -Recurse -Force
    Copy-Item -Path $sourceModule -Destination (Join-Path $modulesDir 'LpcACPIEC.bin') -Force

    $aclProbe = Join-Path $stateDir '.m2-acl-probe'
    'm2-acl-ok' | Set-Content -Path $aclProbe -Encoding Ascii
    $aclReadback = (Get-Content -Path $aclProbe -Raw).Trim()
    Remove-Item $aclProbe -Force

    if ($aclReadback -cne 'm2-acl-ok') {
        throw 'M2 ProgramData ACL read/write probe failed after hardening.'
    }

    Remove-Item $resultPath -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $binDir 'VictusFanControl.Watchdog.exe'
    $q = [char]34
    $binPath = "$q$exe$q --service-name $serviceName --modules-dir $q$modulesDir$q --result-path $q$resultPath$q --log-dir $q$logsDir$q --m2-8c40-read-only"

    & sc.exe create $serviceName binPath= $binPath start= demand obj= LocalSystem displayname= $displayName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed with exit code $LASTEXITCODE."
    }

    & sc.exe description $serviceName 'HP 8C40 M2 read-only Session-0 dependency qualification. Reads exact target identity, EC 0x34/0x35 and HP GetFanLevel only. No fan writes, restore or watchdog lease.' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe description failed with exit code $LASTEXITCODE."
    }

    & sc.exe sidtype $serviceName unrestricted | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe sidtype failed with exit code $LASTEXITCODE."
    }

    Write-Host ''
    Write-Host "Installed service: $serviceName" -ForegroundColor Green
    Write-Host 'Account          : LocalSystem'
    Write-Host 'Startup          : Demand/manual'
    Write-Host "Install root     : $installRoot"
    Write-Host "Result marker    : $resultPath"
    Write-Host 'The installer leaves the service STOPPED.'
    Write-Host 'M2 exposes no SetFanLevel, restore, lease or EC-register write operation.'
}
finally {
    Remove-Item -Recurse -Force $publishTemp -ErrorAction SilentlyContinue
}
