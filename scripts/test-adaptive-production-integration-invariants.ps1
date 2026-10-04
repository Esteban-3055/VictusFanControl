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

# The prepared path must reach the real native boundary while remaining gated.
$main = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$worker = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\TelemetryWorker.cs') -Raw
$client = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\HpOmenBiosWmiClient.cs') -Raw
$backend = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs') -Raw
$gate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
Assert-Contains $main 'automaticHardware:' 'GUI must opt into the shared exact-target Automatic path.'
Assert-Contains $main 'EvaluateAutomaticSafety(snapshot, raw, observe: false)' 'GUI must preview the same Automatic admission without counting.'
Assert-Contains $worker 'await SnapshotProcessor(snapshot, cancellationToken)' 'Automatic processing must be awaited by the worker.'
Assert-Contains $controller 'new FanDispatchAdmissionScope(EnsureAutomaticDispatchAllowed)' 'Automatic must carry admission through asynchronous backend preparation.'
Assert-Contains $backend 'FanDispatchAdmissionScope.EnsureAllowed();' 'Production backend must recheck admission before its setter.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'Prepared integration must not promote Automatic.'
$guardIndex = $client.IndexOf('FanDispatchAdmissionScope.EnsureNativeRequestAllowed(request);', [StringComparison]::Ordinal)
$invokeIndex = $client.IndexOf('target.InvokeMethod(', [StringComparison]::Ordinal)
if ($guardIndex -lt 0 -or $invokeIndex -lt 0 -or $guardIndex -ge $invokeIndex) {
    throw 'The actual HP native invocation must be preceded by scoped Automatic admission.'
}

Write-Host 'HP 8C40 P11 adaptive production adapter invariant: PASS'
