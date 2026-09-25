$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$gateEPath = Join-Path $PSScriptRoot 'test-watchdog-gate-e.ps1'
$mainFormPath = Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$clientPath = Join-Path $repoRoot 'src\VictusFanControl\Control\NamedPipeFanControlWatchdogLeaseClient.cs'
$protocolPath = Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlWatchdogLeaseProtocol.cs'

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

Assert-Contains -Text $gateE -Pattern 'WATCHDOG_IPC_LOSS' -Description 'Gate E harness requires classified watchdog IPC loss'
Assert-Contains -Text $mainForm -Pattern 'FanControlWatchdogTransportException\.Marker' -Description 'Gate E GUI classifies watchdog transport loss explicitly'
Assert-Contains -Text $mainForm -Pattern 'LOCAL-RESTORE-UNRELATED' -Description 'unrelated backend failures cannot be recorded as Gate E watchdog PASS'
Assert-Contains -Text $mainForm -Pattern '_fanCoordinator\.Authority\s*==\s*FanAuthority\.Custom' -Description 'GUI diagnostic EC probe is guarded while Custom authority is active'
Assert-Contains -Text $client -Pattern 'new FanControlWatchdogTransportException' -Description 'named-pipe transport failures use the stable watchdog-loss exception'
Assert-Contains -Text $protocol -Pattern 'public const string Marker = "WATCHDOG_IPC_LOSS"' -Description 'stable WATCHDOG_IPC_LOSS marker is defined in the shared control layer'

Write-Host ''
Write-Host 'Gate E contention/causality invariant self-test: PASS' -ForegroundColor Green
exit 0