$ErrorActionPreference = 'Stop'

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message }
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message }
}

$root = Split-Path -Parent $PSScriptRoot
$main = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$surface = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P13FanControlSurface.cs') -Raw
$settings = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P13UiSettingsStore.cs') -Raw
$gate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$roadmap = Get-Content -LiteralPath (Join-Path $root 'docs\POST_M9_SOFTWARE_ROADMAP.md') -Raw
$p13doc = Get-Content -LiteralPath (Join-Path $root 'docs\P13_CONTROL_SURFACE.md') -Raw

foreach ($needle in @(
    'new AdaptiveFanProductionController(',
    'GetP13ControlSafety',
    'new P13FanControlSurface(',
    '_p13FanControlSurface.UpdateTelemetry(',
    '_p13FanControlSurface.UpdateRuntimeState(',
    '_p13FanControlSurface.ModeStatusText',
    '_p13FanControlSurface.AdaptivePreviewStatusText',
    '_p13FanControlSurface.GateStatusText'
)) {
    Assert-Contains $main $needle ("P13.5 MainForm integration missing: {0}" -f $needle)
}

foreach ($needle in @(
    'SetModeAsync(',
    'ApplyManualAsync(',
    'AdaptiveFanPolicyShadowEvaluator',
    '_shadowEvaluator.Evaluate(',
    'Automatic candidate preview — NO WRITE',
    'Manual equal fan level',
    'Candidate V1 curves — shadow-only',
    'P13UiSettingsStore.Load()',
    '_lastAuthority',
    'GateStatusText'
)) {
    Assert-Contains $surface $needle ("P13.5 control-surface invariant missing: {0}" -f $needle)
}

$gateCheck = $surface.IndexOf('if (!_controller.ManualExecutionAuthorized)', [StringComparison]::Ordinal)
$safetyRead = $surface.IndexOf('_controlSafetyProvider()', [StringComparison]::Ordinal)
$manualCall = $surface.IndexOf('await _controller.ApplyManualAsync(', [StringComparison]::Ordinal)
if ($gateCheck -lt 0 -or $safetyRead -lt 0 -or $manualCall -lt 0 -or
    $gateCheck -ge $safetyRead -or $safetyRead -ge $manualCall) {
    throw 'P13 Manual order must remain gate -> SafetyGate -> production adapter.'
}

foreach ($forbidden in @(
    'ProcessAutomaticAsync(',
    'TryEnterCustomAsync(',
    'FanCommand(',
    'Hp8C40FanControlBackend',
    'HpOmenBiosWmiClient',
    'SetFanLevel(',
    'PawnIo',
    'AcpiEcReader',
    'NamedPipeFanControlWatchdogLeaseClient'
)) {
    Assert-NotContains $surface $forbidden ("P13 UI/shadow path must not bypass production controls: {0}" -f $forbidden)
}

foreach ($forbidden in @(
    'AdaptiveFanProductionMode',
    'AutomaticExecutionAuthorized',
    'ManualExecutionAuthorized',
    'FanAuthority'
)) {
    Assert-NotContains $settings $forbidden ("P13 settings must never persist mode/authority: {0}" -f $forbidden)
}

Assert-Contains $settings 'ManualEqualLevel is < 10 or > 50' 'Persisted manual preference must reject values outside 10..50.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'Candidate curve must remain physically unvalidated.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'Candidate curve must remain production-unauthorized.'
Assert-Contains $gate 'ManualExecutionAuthorized = false' 'P13 Manual execution gate must remain CLOSED.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'P13 Automatic execution gate must remain CLOSED.'

$prep = $profile.control.adaptivePolicyPreparation
if ([string]$prep.p13UiStatus -ne 'P13_SOFTWARE_COMPLETE_HARDWARE_GATES_CLOSED') { throw 'Unexpected final P13 status.' }
if (-not [bool]$prep.p13ModeSelectorWired -or
    -not [bool]$prep.p13ManualUiWired -or
    -not [bool]$prep.p13AutomaticUiWired -or
    -not [bool]$prep.p13TrayStatusWired -or
    -not [bool]$prep.p13SoftwareComplete) {
    throw 'P13 final software components are not all recorded complete.'
}
if ([bool]$prep.p13ManualExecutionAuthorized -or [bool]$prep.p13AutomaticExecutionAuthorized) { throw 'P13 execution gates must remain closed.' }
if ([bool]$prep.candidateCurveValidated -or [bool]$prep.candidateCurveAuthorizedForProduction) { throw 'P13 must not validate/promote Candidate V1.' }
if ([bool]$profile.control.enabledByDefault) { throw 'P13 must keep control disabled by default.' }
if ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) { throw 'P13 must keep automatic policy OFF.' }

Assert-Contains $roadmap 'P13.5' 'P13 roadmap must record P13.5.'
Assert-Contains $p13doc 'P13 software completion boundary' 'P13 completion document boundary missing.'

Write-Host 'HP 8C40 P13 final software control-surface invariant: PASS'
