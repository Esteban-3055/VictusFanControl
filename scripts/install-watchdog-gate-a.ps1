param(
    [ValidateSet('LocalService', 'LocalSystem')]
    [string]$Account = 'LocalService'
)

$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdogGateA'
$displayName = 'VictusFanControl Watchdog Gate A'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj'
$sourceModule = Join-Path $repoRoot 'modules\LpcACPIEC.bin'

$installRoot = Join-Path $env:ProgramData 'VictusFanControl\Watchdog'
$binDir = Join-Path $installRoot 'bin'
$modulesDir = Join-Path $installRoot 'modules'
$logsDir = Join-Path $installRoot 'logs'
$resultPath = Join-Path $installRoot 'gate-a.result.json'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell.'
    }
}

function Remove-ExistingService {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $service) {
        return
    }

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
    }

    & sc.exe delete $serviceName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed with exit code $LASTEXITCODE."
    }

    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw 'Timed out waiting for the previous service registration to disappear.'
}

Assert-Administrator

if (-not (Test-Path $sourceModule)) {
    throw "Required signed module not found: $sourceModule"
}

Write-Host 'Publishing read-only Gate A watchdog service...' -ForegroundColor Cyan

$publishTemp = Join-Path (
    [IO.Path]::GetTempPath()) (
    'VFC-Watchdog-GateA-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $publishTemp | Out-Null

    dotnet publish $project -c Release -o $publishTemp
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Remove-ExistingService

    if (Test-Path $installRoot) {
        Remove-Item -Recurse -Force $installRoot
    }

    New-Item -ItemType Directory -Force -Path $binDir, $modulesDir, $logsDir | Out-Null

    Copy-Item -Path (Join-Path $publishTemp '*') -Destination $binDir -Recurse -Force
    Copy-Item -Path $sourceModule -Destination (Join-Path $modulesDir 'LpcACPIEC.bin') -Force

    if ($Account -eq 'LocalService') {
        # S-1-5-19 is LocalService. Using the SID avoids localized account-name issues.
        & icacls.exe $installRoot /grant:r '*S-1-5-19:(OI)(CI)M' /T /C | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "icacls failed with exit code $LASTEXITCODE."
        }

        $serviceAccount = 'NT AUTHORITY\LocalService'
    }
    else {
        $serviceAccount = 'LocalSystem'
    }

    $exe = Join-Path $binDir 'VictusFanControl.Watchdog.exe'
    $q = [char]34
    $binPath = "$q$exe$q --modules-dir $q$modulesDir$q --result-path $q$resultPath$q --log-dir $q$logsDir$q"

    & sc.exe create $serviceName binPath= $binPath start= demand obj= $serviceAccount displayname= $displayName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed with exit code $LASTEXITCODE."
    }

    & sc.exe description $serviceName 'Read-only VictusFanControl watchdog Gate A: Session 0 PawnIO/EC + HP WMI validation. No fan writes.' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe description failed with exit code $LASTEXITCODE."
    }

    & sc.exe sidtype $serviceName unrestricted | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe sidtype failed with exit code $LASTEXITCODE."
    }

    Write-Host ''
    Write-Host "Installed service: $serviceName" -ForegroundColor Green
    Write-Host "Account          : $serviceAccount"
    Write-Host "Install root     : $installRoot"
    Write-Host "Result marker    : $resultPath"
    Write-Host 'The installer does not start the service.'
}
finally {
    Remove-Item -Recurse -Force $publishTemp -ErrorAction SilentlyContinue
}
