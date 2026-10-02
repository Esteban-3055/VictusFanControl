$ErrorActionPreference='Stop'
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}}

$root=Split-Path -Parent $PSScriptRoot
$p16=Get-Content -LiteralPath (Join-Path $root 'release\p16-target-checkpoint.json') -Raw | ConvertFrom-Json
$p15=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json
$profile=Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P16NormalManualQualification.cs') -Raw
$userGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$main=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$program=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\Program.cs') -Raw
$workflow=Get-Content -LiteralPath (Join-Path $root '.github\workflows\build.yml') -Raw
$doc=Get-Content -LiteralPath (Join-Path $root 'docs\P16_NORMAL_MANUAL.md') -Raw

if([string]$p16.status -cne 'P16A_NORMAL_MANUAL_ARCHITECTURE_CI_PENDING_GATE_CLOSED'){
    throw "Unexpected P16A architecture status: $($p16.status)"
}
if([string]$p15.status -cne 'P15D2_VARIABLE_MANUAL_PHYSICAL_PASS_FORMALLY_CLOSED'){throw 'P16A requires formally closed P15D2.'}
$d15=$p15.guiManualVariableLevel
Assert-True ([bool]$d15.physicalPassed) 'P16A requires P15D2 physical PASS.'
Assert-True ([bool]$d15.evidenceClosed) 'P16A requires P15D2 evidence closure.'
Assert-False ([bool]$d15.executionAuthorized) 'P16A requires P15D2 parent gate re-blocked.'
Assert-False ([bool]$d15.controllerPhysicalExecutionAuthorized) 'P16A requires P15D2 controller gate re-blocked.'
Assert-False ([bool]$d15.physicalGatesOpened) 'P16A requires P15D2 physical gates closed.'

$b=$p16.p15Baseline
if([string]$b.closureHead -cne 'c1e04963448e78a87dccc1719f11d9951f2970a7' -or
   [int]$b.closureCiRunNumber -ne 1180 -or
   [long]$b.closureCiRunId -ne 37050355620 -or
   [string]$b.closureCiResult -cne 'SUCCESS'){
    throw 'P16A P15 baseline identity mismatch.'
}
if([string]$p16.expectedBranch -cne 'feature/victus-8c40-p16-normal-manual'){throw 'P16A branch contract mismatch.'}
if([string]$p16.targetProfileId -cne 'HP-8C40-9D0R1LA-F18'){throw 'P16A target mismatch.'}

$d=$p16.normalManual
Assert-True ([bool]$d.architectureImplemented) 'P16A architecture must be implemented.'
Assert-False ([bool]$d.architectureCiValidated) 'P16A pending state cannot pre-claim CI.'
Assert-False ([bool]$d.executionAuthorized) 'P16A parent physical gate must remain closed.'
Assert-False ([bool]$d.controllerPhysicalExecutionAuthorized) 'P16A controller/source gate must remain closed.'
Assert-False ([bool]$d.physicalGatesOpened) 'P16A physical gates must remain closed.'
Assert-False ([bool]$d.physicalPassed) 'P16A cannot pre-claim physical PASS.'
Assert-False ([bool]$d.evidenceClosed) 'P16A cannot pre-close physical evidence.'
Assert-False ([bool]$d.hardwareExecution) 'P16A must be software-only.'
Assert-True ([bool]$d.normalApplicationLaunchRequired) 'P16 must use normal application launch.'
Assert-False ([bool]$d.specialQualificationStartupModeAllowed) 'P16 must not introduce a special startup mode.'
Assert-False ([bool]$d.harnessImplemented) 'P16A architecture commit must not pre-claim harness implementation.'
Assert-False ([bool]$d.evidencePackagerImplemented) 'P16A architecture commit must not pre-claim evidence packager.'
Assert-False ([bool]$d.independentFailsafeImplemented) 'P16A architecture commit must not pre-claim failsafe.'
if([string]$d.requiredToken -cne '8C40-P16-NORMAL-MANUAL-30-40-30'){throw 'P16 token mismatch.'}
if([int]$d.initialLevel -ne 30 -or [int]$d.changedLevel -ne 40 -or [int]$d.returnLevel -ne 30){throw 'P16 level sequence mismatch.'}
$g=@($d.expectedOwnedGenerations)
if($g.Count -ne 3 -or [int]$g[0] -ne 3 -or [int]$g[1] -ne 5 -or [int]$g[2] -ne 7){throw 'P16 generation contract must be 3/5/7.'}

Assert-False ([bool]$p16.safetyBoundary.controlEnabledByDefault) 'P16A must keep default control OFF.'
Assert-False ([bool]$p16.safetyBoundary.automaticPolicyEnabled) 'P16A must keep automatic policy OFF.'
Assert-False ([bool]$p16.safetyBoundary.permanentUserManualExecutionAuthorized) 'P16A must keep permanent Manual closed.'
Assert-False ([bool]$p16.safetyBoundary.automaticExecutionAuthorized) 'P16A must keep Automatic closed.'
Assert-False ([bool]$p16.safetyBoundary.candidateCurvePhysicallyValidated) 'P16A must keep Candidate V1 unvalidated.'
Assert-False ([bool]$p16.safetyBoundary.candidateCurveAuthorizedForProduction) 'P16A must keep Candidate V1 unpromoted.'
Assert-False ([bool]$profile.control.enabledByDefault) 'P16A must keep profile control disabled by default.'
Assert-Contains $userGate 'ManualExecutionAuthorized = false' 'P16A permanent Manual compile gate must remain false.'
Assert-Contains $userGate 'AutomaticExecutionAuthorized = false' 'P16A permanent Automatic compile gate must remain false.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'P16A Candidate V1 physical flag must remain false.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'P16A Candidate V1 production flag must remain false.'
Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P16A qualification source gate must be hard-closed.'

foreach($needle in @(
 'p16NormalManualQualificationAuthorized',
 'Hp8C40P16NormalManualQualificationGate.PhysicalExecutionAuthorized',
 'Hp8C40PostM9UserControlGate.ManualExecutionAuthorized ||',
 'Hp8C40TargetProfile.Instance.Id'
)){Assert-Contains $main $needle ("P16A normal-path wiring missing: {0}" -f $needle)}

Assert-NotContains $program '--8c40-p16' 'P16A must not add a P16 command-line test mode.'
Assert-NotContains $program 'P16NormalManual' 'P16A must not add P16 to Program hardware-test modes.'
Assert-False ([bool]$p16.promotion.manualExecutionAuthorized) 'P16A must not promote permanent Manual.'
Assert-False ([bool]$p16.promotion.automaticMayOpen) 'P16A must not open Automatic.'
Assert-False ([bool]$p16.promotion.candidateCurveMayBePromoted) 'P16A must not promote Candidate V1.'
Assert-False ([bool]$p16.promotion.controlMayEnableByDefault) 'P16A must not enable startup control.'

Assert-Contains $workflow 'HP 8C40 P16A normal Manual architecture invariant' 'P16A invariant must run in CI.'
Assert-Contains $doc 'No P16 command-line startup mode' 'P16 documentation must preserve normal-app boundary.'

Write-Host 'PASS: P16A architecture is hard-closed, normal-app based, software-only, and preserves P15/Automatic/Candidate boundaries.' -ForegroundColor Green
