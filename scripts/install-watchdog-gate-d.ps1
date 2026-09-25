$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdog'
$displayName = 'VictusFanControl Watchdog'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj'
$sourceModule = Join-Path $repoRoot 'modules\LpcACPIEC.bin'

$installRoot = Join-Path $env:ProgramData 'VictusFanControl\Watchdog'
$binDir = Join-Path $installRoot 'bin'
$modulesDir = Join-Path $installRoot 'modules'
$logsDir = Join-Path $installRoot 'logs'
$stateDir = Join-Path $installRoot 'state'
$statusPath = Join-Path $stateDir 'gate-d.status.json'

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

if (-not (Test-Path $sourceModule)) {
    throw "Required signed module not found: $sourceModule"
}

Write-Host 'Publishing persistent Gate D watchdog service...' -ForegroundColor Cyan

$publishTemp = Join-Path (
    [IO.Path]::GetTempPath()) (
    'VFC-Watchdog-GateD-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $publishTemp | Out-Null

    dotnet publish $project -c Release -o $publishTemp
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    # Gate A/B/D currently share the watchdog publish tree. Never replace it
    # while an older service executable is still running.
    foreach ($name in @(
        'VictusFanControlWatchdogGateA',
        'VictusFanControlWatchdogGateB',
        $serviceName
    )) {
        Stop-ServiceIfRunning -Name $name
    }

    Remove-ServiceIfPresent -Name $serviceName

    if (Test-Path $binDir) {
        Remove-Item -Recurse -Force $binDir
    }

    New-Item -ItemType Directory -Force -Path $binDir, $modulesDir, $logsDir, $stateDir | Out-Null

    Copy-Item -Path (Join-Path $publishTemp '*') -Destination $binDir -Recurse -Force
    Copy-Item -Path $sourceModule -Destination (Join-Path $modulesDir 'LpcACPIEC.bin') -Force

    # The LocalSystem watchdog journal is a privileged ownership record.
    # Remove inherited ProgramData permissions and allow only SYSTEM plus local
    # Administrators to read/modify the service tree.
    & icacls.exe $installRoot /inheritance:r /T /C | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "icacls inheritance hardening failed with exit code $LASTEXITCODE."
    }

    & icacls.exe $installRoot /grant:r '*S-1-5-18:(OI)(CI)F' /T /C | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "icacls SYSTEM grant failed with exit code $LASTEXITCODE."
    }

    & icacls.exe $installRoot /grant '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "icacls Administrators grant failed with exit code $LASTEXITCODE."
    }

    & icacls.exe $installRoot /setowner '*S-1-5-18' /T /C | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "icacls owner hardening failed with exit code $LASTEXITCODE."
    }

    # Do NOT delete lease.json. Durable armed state must survive service update
    # or process replacement. Only the service lease machine may clear it.
    Remove-Item $statusPath -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $binDir 'VictusFanControl.Watchdog.exe'
    $q = [char]34
    $binPath = "$q$exe$q --service-name $serviceName --modules-dir $q$modulesDir$q --result-path $q$statusPath$q --log-dir $q$logsDir$q --gate-d-service"

    & sc.exe create $serviceName binPath= $binPath start= auto obj= LocalSystem displayname= $displayName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed with exit code $LASTEXITCODE."
    }

    & sc.exe description $serviceName 'Independent VictusFanControl crash watchdog. No ordinary fan-level write API; restore-only hardware authority.' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe description failed with exit code $LASTEXITCODE."
    }

    & sc.exe sidtype $serviceName unrestricted | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe sidtype failed with exit code $LASTEXITCODE."
    }

    & sc.exe failure $serviceName reset= 86400 actions= restart/1000/restart/5000/restart/10000 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failure failed with exit code $LASTEXITCODE."
    }

    & sc.exe failureflag $serviceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failureflag failed with exit code $LASTEXITCODE."
    }

    Write-Host ''
    Write-Host "Installed service: $serviceName" -ForegroundColor Green
    Write-Host 'Account          : LocalSystem'
    Write-Host 'Startup          : Automatic'
    Write-Host 'SCM recovery     : restart 1s / 5s / 10s'
    Write-Host "Status marker    : $statusPath"
    Write-Host "Durable journal  : $(Join-Path $stateDir 'lease.json')"
    Write-Host 'The installer leaves the service STOPPED; the test/startup owner starts it explicitly.'
}
finally {
    Remove-Item -Recurse -Force $publishTemp -ErrorAction SilentlyContinue
}
