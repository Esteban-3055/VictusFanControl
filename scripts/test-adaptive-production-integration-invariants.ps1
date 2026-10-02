$ErrorActionPreference = 'Stop'

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message }
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message }
}

$root = Split-Path -Parent $PSScriptRoot
$controller = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionController.cs') -Raw
$selfTest = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionControllerSelfTest.cs') -Raw
$program = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Program.cs') -Raw
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json

foreach ($needle in @(
    'FanControlCoordinator',
    'AdaptiveFanPolicyEngine',
    'AdaptiveFanControlIntentPlanner',
    'manualExecutionAuthorized',
    'automaticExecutionAuthorized',
    'unchanged manual target was not retransmitted',
    'new FanCommand(',
    'RestoreFirmwareAsync',
    'ManualFreshSafetyMaximumAttempts = 4',
    'refreshSafetyProvider',
    'post-admission SafetyGate refresh',
    'bounded attempt(s)'
)) {
    Assert-Contains $controller $needle ("P11 production adapter invariant missing: {0}" -f $needle)
}

foreach ($forbidden in @(
    'Hp8C40FanControlBackend',
    'HpOmenBiosWmiClient',
    'SetFanLevel(',
    'PawnIo',
    'AcpiEcReader',
    'NamedPipeFanControlWatchdogLeaseClient'
)) {
    Assert-NotContains $controller $forbidden ("P11 adapter must not bypass coordinator into hardware: {0}" -f $forbidden)
}

foreach ($needle in @(
    'TestClosedGatesNeverTouchBackendAsync',
    'TestManualEqualOnlyAndNoRetransmitAsync',
    'TestAutomaticNoRetransmitAndSafetyReleaseAsync',
    'TestManualRangeGuardAsync',
    'TestManualFreshSafetyRefreshAndRetryAsync',
    'TestManualFreshSafetyExhaustionRestoresAsync'
)) {
    Assert-Contains $selfTest $needle ("P11 self-test coverage missing: {0}" -f $needle)
}

Assert-Contains $program 'AdaptiveFanProductionControllerSelfTest.RunAsync' 'P11 self-test is not wired into the CLI route.'
if (-not [bool]$profile.control.adaptivePolicyPreparation.productionAdapterImplemented) { throw 'Profile must record the P11 adapter implementation.' }
if ([bool]$profile.control.adaptivePolicyPreparation.productionAdapterHardwareAuthorized) { throw 'P11 must not authorize adaptive hardware execution.' }
if ([bool]$profile.control.enabledByDefault) { throw 'P11 must not enable control by default.' }
if ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) { throw 'P11 must not enable automatic policy.' }

Write-Host 'HP 8C40 P11 adaptive production adapter invariant: PASS'
