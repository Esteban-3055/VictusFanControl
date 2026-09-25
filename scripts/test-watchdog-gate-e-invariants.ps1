$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$gateEPath = Join-Path $PSScriptRoot 'test-watchdog-gate-e.ps1'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$clientPath = Join-Path $repoRoot 'src\VictusFanControl\Control\NamedPipeFanControlWatchdogLeaseClient.cs'
$protocolPath = Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlWatchdogLeaseProtocol.cs'
$backendPath = Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp88F8FanControlBackend.cs'
$coordinatorPath = Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlCoordinator.cs'

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "Gate E invariant missing: $Description"
    }

    Write-Host "PASS  $Description"
}

$gateE = Get-Content $gateEPath -Raw
$mainForm = Get-Content $mainFormPath -Raw
$client = Get-Content $clientPath -Raw
$protocol = Get-Content $protocolPath -Raw
$backend = Get-Content $backendPath -Raw
$coordinator = Get-Content $coordinatorPath -Raw

$readyBoundary = $gateE.IndexOf('$readyReached = $true', [StringComparison]::Ordinal)
$killBoundary = $gateE.IndexOf('Stop-Process -Id $servicePidBefore -Force', [StringComparison]::Ordinal)

if ($readyBoundary -lt 0 -or $killBoundary -le $readyBoundary) {
    throw 'Could not resolve the Gate E READY -> watchdog-kill boundary.'
}

$preKillSegment = $gateE.Substring($readyBoundary, $killBoundary - $readyBoundary)

if ($preKillSegment -match 'Read-EcState|--probe-88f8-ec-state|Hp88F8EcControlStateProbe') {
    throw 'Gate E invariant violated: an out-of-band EC probe exists between READY and watchdog kill.'
}

Write-Host 'PASS  no out-of-band EC probe exists between Gate E READY and watchdog kill'

if ($preKillSegment -match 'Start-Sleep') {
    throw 'Gate E invariant violated: an artificial dwell exists between READY and watchdog kill.'
}

Write-Host 'PASS  no artificial dwell exists between Gate E READY and watchdog kill'

if ($preKillSegment -match 'if\s*\(\s*Get-ServiceProcessId\s+-[a-zA-Z]+') {
    throw 'Gate E invariant violated: Get-ServiceProcessId is used as a bare command inside a comparison. Capture its scalar result before comparing so PowerShell cannot parse the operator as a command argument.'
}

Assert-Contains -Text $preKillSegment -Pattern '\$servicePidAtKillBoundary\s*=\s*Get-ServiceProcessId' -Description 'watchdog PID is captured explicitly at the forced-kill boundary'
Assert-Contains -Text $preKillSegment -Pattern '\$servicePidAtKillBoundary\s+-ne\s+\$servicePidBefore' -Description 'captured watchdog PID is compared as a scalar before forced kill'

$getStatusStart = $backend.IndexOf('public async ValueTask<FanBackendStatus> GetStatusAsync', [StringComparison]::Ordinal)
$getStatusEnd = $backend.IndexOf('public async ValueTask EnterCustomModeAsync', [StringComparison]::Ordinal)

if ($getStatusStart -lt 0 -or $getStatusEnd -le $getStatusStart) {
    throw 'Could not resolve Hp88F8FanControlBackend.GetStatusAsync for Gate E invariant checks.'
}

$getStatusSegment = $backend.Substring($getStatusStart, $getStatusEnd - $getStatusStart)
$probeIndex = $getStatusSegment.IndexOf('ProbeAsync(', [StringComparison]::Ordinal)
$ecIndex = $getStatusSegment.IndexOf('_hardware!.ReadEcState()', [StringComparison]::Ordinal)

if ($probeIndex -lt 0 -or $ecIndex -lt 0 -or $probeIndex -ge $ecIndex) {
    throw 'Gate E invariant violated: watchdog non-renewing ProbeAsync must occur before the EC health read.'
}

Write-Host 'PASS  watchdog transport/lease probe occurs before EC health validation'

$enforceStart = $coordinator.IndexOf('public async ValueTask<bool> EnforceSafetyAsync', [StringComparison]::Ordinal)
$enforceEnd = $coordinator.IndexOf('public async ValueTask RestoreFirmwareAsync', [StringComparison]::Ordinal)

if ($enforceStart -lt 0 -or $enforceEnd -le $enforceStart) {
    throw 'Could not resolve FanControlCoordinator.EnforceSafetyAsync for Gate E invariant checks.'
}

$enforceSegment = $coordinator.Substring($enforceStart, $enforceEnd - $enforceStart)
$dependencyIndex = $enforceSegment.IndexOf('ProbeControlDependencyAsync(', [StringComparison]::Ordinal)
$safetyHandoffIndex = $enforceSegment.IndexOf('if (!SafetyAllowsCustomLocked(safety))', [StringComparison]::Ordinal)

if ($dependencyIndex -lt 0 -or $safetyHandoffIndex -lt 0 -or $dependencyIndex -ge $safetyHandoffIndex) {
    throw 'Gate E invariant violated: watchdog/control dependency must be probed before telemetry-derived safety handoff.'
}

Write-Host 'PASS  watchdog dependency is probed before telemetry-derived safety handoff'

Assert-Contains -Text $gateE -Pattern 'WATCHDOG_IPC_LOSS' -Description 'Gate E harness requires classified watchdog IPC loss'
Assert-Contains -Text $mainForm -Pattern 'FanControlWatchdogTransportException\.Marker' -Description 'Gate E GUI classifies watchdog transport loss explicitly'
Assert-Contains -Text $mainForm -Pattern 'LOCAL-RESTORE-UNRELATED' -Description 'unrelated backend failures cannot be recorded as Gate E watchdog PASS'
Assert-Contains -Text $mainForm -Pattern '_fanCoordinator\.Authority\s*!=\s*FanAuthority\.Firmware' -Description 'GUI diagnostic EC probe is permitted only under established Firmware authority'
Assert-Contains -Text $client -Pattern 'new FanControlWatchdogTransportException' -Description 'named-pipe transport failures use the stable watchdog-loss exception'
Assert-Contains -Text $protocol -Pattern 'public const string Marker = "WATCHDOG_IPC_LOSS"' -Description 'stable WATCHDOG_IPC_LOSS marker is defined in the shared control layer'

Write-Host ''
Write-Host 'Gate E contention/causality invariant self-test: PASS' -ForegroundColor Green
exit 0