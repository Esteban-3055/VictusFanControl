$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogGateB'
$displayName = 'VictusFanControl Watchdog Gate B'
$gateAServiceName = 'VictusFanControlWatchdogGateA'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj'
$sourceModule = Join-Path $repoRoot 'modules\LpcACPIEC.bin'

$installRoot = Join-Path $env:ProgramData 'VictusFanControl\Watchdog'
$binDir = Join-Path $installRoot 'bin'
$modulesDir = Join-Path $installRoot 'modules'
$logsDir = Join-Path $installRoot 'logs'
$stateDir = Join-Path $installRoot 'state'
$resultPath = Join-Path $stateDir 'gate-b.result.json'

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
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
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

    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for service '$Name' to be deleted."
}

Assert-Administrator

if (-not (Test-Path $sourceModule)) {
    throw "Required signed module not found: $sourceModule"
}

Write-Host 'Publishing Gate B one-shot restore service...' -ForegroundColor Cyan

$publishTemp = Join-Path (
    [IO.Path]::GetTempPath()) (
    'VFC-Watchdog-GateB-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $publishTemp | Out-Null

    dotnet publish $project -c Release -o $publishTemp
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Stop-ServiceIfRunning -Name $gateAServiceName
    Remove-ServiceIfPresent -Name $serviceName

    if (Test-Path $binDir) {
        Remove-Item -Recurse -Force $binDir
    }

    New-Item -ItemType Directory -Force -Path $binDir, $modulesDir, $logsDir, $stateDir | Out-Null
    Copy-Item -Path (Join-Path $publishTemp '*') -Destination $binDir -Recurse -Force
    Copy-Item -Path $sourceModule -Destination (Join-Path $modulesDir 'LpcACPIEC.bin') -Force

    $exe = Join-Path $binDir 'VictusFanControl.Watchdog.exe'
    $q = [char]34
    $binPath = "$q$exe$q --service-name $serviceName --modules-dir $q$modulesDir$q --result-path $q$resultPath$q --log-dir $q$logsDir$q --gate-b-restore --gate-b-token 88F8-GATEB-RESTORE"

    & sc.exe create $serviceName binPath= $binPath start= demand obj= LocalSystem displayname= $displayName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed with exit code $LASTEXITCODE."
    }

    & sc.exe description $serviceName 'TEST-ONLY one-shot watchdog Gate B: only FF,FF -> LegacyDefault restore from an explicitly armed 30/30 state.' | Out-Host
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
    Write-Host "Result marker    : $resultPath"
    Write-Host 'Mode             : one-shot Gate B restore only'
    Write-Host 'The installer does not start the service.'
}
finally {
    Remove-Item -Recurse -Force $publishTemp -ErrorAction SilentlyContinue
}
