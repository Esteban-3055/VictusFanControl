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
$gate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$roadmap = Get-Content -LiteralPath (Join-Path $root 'docs\POST_M9_SOFTWARE_ROADMAP.md') -Raw

foreach ($needle in @(
    'new AdaptiveFanProductionController(',
    'Hp8C40PostM9UserControlGate.ManualExecutionAuthorized',
    'Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized',
    'new P13FanControlSurface('
)) {
    Assert-Contains $main $needle ("P13.2 MainForm integration missing: {0}" -f $needle)
}

foreach ($needle in @(
    'Firmware',
    'Manual (locked)',
    'Automatic (locked)',
    'SetModeAsync(',
    'AdaptiveFanProductionMode.Manual',
    'AdaptiveFanProductionMode.Automatic',
    'Hp8C40AdaptiveCandidateV1.Id'
)) {
    Assert-Contains $surface $needle ("P13.2 surface invariant missing: {0}" -f $needle)
}

foreach ($forbidden in @(
    'ApplyManualAsync(',
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
    Assert-NotContains $surface $forbidden ("P13.2 surface must remain mode-only / no-write: {0}" -f $forbidden)
}

Assert-Contains $gate 'ManualExecutionAuthorized = false' 'P13 manual execution gate must remain compile-time CLOSED.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'P13 automatic execution gate must remain compile-time CLOSED.'

$prep = $profile.control.adaptivePolicyPreparation
if (-not [bool]$prep.p13UiPrepared) { throw 'Profile must record P13 UI preparation.' }
if ([string]$prep.p13UiStatus -ne 'STEP2_MODE_SELECTOR_WIRED_GATES_CLOSED') { throw 'Unexpected P13.2 UI status.' }
if (-not [bool]$prep.p13ModeSelectorWired) { throw 'P13.2 must record mode selector wiring.' }
if ([bool]$prep.p13ManualUiWired) { throw 'P13.2 must not yet claim Manual apply wiring.' }
if ([bool]$prep.p13AutomaticUiWired) { throw 'P13.2 must not yet claim Automatic preview wiring.' }
if ([bool]$prep.p13ManualExecutionAuthorized) { throw 'P13 must not authorize Manual execution.' }
if ([bool]$prep.p13AutomaticExecutionAuthorized) { throw 'P13 must not authorize Automatic execution.' }
if ([bool]$profile.control.enabledByDefault) { throw 'P13 must keep control disabled by default.' }
if ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) { throw 'P13 must keep automatic policy OFF.' }

Assert-Contains $roadmap 'P13.2' 'P13 roadmap must record P13.2.'

Write-Host 'HP 8C40 P13.2 mode-selector wiring invariant: PASS'
