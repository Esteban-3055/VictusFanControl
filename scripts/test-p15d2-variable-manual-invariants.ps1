$ErrorActionPreference='Stop'

function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}}

$root=Split-Path -Parent $PSScriptRoot
$contract=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json
$gate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P15D2VariableManualQualification.cs') -Raw
$userGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$adapter=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionController.cs') -Raw
$surface=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P13FanControlSurface.cs') -Raw
$program=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\Program.cs') -Raw
$main=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$harness=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15d2-variable-manual.ps1') -Raw
$packager=Get-Content -LiteralPath (Join-Path $root 'scripts\package-p15d2-evidence.ps1') -Raw
$failsafe=Get-Content -LiteralPath (Join-Path $root 'scripts\watchdog-p15d2-service-failsafe-8c40.ps1') -Raw
$workflow=Get-Content -LiteralPath (Join-Path $root '.github\workflows\build.yml') -Raw
$doc=Get-Content -LiteralPath (Join-Path $root 'docs\P15_TARGET_CHECKPOINT.md') -Raw
$p15d1Invariant=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15d1-tray-exit-invariants.ps1') -Raw

$status=[string]$contract.status
if($status -notin @(
    'P15D2_VARIABLE_MANUAL_PREPARATION_CI_PENDING_GATE_CLOSED',
    'P15D2_VARIABLE_MANUAL_IMPLEMENTATION_CI_PENDING_GATE_CLOSED'
)){
    throw "Unexpected P15D2 preparation status: $status"
}

Assert-True ([bool]$contract.guiLifecycleTrayExit.physicalPassed) 'P15D2 requires P15D1 physical PASS.'
Assert-True ([bool]$contract.guiLifecycleTrayExit.evidenceClosed) 'P15D2 requires P15D1 evidence closure.'
Assert-False ([bool]$contract.guiLifecycleTrayExit.executionAuthorized) 'P15D1 parent harness must remain re-blocked.'
Assert-False ([bool]$contract.guiLifecycleTrayExit.controllerPhysicalExecutionAuthorized) 'P15D1 GUI gate must remain re-blocked.'
if([string]$contract.guiLifecycleTrayExit.physicalPassClosure.sourceHead -cne 'a905b535aa3382a731faf174e95e21d088cd33bd' -or
   [int]$contract.guiLifecycleTrayExit.physicalPassClosure.sourceCiRunNumber -ne 1136 -or
   [long]$contract.guiLifecycleTrayExit.physicalPassClosure.sourceCiRunId -ne 36968252221 -or
   [string]$contract.guiLifecycleTrayExit.physicalPassClosure.result -cne 'PASS'){
    throw 'P15D2 P15D1 physical evidence prerequisite mismatch.'
}

$d=$contract.guiManualVariableLevel
Assert-True ([bool]$d.preparationImplemented) 'P15D2 preparation contract must be implemented.'
Assert-False ([bool]$d.preparationClosure.closed) 'P15D2 implementation stage cannot pre-close software evidence.'
Assert-False ([bool]$d.executionAuthorized) 'P15D2 parent harness physical gate must remain closed.'
Assert-False ([bool]$d.controllerPhysicalExecutionAuthorized) 'P15D2 GUI physical gate must remain closed.'
Assert-False ([bool]$d.physicalPassed) 'P15D2 cannot pre-claim physical PASS.'
Assert-False ([bool]$d.evidenceClosed) 'P15D2 cannot pre-close physical evidence.'
Assert-False ([bool]$d.hardwareExecution) 'P15D2 software preparation must record no hardware execution.'
Assert-False ([bool]$d.physicalGatesOpened) 'P15D2 software preparation must keep physical gates closed.'

if($status -eq 'P15D2_VARIABLE_MANUAL_PREPARATION_CI_PENDING_GATE_CLOSED'){
    Assert-False ([bool]$d.preparationCiValidated) 'Initial P15D2 preparation cannot pre-claim CI.'
    Assert-False ([bool]$d.runtimeImplementationComplete) 'Initial P15D2 preparation cannot pre-claim runtime implementation.'
    Assert-False ([bool]$d.runtimeImplementationCiValidated) 'Initial P15D2 preparation cannot pre-claim runtime CI.'
}else{
    Assert-True ([bool]$d.preparationCiValidated) 'P15D2 runtime implementation requires CI-validated preparation.'
    $base=$d.preparationBaseCi
    if([string]$base.head -cne 'b55742effc1b46112656ef2febdd96275389dbfc' -or
       [int]$base.runNumber -ne 1146 -or
       [long]$base.runId -ne 36972919383 -or
       [string]$base.result -cne 'SUCCESS'){
        throw 'P15D2 preparation base CI identity mismatch.'
    }
    Assert-True ([bool]$d.runtimeImplementationComplete) 'P15D2 implementation-pending state requires complete runtime/harness implementation.'
    Assert-False ([bool]$d.runtimeImplementationCiValidated) 'P15D2 implementation-pending state cannot pre-claim runtime CI.'
    $ri=$d.runtimeImplementation
    foreach($flag in @(
        'programBoundaryImplemented','exactTargetBoundaryImplemented','realP13VariableManualPathReused',
        'readyRequiresThreeHealthySafetySamples','initial30ParentFenceImplemented','changed40ParentFenceImplemented',
        'duplicate40HoldCustomFenceImplemented','duplicate40NoRetransmitParentAuditImplemented','return30ParentFenceImplemented',
        'realFirmwareStrongRestoreImplemented','sameOwnedSessionAuditImplemented','causalThreeWriteWatchdogAuditImplemented',
        'nativeTrackedGuiProcess','independentFailsafeImplemented','evidencePackagerImplemented','evidencePackagingSelfTestImplemented'
    )){Assert-True ([bool]$ri.$flag) ("P15D2 runtime implementation flag missing: {0}" -f $flag)}
    Assert-False ([bool]$ri.physicalExecution) 'P15D2 runtime implementation must remain software-only.'
    $failed=@($d.failedCiHistoryPreserved)
    if($failed.Count -ne 1 -or [int]$failed[0].runNumber -ne 1148 -or
       [long]$failed[0].runId -ne 36973216494 -or
       [string]$failed[0].head -cne '0e39d6c0583a90b135f2bcb1b903b01a822ed3b5' -or
       [string]$failed[0].result -cne 'FAILURE' -or
       [string]$failed[0].failureStep -cne 'Build'){
        throw 'P15D2 failed-CI history mismatch.'
    }
    Assert-False ([bool]$failed[0].hardwareExecution) 'P15D2 failed CI must record no hardware execution.'
    Assert-False ([bool]$failed[0].physicalGatesOpened) 'P15D2 failed CI must keep physical gates closed.'
}

if([string]$d.prerequisite -cne 'P15D1 tray-exit lifecycle physical PASS formally closed'){throw 'P15D2 prerequisite mismatch.'}
if([string]$d.expectedBranch -cne 'feature/victus-8c40-p15-hardware-checkpoint'){throw 'P15D2 branch contract mismatch.'}
if([string]$d.requiredToken -cne '8C40-P15D2-MANUAL30-40-40-30'){throw 'P15D2 token contract mismatch.'}
if([int]$d.initialLevel -ne 30 -or [int]$d.changedLevel -ne 40 -or [int]$d.returnLevel -ne 30){throw 'P15D2 level sequence mismatch.'}
if([int]$d.exactManualModeRequests -ne 1 -or [int]$d.exactApplyManualCalls -ne 4 -or [int]$d.exactFirmwareModeRequests -ne 1 -or [int]$d.exactAutomaticModeRequests -ne 0){throw 'P15D2 bounded real-GUI interaction counts mismatch.'}
Assert-True ([bool]$d.requiresRealP13Surface) 'P15D2 must use the real P13 surface.'
Assert-True ([bool]$d.requiresProductionAdapter) 'P15D2 must use AdaptiveFanProductionController.'
Assert-True ([bool]$d.requiresProductionCoordinator) 'P15D2 must use FanControlCoordinator.'
Assert-True ([bool]$d.requiresSameOwnershipSessionAcrossChangedLevels) 'P15D2 must preserve one ownership session across changed levels.'
Assert-True ([bool]$d.requiresDuplicate40NoRetransmit) 'P15D2 must physically prove duplicate 40/40 no-retransmit.'
Assert-True ([bool]$d.requiresStrongRestore) 'P15D2 must end in production strong restore.'
Assert-False ([bool]$d.candidateCurveMayBePromoted) 'P15D2 must not promote Candidate V1.'
Assert-False ([bool]$d.automaticMayOpen) 'P15D2 must not open Automatic.'
Assert-False ([bool]$d.userFacingManualMayOpen) 'P15D2 must not open normal user Manual.'
Assert-False ([bool]$d.m9cOrM9dQualificationMayReopen) 'P15D2 must not reopen M9C/M9D.'

Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15D2 source gate must be hard-closed.'
Assert-Contains $gate 'RequiredToken = "8C40-P15D2-MANUAL30-40-40-30"' 'P15D2 source token mismatch.'
Assert-Contains $gate 'InitialLevel = 30' 'P15D2 initial level mismatch.'
Assert-Contains $gate 'ChangedLevel = 40' 'P15D2 changed level mismatch.'
Assert-Contains $gate 'ReturnLevel = 30' 'P15D2 return level mismatch.'
Assert-Contains $gate 'NormalUserExecutionGatesClosed()' 'P15D2 source gate must assert normal-user isolation.'

foreach($needle in @(
    '_lastManualAppliedLevel == equalFanLevel',
    'AdaptiveFanProductionActionKind.HoldCustom',
    'unchanged manual target was not retransmitted',
    'AdaptiveFanProductionActionKind.ApplyChangedLevel',
    'new FanCommand('
)){Assert-Contains $adapter $needle ("P15D2 existing production-adapter prerequisite missing: {0}" -f $needle)}

foreach($needle in @(
    '_manualLevel.Minimum = 10;',
    '_manualLevel.Maximum = 50;',
    'P13ControlInteractionKind.ManualApply',
    'qualification pre-action fence rejected the interaction'
)){Assert-Contains $surface $needle ("P15D2 real P13 surface prerequisite missing: {0}" -f $needle)}

if($status -eq 'P15D2_VARIABLE_MANUAL_IMPLEMENTATION_CI_PENDING_GATE_CLOSED'){
    foreach($needle in @(
        '--8c40-p15d2-variable-manual-test','--8c40-p15d2-test-token','--8c40-p15d2-marker-root',
        'Hp8C40P15D2VariableManualQualificationGate.PhysicalExecutionAuthorized',
        'Hp8C40P15D2VariableManualQualificationGate.NormalUserExecutionGatesClosed()'
    )){Assert-Contains $program $needle ("P15D2 Program boundary missing: {0}" -f $needle)}

    foreach($needle in @(
        '_p15d2VariableManualHardwareTest','TryPublishP15D2ReadyAsync','EnsureP15D2QualificationEnvelope',
        'IsP15D2ControlInteractionAuthorized','OnP15D2ControlInteraction',
        'P15D2-PARENT-30-VERIFIED|','P15D2-PARENT-40-VERIFIED|','P15D2-PARENT-HOLD40-VERIFIED|','P15D2-PARENT-RETURN30-VERIFIED|',
        'AdaptiveFanProductionActionKind.EnterCustomAndApply','AdaptiveFanProductionActionKind.ApplyChangedLevel',
        'AdaptiveFanProductionActionKind.HoldCustom','AdaptiveFanProductionActionKind.RestoreFirmware',
        'LastRestoreEvidence','WatchdogReleaseVerified','WriteP15D2Json'
    )){Assert-Contains $main $needle ("P15D2 MainForm runtime wiring missing: {0}" -f $needle)}

    foreach($needle in @(
        'HARD VERSIONED AUTHORIZATION BARRIER','P15D2_VARIABLE_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI',
        'Assert-StableSetpoint 30 30','Assert-StableSetpoint 40 40','Assert-OwnedJournal',
        'Assert-NoRetransmitAfterBoundary','P15D2-PARENT-30-VERIFIED|','P15D2-PARENT-40-VERIFIED|',
        'P15D2-PARENT-HOLD40-VERIFIED|','P15D2-PARENT-RETURN30-VERIFIED|',
        'WATCHDOG WRITE_INTENT ACK','WATCHDOG COMMIT ACK','Assert-CausalServiceLog',
        'PREPARE < 30 write/commit < 40 write/commit < return-30 write/commit < RESTORE_BEGIN < RELEASE',
        '--8c40-p15d2-variable-manual-test','Assert-StableSetpoint 255 255','Test-FailsafeTakeover'
    )){Assert-Contains $harness $needle ("P15D2 harness contract missing: {0}" -f $needle)}

    foreach($forbidden in @('SetFanLevel','--restore-hp-auto','git clean','Start-Service','Stop-Service','Restart-Service')){
        Assert-NotContains $harness $forbidden ("P15D2 parent harness forbidden bypass: {0}" -f $forbidden)
    }

    foreach($needle in @(
        'sourceEvidencePreserved=$true','gitCleanUsed=$false','parent-30-proof','parent-40-proof',
        'parent-hold40-proof','parent-return30-proof','production-adapter-source','coordinator-source','failsafe-source'
    )){Assert-Contains $packager $needle ("P15D2 packager contract missing: {0}" -f $needle)}

    foreach($needle in @('P15D2 FAILSAFE ARMED:','P15D2 FAILSAFE TAKEOVER:','P15D2 FAILSAFE CONTROLLER-KILL:','P15D2 FAILSAFE RECOVERED:')){
        Assert-Contains $failsafe $needle ("P15D2 failsafe contract missing: {0}" -f $needle)
    }

    Assert-Contains $workflow 'HP 8C40 P15D2 evidence packaging self-test' 'P15D2 evidence packaging self-test must run in CI.'
}

Assert-False ([bool]$contract.safetyBoundary.controlEnabledByDefault) 'P15D2 must keep default control OFF.'
Assert-False ([bool]$contract.safetyBoundary.automaticPolicyEnabled) 'P15D2 must keep automatic policy OFF.'
Assert-False ([bool]$contract.safetyBoundary.manualExecutionAuthorized) 'P15D2 must keep normal user Manual closed.'
Assert-False ([bool]$contract.safetyBoundary.automaticExecutionAuthorized) 'P15D2 must keep normal user Automatic closed.'
Assert-False ([bool]$contract.automatic.executionAuthorized) 'P15D2 must keep Automatic physical execution closed.'
Assert-False ([bool]$contract.safetyBoundary.candidateCurvePhysicallyValidated) 'P15D2 must keep Candidate V1 physically unvalidated.'
Assert-False ([bool]$contract.safetyBoundary.candidateCurveAuthorizedForProduction) 'P15D2 must keep Candidate V1 production-unauthorized.'
Assert-False ([bool]$contract.safetyBoundary.m9cQualificationConstructionAuthorized) 'P15D2 must keep M9C closed.'
Assert-False ([bool]$contract.safetyBoundary.m9dQualificationConstructionAuthorized) 'P15D2 must keep M9D closed.'
Assert-Contains $userGate 'ManualExecutionAuthorized = false' 'P15D2 normal user Manual compile gate must remain false.'
Assert-Contains $userGate 'AutomaticExecutionAuthorized = false' 'P15D2 normal user Automatic compile gate must remain false.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'P15D2 Candidate V1 physical flag must remain false.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'P15D2 Candidate V1 production flag must remain false.'

foreach($needle in @(
    'qualificationSource','P15D2VariableManualQualification.cs',
    'futureHarness','test-p15d2-variable-manual.ps1',
    'futureEvidencePackager','package-p15d2-evidence.ps1',
    'futureIndependentFailsafe','watchdog-p15d2-service-failsafe-8c40.ps1'
)){Assert-Contains ($d.plannedFiles | ConvertTo-Json -Depth 5) $needle ("P15D2 planned-file contract missing: {0}" -f $needle)}

Assert-Contains $p15d1Invariant '$postP15D1State' 'P15D1 historical invariant must explicitly accept later P15D2 states while preserving P15D1 closure.'
Assert-Contains $doc 'P15D2 preparation — real GUI variable Manual levels' 'P15D2 documentation section is missing.'

Write-Host 'PASS: P15D2 variable-Manual preparation/runtime is software-only, hard-closed, and preserves all prior physical closures.' -ForegroundColor Green
