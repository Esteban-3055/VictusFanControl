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

foreach ($needle in @(
    'new AdaptiveFanProductionController(',
    'GetP13ControlSafety',
    'new P13FanControlSurface(',
    '_p13FanControlSurface.UpdateTelemetry(',
    '_p13FanControlSurface.UpdateRuntimeState('
)) {
    Assert-Contains $main $needle ("P13.4 MainForm integration missing: {0}" -f $needle)
}

foreach ($needle in @(
    'SetModeAsync(',
    'ApplyManualAsync(',
    'AdaptiveFanPolicyShadowEvaluator',
    '_shadowEvaluator.Evaluate(',
    'Automatic candidate preview — NO WRITE',
    'Recommended equal level:',
    'Raw demand:',
    'Notional intent:',
    'Candidate V1 curves — shadow-only',
    'CpuTemperatureCurve',
    'GpuTemperatureCurve',
    'CpuPowerCurve',
    'GpuPowerCurve',
    'CpuLoadCurve',
    'GpuLoadCurve'
)) {
    Assert-Contains $surface $needle ("P13.4 surface invariant missing: {0}" -f $needle)
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
    Assert-NotContains $surface $forbidden ("P13.4 UI/shadow path must not bypass production controls: {0}" -f $forbidden)
}

Assert-Contains $candidate 'PhysicallyValidated = false' 'Candidate curve must remain physically unvalidated.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'Candidate curve must remain production-unauthorized.'
Assert-Contains $gate 'ManualExecutionAuthorized = false' 'P13 Manual gate must remain CLOSED.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'P13 Automatic gate must remain CLOSED.'

foreach ($forbidden in @(
    'AdaptiveFanProductionMode',
    'AutomaticExecutionAuthorized',
    'ManualExecutionAuthorized'
)) {
    Assert-NotContains $settings $forbidden ("P13 settings must not persist mode/authority: {0}" -f $forbidden)
}

$prep = $profile.control.adaptivePolicyPreparation
if ([string]$prep.p13UiStatus -ne 'STEP4_AUTOMATIC_SHADOW_PREVIEW_WIRED_GATE_CLOSED') { throw 'Unexpected P13.4 status.' }
if (-not [bool]$prep.p13ModeSelectorWired -or -not [bool]$prep.p13ManualUiWired -or -not [bool]$prep.p13AutomaticUiWired) { throw 'P13.4 UI components must all be wired.' }
if ([bool]$prep.p13ManualExecutionAuthorized -or [bool]$prep.p13AutomaticExecutionAuthorized) { throw 'P13 execution gates must remain closed.' }
if ([bool]$prep.candidateCurveValidated -or [bool]$prep.candidateCurveAuthorizedForProduction) { throw 'P13 must not validate/promote the candidate curve.' }
if ([bool]$profile.control.enabledByDefault) { throw 'P13 must keep control disabled by default.' }
if ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) { throw 'P13 must keep automatic policy OFF.' }

Assert-Contains $roadmap 'P13.4' 'P13 roadmap must record P13.4.'

Write-Host 'HP 8C40 P13.4 automatic shadow-preview invariant: PASS'
