$ErrorActionPreference = 'Stop'

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0) { throw $Message }
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message) {
    if ($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0) { throw $Message }
}

$root = Split-Path -Parent $PSScriptRoot
$main = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$gate = Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$profile = Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$roadmap = Get-Content -LiteralPath (Join-Path $root 'docs\POST_M9_SOFTWARE_ROADMAP.md') -Raw

foreach ($needle in @(
    'BuildP13FanControlSurface',
    'Firmware (current)',
    'Manual (locked)',
    'Automatic (locked)',
    'Hp8C40PostM9UserControlGate.ManualExecutionAuthorized',
    'Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized',
    'Hp8C40AdaptiveCandidateV1.Id',
    'this tab has no control callbacks'
)) {
    Assert-Contains $main $needle ("P13 step 1 GUI invariant missing: {0}" -f $needle)
}

Assert-Contains $gate 'ManualExecutionAuthorized = false' 'P13 manual execution gate must remain compile-time CLOSED.'
Assert-Contains $gate 'AutomaticExecutionAuthorized = false' 'P13 automatic execution gate must remain compile-time CLOSED.'
Assert-NotContains $main 'ApplyManualAsync(' 'P13 step 1 must not wire manual execution into MainForm.'
Assert-NotContains $main 'ProcessAutomaticAsync(' 'P13 step 1 must not wire automatic execution into MainForm.'

$prep = $profile.control.adaptivePolicyPreparation
if (-not [bool]$prep.p13UiPrepared) { throw 'Profile must record P13 step 1 UI preparation.' }
if ([string]$prep.p13UiStatus -ne 'STEP1_PRESENTATION_SURFACE_GATES_CLOSED') { throw 'Unexpected P13 UI status.' }
if ([bool]$prep.p13ManualUiWired) { throw 'P13 step 1 must not claim Manual UI wiring.' }
if ([bool]$prep.p13AutomaticUiWired) { throw 'P13 step 1 must not claim Automatic UI wiring.' }
if ([bool]$prep.p13ManualExecutionAuthorized) { throw 'P13 step 1 must not authorize Manual execution.' }
if ([bool]$prep.p13AutomaticExecutionAuthorized) { throw 'P13 step 1 must not authorize Automatic execution.' }
if ([bool]$profile.control.enabledByDefault) { throw 'P13 must keep control disabled by default.' }
if ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) { throw 'P13 must keep automatic policy OFF.' }

Assert-Contains $roadmap 'P13.1' 'P13 roadmap must record the stepwise UI state.'

Write-Host 'HP 8C40 P13.1 presentation-only control surface invariant: PASS'
