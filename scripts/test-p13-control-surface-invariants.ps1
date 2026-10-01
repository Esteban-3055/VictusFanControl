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
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$roadmap = Get-Content -LiteralPath (Join-Path $root 'docs\POST_M9_SOFTWARE_ROADMAP.md') -Raw

foreach ($needle in @(
    'new AdaptiveFanProductionController(',
    'GetP13ControlSafety',
    'new P13FanControlSurface('
)) {
    Assert-Contains $main $needle ("P13.3 MainForm integration missing: {0}" -f $needle)
}

foreach ($needle in @(
    'SetModeAsync(',
    'ApplyManualAsync(',
    '_controller.ManualExecutionAuthorized',
    'Manual equal fan level',
    'Level 10..50:',
    'P13UiSettingsStore.Load()',
    'SaveManualEqualLevel('
)) {
    Assert-Contains $surface $needle ("P13.3 surface invariant missing: {0}" -f $needle)
}

$gateCheck = $surface.IndexOf('if (!_controller.ManualExecutionAuthorized)', [StringComparison]::Ordinal)
$safetyRead = $surface.IndexOf('_controlSafetyProvider()', [StringComparison]::Ordinal)
$manualCall = $surface.IndexOf('await _controller.ApplyManualAsync(', [StringComparison]::Ordinal)
if ($gateCheck -lt 0 -or $safetyRead -lt 0 -or $manualCall -lt 0 -or
    $gateCheck -ge $safetyRead -or $safetyRead -ge $manualCall) {
    throw 'P13.3 Manual order must be closed-gate check -> SafetyGate provider -> production-controller apply.'
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
    Assert-NotContains $surface $forbidden ("P13.3 UI must not bypass the production controller: {0}" -f $forbidden)
}

foreach ($needle in @(
    'ManualEqualLevel',
    'ManualEqualLevel is < 10 or > 50',
    'SaveManualEqualLevel',
    'p13-ui-settings.json'
)) {
    Assert-Contains $settings $needle ("P13.3 settings invariant missing: {0}" -f $needle)
}
foreach ($forbidden in @(
    'AdaptiveFanProductionMode',
    'AutomaticExecutionAuthorized',
    'ManualExecutionAuthorized',
    'enabledByDefault',
    'automaticPolicyEnabled'
)) {
    Assert-NotContains $settings $forbidden ("P13 settings must never persist authority/mode: {0}" -f $forbidden)
}

Assert-Contains $gate 'ManualExecutionAuthorized = false' 'P13 manual execution gate must remain CLOSED.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'P13 automatic execution gate must remain CLOSED.'

$prep = $profile.control.adaptivePolicyPreparation
if ([string]$prep.p13UiStatus -ne 'STEP3_MANUAL_UI_WIRED_GATE_CLOSED') { throw 'Unexpected P13.3 status.' }
if (-not [bool]$prep.p13ModeSelectorWired) { throw 'P13 mode selector must remain wired.' }
if (-not [bool]$prep.p13ManualUiWired) { throw 'P13.3 must record Manual UI wiring.' }
if ([bool]$prep.p13AutomaticUiWired) { throw 'P13.3 must not yet claim Automatic preview wiring.' }
if ([bool]$prep.p13ManualExecutionAuthorized -or [bool]$prep.p13AutomaticExecutionAuthorized) { throw 'P13 execution gates must remain closed.' }
if ([bool]$profile.control.enabledByDefault) { throw 'P13 must keep control disabled by default.' }
if ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) { throw 'P13 must keep automatic policy OFF.' }

Assert-Contains $roadmap 'P13.3' 'P13 roadmap must record P13.3.'

Write-Host 'HP 8C40 P13.3 manual UI / persistence invariant: PASS'
