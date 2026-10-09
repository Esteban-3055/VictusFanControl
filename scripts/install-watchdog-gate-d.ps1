$ErrorActionPreference = 'Stop'

$serviceName = 'VictusFanControlWatchdog'
$displayName = 'VictusFanControl Watchdog'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\VictusFanControl.Watchdog\VictusFanControl.Watchdog.csproj'
$sourceModule = Join-Path $repoRoot 'modules\LpcACPIEC.bin'

# Gate D uses an isolated ProgramData tree. Earlier Gate A/B validation files
# intentionally had different ACL histories; reusing that tree can make an
# elevated Administrator unable to read newly created service state.
$installRoot = Join-Path $env:ProgramData 'VictusFanControl\WatchdogService'
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

    # Stop any earlier watchdog validation/service instances before touching
    # the Gate D deployment. Gate D itself uses an isolated ProgramData tree.
    foreach ($name in @(
        'VictusFanControlWatchdogGateA',
        'VictusFanControlWatchdogGateB',
        $serviceName
    )) {
        Stop-ServiceIfRunning -Name $name
    }

    Remove-ServiceIfPresent -Name $serviceName

    # Harden the ROOT before creating/copying any service children.
    # Child files/directories will then inherit only the two intended ACEs.
    #
    # Do not recursively remove inheritance from existing child objects: doing
    # that can strip the very inherited SYSTEM/Administrators ACEs we need and
    # lock the installer out mid-pass. Root-level protection is sufficient for
    # a fresh service tree and remains stable across later updates.
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

    if (Test-Path $binDir) {
        Remove-Item -Recurse -Force $binDir
    }

    New-Item -ItemType Directory -Force -Path $binDir, $modulesDir, $logsDir, $stateDir | Out-Null

    Copy-Item -Path (Join-Path $publishTemp '*') -Destination $binDir -Recurse -Force
    Copy-Item -Path $sourceModule -Destination (Join-Path $modulesDir 'LpcACPIEC.bin') -Force

    # Ownership is intentionally not rewritten recursively. The protected DACL
    # is the access-control boundary here; local Administrators already have
    # explicit FullControl and can service/update the tree, while SYSTEM has
    # the same rights for runtime operation.
    #
    # Prove the exact elevated maintenance path still has read/write/delete
    # access before the service is created.
    $aclProbe = Join-Path $stateDir '.gate-d-acl-probe'
    'gate-d-acl-ok' | Set-Content -Path $aclProbe -Encoding Ascii
    $aclProbeReadback = (Get-Content -Path $aclProbe -Raw).Trim()
    Remove-Item $aclProbe -Force

    if ($aclProbeReadback -cne 'gate-d-acl-ok') {
        throw 'Gate D ProgramData ACL read/write probe failed after hardening.'
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
