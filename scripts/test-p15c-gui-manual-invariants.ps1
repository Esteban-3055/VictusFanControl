$ErrorActionPreference='Stop'

function Assert-True([bool]$v,[string]$m){if(-not $v){throw $m}}
function Assert-False([bool]$v,[string]$m){if($v){throw $m}}
function Assert-Contains([string]$t,[string]$n,[string]$m){if($t.IndexOf($n,[StringComparison]::Ordinal)-lt 0){throw $m}}
function Assert-NotContains([string]$t,[string]$n,[string]$m){if($t.IndexOf($n,[StringComparison]::Ordinal)-ge 0){throw $m}}

$root=Split-Path -Parent $PSScriptRoot
$contract=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json
$profile=Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$userGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$adaptive=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionController.cs') -Raw
$qualification=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P15CGuiManualQualification.cs') -Raw
$program=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\Program.cs') -Raw
$main=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$surface=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P13FanControlSurface.cs') -Raw
$harness=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15c-gui-manual.ps1') -Raw
$failsafe=Get-Content -LiteralPath (Join-Path $root 'scripts\watchdog-p15c-service-failsafe-8c40.ps1') -Raw
$packager=Get-Content -LiteralPath (Join-Path $root 'scripts\package-p15c-evidence.ps1') -Raw
$doc=Get-Content -LiteralPath (Join-Path $root 'docs\P15_TARGET_CHECKPOINT.md') -Raw

$status=[string]$contract.status
$allowed=@(
 'P15C_GUI_MANUAL_PREPARATION_CI_PENDING_GATE_CLOSED',
 'P15C_GUI_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED',
 'P15C_GUI_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI',
 'P15C_GUI_MANUAL_PHYSICAL_PASS_FORMALLY_CLOSED',
 'P15C_GUI_MANUAL_STALE_SAFETY_FAIL_CLOSED_GATE_CLOSED',
 'P15C_GUI_MANUAL_STALE_SAFETY_CORRECTION_CI_PENDING_GATE_CLOSED',
 'P15C_GUI_MANUAL_STALE_SAFETY_CORRECTION_CI_PASS_GATE_CLOSED',
 'P15C_GUI_MANUAL_REAUTHORIZED_AFTER_STALE_SAFETY_CORRECTION_AWAITING_SAME_HEAD_CI'
)
if($status -notin $allowed){throw 'P15C contract state mismatch.'}

Assert-True ([bool]$contract.startupNoWrite.physicalPassed) 'P15C requires P15A physical PASS.'
Assert-True ([bool]$contract.startupNoWrite.evidenceClosed) 'P15C requires P15A evidence closure.'
Assert-False ([bool]$contract.startupNoWrite.executionAuthorized) 'P15A must remain re-blocked.'
Assert-True ([bool]$contract.manual30.physicalPassed) 'P15C requires P15B physical PASS.'
Assert-True ([bool]$contract.manual30.evidenceClosed) 'P15C requires P15B evidence closure.'
Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B harness authorization must remain closed.'
Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B controller gate must remain closed.'

$g=$contract.guiManual
Assert-True ([bool]$g.preparationImplemented) 'P15C preparation must be implemented.'
$failedPreparation=@($g.preparationClosure.failedCiHistoryPreserved)
if($failedPreparation.Count -ne 2 -or
   [int]$failedPreparation[0].runNumber -ne 1117 -or
   [long]$failedPreparation[0].runId -ne 36941758398 -or
   [string]$failedPreparation[0].head -cne '33bb63828561ff88664f1896f76a8e2c95863698' -or
   [string]$failedPreparation[0].result -cne 'FAILURE' -or
   [string]$failedPreparation[0].failureStep -cne 'Build' -or
   [int]$failedPreparation[1].runNumber -ne 1119 -or
   [long]$failedPreparation[1].runId -ne 36949301023 -or
   [string]$failedPreparation[1].head -cne 'b7ed2871a9cab0bbefb3375e0ea005a800054a61' -or
   [string]$failedPreparation[1].result -cne 'FAILURE' -or
   [string]$failedPreparation[1].failureStep -cne 'HP 8C40 P15C real-GUI Manual preparation invariant'){
    throw 'P15C failed preparation CI history mismatch.'
}
foreach($entry in $failedPreparation){
    Assert-False ([bool]$entry.hardwareExecution) 'P15C failed preparation CI must record no hardware execution.'
    Assert-False ([bool]$entry.physicalGatesOpened) 'P15C failed preparation CI must record physical gates closed.'
}
if([string]$g.prerequisite -cne 'P15B Manual30 physical PASS formally closed'){throw 'P15C prerequisite mismatch.'}
if([string]$g.expectedBranch -cne 'feature/victus-8c40-p15-hardware-checkpoint'){throw 'P15C branch contract mismatch.'}
if([string]$g.requiredToken -cne '8C40-P15C-GUI-MANUAL30'){throw 'P15C token contract mismatch.'}
if([int]$g.equalLevel -ne 30 -or [int]$g.exactManualModeRequests -ne 1 -or [int]$g.exactApplyManualCalls -ne 1 -or [int]$g.exactFirmwareModeRequests -ne 1){throw 'P15C bounded GUI transaction contract mismatch.'}
Assert-True ([bool]$g.requiresRealP13Surface) 'P15C must use the real P13 surface.'
Assert-True ([bool]$g.requiresProductionAdapter) 'P15C must use AdaptiveFanProductionController.'
Assert-True ([bool]$g.requiresProductionBackendFactory) 'P15C must use the normal production backend factory.'
Assert-True ([bool]$g.strongRestoreRequired) 'P15C must require strong restore.'
Assert-True ([bool]$g.parentOwnedProofBeforeFirmwareClick) 'P15C must fence the Firmware click behind parent OWNED proof.'
foreach($p in @('preActionInteractionFenceRequired','wrongManualLevelMustBlockBeforeProductionAdapter','duplicateManualApplyMustBlockBeforeProductionAdapter','outOfOrderFirmwareMustBlockBeforeProductionAdapter','concurrentControlInteractionMustFailClosed','qualificationLevelLockedInUi','parentOwnedMarkerMustBindGuiPidAndStartIdentity')){Assert-True ([bool]$g.$p) ("P15C pre-action safety contract missing: {0}" -f $p)}
$hardening=$g.preActionFenceHardening
Assert-True ([bool]$hardening.required) 'P15C pre-action fence hardening must be required.'
Assert-True ([bool]$hardening.implementationComplete) 'P15C pre-action fence hardening implementation must be complete.'
if([string]$hardening.discoveredOnHead -cne '2520cfc67ed416e6497310e31030b31eadc24645' -or [int]$hardening.discoveredHeadCiRunNumber -ne 1118 -or [long]$hardening.discoveredHeadCiRunId -ne 36942085290 -or [string]$hardening.discoveredHeadCiResult -cne 'SUCCESS'){throw 'P15C pre-action fence discovery baseline mismatch.'}
Assert-False ([bool]$hardening.physicalExecutionOccurred) 'P15C pre-action fence finding must record no hardware execution.'
Assert-False ([bool]$hardening.physicalGatesOpened) 'P15C pre-action fence finding must record physical gates closed.'
Assert-False ([bool]$g.userFacingManualGateMayOpen) 'P15C must not globally open user-facing Manual.'
Assert-False ([bool]$g.automaticGateMayOpen) 'P15C must not open Automatic.'
Assert-False ([bool]$g.candidateCurveMayBePromoted) 'P15C must not promote Candidate V1.'
Assert-False ([bool]$g.m9cOrM9dQualificationMayReopen) 'P15C must not reopen M9C/M9D.'

if($status -eq 'P15C_GUI_MANUAL_PREPARATION_CI_PENDING_GATE_CLOSED'){
    Assert-False ([bool]$g.preparationCiValidated) 'Pending P15C preparation must not pre-claim CI.'
    Assert-False ([bool]$g.preparationClosure.closed) 'Pending P15C preparation must not pre-close.'
    Assert-False ([bool]$g.executionAuthorized) 'Pending P15C preparation must keep harness gate closed.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'Pending P15C preparation must keep GUI qualification gate closed.'
    Assert-False ([bool]$g.physicalPassed) 'Pending P15C preparation must not pre-claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'Pending P15C preparation must not pre-close evidence.'
    Assert-False ([bool]$hardening.ciValidated) 'Pending P15C pre-action hardening must not pre-claim CI.'
    Assert-False ([bool]$hardening.closure.closed) 'Pending P15C pre-action hardening must not pre-close.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'Pending P15C qualification source gate must remain false.'
}

if($status -eq 'P15C_GUI_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED'){
    Assert-True ([bool]$g.preparationCiValidated) 'Closed P15C preparation must be CI validated.'
    Assert-True ([bool]$g.preparationClosure.closed) 'Closed P15C preparation must record closure.'
    $pc=$g.preparationClosure
    if([string]$pc.result -cne 'PASS' -or
       [string]$pc.implementationHead -cne '356275b56bdfe3324b054e2b79f2bbbf3f4e5067' -or
       [int]$pc.sourceCiRunNumber -ne 1120 -or
       [long]$pc.sourceCiRunId -ne 36949763034 -or
       [string]$pc.sourceCiResult -cne 'SUCCESS'){
        throw 'P15C preparation closure CI identity mismatch.'
    }
    foreach($p in @('powerShellSyntaxValidated','p15aInvariantValidated','p15bInvariantValidated','p15cInvariantValidated','p13SurfaceInvariantValidated','evidencePackagingSelfTestValidated','powerShell51CompatibilityValidated','warningsAsErrorsBuildValidated','preActionFenceHardeningValidated')){Assert-True ([bool]$pc.$p) ("P15C preparation closure missing validation: {0}" -f $p)}
    Assert-False ([bool]$pc.hardwareExecution) 'P15C preparation closure must record no hardware execution.'
    Assert-True ([bool]$hardening.ciValidated) 'P15C preparation closure requires CI-validated pre-action hardening.'
    Assert-True ([bool]$hardening.closure.closed) 'P15C preparation closure requires formally closed pre-action hardening.'
    $hc=$hardening.closure
    if([string]$hc.result -cne 'PASS' -or
       [string]$hc.implementationHead -cne '356275b56bdfe3324b054e2b79f2bbbf3f4e5067' -or
       [int]$hc.sourceCiRunNumber -ne 1120 -or
       [long]$hc.sourceCiRunId -ne 36949763034 -or
       [string]$hc.sourceCiResult -cne 'SUCCESS'){
        throw 'P15C pre-action hardening closure CI identity mismatch.'
    }
    foreach($p in @('p15cInvariantValidated','p13SurfaceInvariantValidated','evidencePackagingSelfTestValidated','powerShell51CompatibilityValidated','warningsAsErrorsBuildValidated')){Assert-True ([bool]$hc.$p) ("P15C pre-action hardening closure missing validation: {0}" -f $p)}
    Assert-False ([bool]$hc.hardwareExecution) 'P15C pre-action hardening closure must record no hardware execution.'
    Assert-False ([bool]$g.executionAuthorized) 'Closed P15C preparation must keep harness gate closed.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'Closed P15C preparation must keep GUI qualification gate closed.'
    Assert-False ([bool]$g.physicalPassed) 'P15C preparation closure cannot pre-claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'P15C preparation closure cannot pre-close physical evidence.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15C preparation closure requires qualification source gate closed.'
}

if($status -eq 'P15C_GUI_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI'){
    Assert-True ([bool]$g.preparationCiValidated) 'P15C authorization requires CI-validated preparation.'
    Assert-True ([bool]$g.preparationClosure.closed) 'P15C authorization requires formally closed preparation.'
    Assert-True ([bool]$hardening.ciValidated) 'P15C authorization requires CI-validated pre-action hardening.'
    Assert-True ([bool]$hardening.closure.closed) 'P15C authorization requires formally closed pre-action hardening.'
    $a=$g.authorization
    if([string]$a.basisHead -cne 'a7129f616d955ae22e09ec5ef999bb6a3b64ffc2' -or
       [int]$a.basisCiRunNumber -ne 1121 -or
       [long]$a.basisCiRunId -ne 36950212076 -or
       [string]$a.basisCiResult -cne 'SUCCESS' -or
       [string]$a.preparationImplementationHead -cne '356275b56bdfe3324b054e2b79f2bbbf3f4e5067' -or
       [int]$a.preparationImplementationCiRunNumber -ne 1120){
        throw 'P15C authorization basis mismatch.'
    }
    Assert-True ([bool]$a.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P15C authorization must require same-HEAD CI success.'
    Assert-True ([bool]$a.preActionFenceHardeningClosed) 'P15C authorization requires closed pre-action hardening.'
    foreach($p in @('p15aAuthorizationOpened','p15bAuthorizationOpened','userFacingManualGateOpened','automaticAuthorizationOpened','candidateCurveAuthorizationOpened','m9cQualificationConstructionOpened','m9dQualificationConstructionOpened','hardwareExecutionAtAuthorizationCommit')){Assert-False ([bool]$a.$p) ("P15C authorization opened forbidden scope: {0}" -f $p)}
    Assert-True ([bool]$g.executionAuthorized) 'P15C authorization must open only the dedicated parent harness gate.'
    Assert-True ([bool]$g.controllerPhysicalExecutionAuthorized) 'P15C authorization must open only the dedicated GUI qualification gate.'
    Assert-False ([bool]$g.physicalPassed) 'P15C authorization must not pre-claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'P15C authorization must not pre-close physical evidence.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P15C authorized state requires qualification source gate open.'
}

if($status -eq 'P15C_GUI_MANUAL_STALE_SAFETY_FAIL_CLOSED_GATE_CLOSED'){
    Assert-False ([bool]$g.executionAuthorized) 'P15C must be re-blocked after target FAIL_CLOSED.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'P15C GUI qualification gate must be re-blocked after target FAIL_CLOSED.'
    Assert-False ([bool]$g.physicalPassed) 'P15C FAIL_CLOSED must not claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'P15C FAIL_CLOSED must not close PASS evidence.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15C source gate must be closed after target FAIL_CLOSED.'
    $f=$g.targetFailClosedAttempt
    if([string]$f.authorizationHead -cne '5eb02ca3b50a6d88dc027b97f2dc76fa5b39490e' -or [int]$f.authorizationCiRunNumber -ne 1122 -or [long]$f.authorizationCiRunId -ne 36950548313 -or [string]$f.authorizationCiResult -cne 'SUCCESS' -or [string]$f.result -cne 'FAIL_CLOSED'){throw 'P15C target FAIL_CLOSED identity mismatch.'}
    if([string]$f.evidenceZipSha256 -cne 'd89bf8903e593b995f5f4f4c3807bfca94aa2eb21d8bd9a2ea0bd177cb2a96ea' -or [string]$f.evidenceSidecarSha256 -cne 'dd1e27d58617ad97aa6adfb01f4aac7d07c2db01f0dcd20d54f4e50521d94518' -or [string]$f.packageManifestSha256 -cne '94fd16849eeccd98889e3b2a33c328a7d29c3281321f7997d12b60d9858a5049'){throw 'P15C target FAIL_CLOSED evidence identity mismatch.'}
    foreach($p in @('archiveIntegrityVerified','embeddedEvidenceHashesVerified','sourceIdentityHashesVerifiedAgainstAuthorizationHead','repositoryHeadMatched','trackedSourceClean','onlyPreservedUntrackedLogsObserved','guiReadyObserved','watchdogProcessIdentityStable','manualModeHeldFirmwareAuthority','customAuthorityPrepared','journalPresentAtGuiFailure','staleSafetyRejectedBeforeCoordinatorBackendApply','shutdownRestoreObserved','cleanupFfFfVerified','failsafeArmed','correctionRequired')){Assert-True ([bool]$f.$p) ("P15C target FAIL_CLOSED expected true: {0}" -f $p)}
    foreach($p in @('backendApplyReached','writeIntentReached','wmiSetFanLevelReached','physical30FanWriteExecuted','manualAppliedMarkerProduced','ownedSetpointEvidenceProduced','ownedJournalEvidenceProduced','failsafeTakeover','physicalPassClaimed','evidenceClosed')){Assert-False ([bool]$f.$p) ("P15C target FAIL_CLOSED expected false: {0}" -f $p)}
    if([int]$f.embeddedEvidenceHashCount -ne 11 -or [int]$f.sourceIdentityHashCount -ne 11 -or [int]$f.guiReadyHealthySafetySamples -ne 3 -or [int]$f.manualModeRequests -ne 1 -or [int]$f.manualApplyRequests -ne 1 -or [int]$f.firmwareModeRequests -ne 0 -or [int]$f.automaticModeRequests -ne 0){throw 'P15C target FAIL_CLOSED bounded evidence mismatch.'}
    $corr=$g.staleSafetyRaceCorrection
    Assert-True ([bool]$corr.required) 'P15C stale-Safety correction must be required.'
    Assert-True ([bool]$corr.mustRefreshSafetyAfterCustomAdmission) 'P15C correction must refresh SafetyGate after Custom admission.'
    Assert-True ([bool]$corr.boundedStaleRetryRequired) 'P15C correction must require bounded stale retry.'
    Assert-True ([bool]$corr.exhaustedRetryMustRestoreFirmware) 'P15C correction must restore Firmware if stale retries exhaust.'
    Assert-False ([bool]$corr.implementationComplete) 'P15C stale-Safety correction cannot be pre-claimed in re-block commit.'
    Assert-False ([bool]$corr.ciValidated) 'P15C stale-Safety correction cannot pre-claim CI.'
    Assert-False ([bool]$corr.closure.closed) 'P15C stale-Safety correction cannot pre-close.'
}

if($status -eq 'P15C_GUI_MANUAL_STALE_SAFETY_CORRECTION_CI_PENDING_GATE_CLOSED'){
    Assert-False ([bool]$g.executionAuthorized) 'P15C correction must keep parent gate closed.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'P15C correction must keep GUI gate closed.'
    Assert-False ([bool]$g.physicalPassed) 'P15C correction cannot claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'P15C correction cannot close PASS evidence.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15C correction requires source gate closed.'
    $corr=$g.staleSafetyRaceCorrection
    Assert-True ([bool]$corr.required) 'P15C stale-Safety correction must remain required.'
    Assert-True ([bool]$corr.implementationComplete) 'P15C stale-Safety correction implementation must be complete.'
    Assert-False ([bool]$corr.ciValidated) 'P15C correction implementation must not pre-claim CI.'
    Assert-False ([bool]$corr.closure.closed) 'P15C correction implementation must not pre-close CI.'
    if([int]$corr.maximumFreshSafetyAttempts -ne 4){throw 'P15C stale-Safety correction retry bound mismatch.'}
    foreach($p in @('mustRefreshSafetyAfterCustomAdmission','boundedStaleRetryRequired','exhaustedRetryMustRestoreFirmware','productionAdapterRefreshOverloadImplemented','realP13SurfaceUsesRefreshProvider','retryIsNoAdditionalOperatorInteraction','exhaustedRetryRestoresFirmwareImplemented','deterministicRefreshRaceSelfTestImplemented','deterministicExhaustionRestoreSelfTestImplemented')){Assert-True ([bool]$corr.$p) ("P15C stale-Safety correction missing implementation: {0}" -f $p)}
    Assert-False ([bool]$corr.automaticPathChanged) 'P15C Manual correction must not widen scope into Automatic.'
    Assert-False ([bool]$corr.hardwareExecution) 'P15C correction implementation must be software-only.'
    $failedCorrection=@($corr.failedCiHistoryPreserved)
    if($failedCorrection.Count -ne 1 -or
       [int]$failedCorrection[0].runNumber -ne 1124 -or
       [long]$failedCorrection[0].runId -ne 36962046081 -or
       [string]$failedCorrection[0].head -cne '10bf1ca322c18feaf884b44b8c8f38026283aba1' -or
       [string]$failedCorrection[0].result -cne 'FAILURE' -or
       [string]$failedCorrection[0].failureStep -cne 'HP 8C40 P15C real-GUI Manual preparation invariant'){
        throw 'P15C stale-Safety correction failed-CI history mismatch.'
    }
    Assert-False ([bool]$failedCorrection[0].hardwareExecution) 'P15C stale-Safety correction failed CI must record no hardware execution.'
    Assert-False ([bool]$failedCorrection[0].physicalGatesOpened) 'P15C stale-Safety correction failed CI must keep physical gates closed.'
}

if($status -eq 'P15C_GUI_MANUAL_STALE_SAFETY_CORRECTION_CI_PASS_GATE_CLOSED'){
    Assert-False ([bool]$g.executionAuthorized) 'Closed P15C stale-Safety correction must keep parent gate closed.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'Closed P15C stale-Safety correction must keep GUI gate closed.'
    Assert-False ([bool]$g.physicalPassed) 'P15C correction closure cannot claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'P15C correction closure cannot close physical evidence.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'Closed P15C correction requires qualification source gate closed.'
    $corr=$g.staleSafetyRaceCorrection
    Assert-True ([bool]$corr.required) 'P15C stale-Safety correction must remain required.'
    Assert-True ([bool]$corr.implementationComplete) 'P15C stale-Safety correction implementation must remain complete.'
    Assert-True ([bool]$corr.ciValidated) 'P15C stale-Safety correction must record CI validation.'
    Assert-True ([bool]$corr.closure.closed) 'P15C stale-Safety correction closure must be closed.'
    $cc=$corr.closure
    if([string]$cc.result -cne 'PASS' -or
       [string]$cc.implementationHead -cne '550d6372b1e797cc1d7eecf62ee7afb4bb541061' -or
       [int]$cc.sourceCiRunNumber -ne 1125 -or
       [long]$cc.sourceCiRunId -ne 36962248019 -or
       [string]$cc.sourceCiResult -cne 'SUCCESS'){
        throw 'P15C stale-Safety correction closure CI identity mismatch.'
    }
    foreach($p in @('p11ProductionAdapterInvariantValidated','p13SurfaceInvariantValidated','p15cInvariantValidated','p15cEvidencePackagingValidated','powerShell51CompatibilityValidated','warningsAsErrorsBuildValidated','adaptiveProductionSelfTestValidated','freshSafetyRefreshRetrySelfTestValidated','freshSafetyExhaustionRestoreSelfTestValidated')){Assert-True ([bool]$cc.$p) ("P15C stale-Safety correction closure missing validation: {0}" -f $p)}
    Assert-False ([bool]$cc.automaticPathChanged) 'P15C correction closure must not widen into Automatic.'
    Assert-False ([bool]$cc.hardwareExecution) 'P15C correction closure must record no hardware execution.'
    Assert-False ([bool]$cc.physicalGatesOpened) 'P15C correction closure must record physical gates closed.'
    $failedCorrection=@($corr.failedCiHistoryPreserved)
    if($failedCorrection.Count -ne 1 -or [int]$failedCorrection[0].runNumber -ne 1124 -or [long]$failedCorrection[0].runId -ne 36962046081 -or [string]$failedCorrection[0].head -cne '10bf1ca322c18feaf884b44b8c8f38026283aba1' -or [string]$failedCorrection[0].result -cne 'FAILURE'){
        throw 'P15C stale-Safety correction closure failed-CI history mismatch.'
    }
}

if($status -eq 'P15C_GUI_MANUAL_REAUTHORIZED_AFTER_STALE_SAFETY_CORRECTION_AWAITING_SAME_HEAD_CI'){
    Assert-True ([bool]$g.preparationCiValidated) 'P15C reauthorization requires closed base preparation.'
    Assert-True ([bool]$g.preparationClosure.closed) 'P15C reauthorization requires formal base preparation closure.'
    $corr=$g.staleSafetyRaceCorrection
    Assert-True ([bool]$corr.implementationComplete) 'P15C reauthorization requires stale-Safety correction implementation.'
    Assert-True ([bool]$corr.ciValidated) 'P15C reauthorization requires stale-Safety correction CI validation.'
    Assert-True ([bool]$corr.closure.closed) 'P15C reauthorization requires formally closed stale-Safety correction.'
    if([string]$corr.closure.result -cne 'PASS' -or [string]$corr.closure.implementationHead -cne '550d6372b1e797cc1d7eecf62ee7afb4bb541061' -or [int]$corr.closure.sourceCiRunNumber -ne 1125 -or [long]$corr.closure.sourceCiRunId -ne 36962248019 -or [string]$corr.closure.sourceCiResult -cne 'SUCCESS'){throw 'P15C reauthorization correction-closure basis mismatch.'}
    $a=$g.authorization
    if([string]$a.basisHead -cne 'f5921b4ce0d2a3b6fb137e45a424280602c1e9a8' -or [int]$a.basisCiRunNumber -ne 1126 -or [long]$a.basisCiRunId -ne 36962577006 -or [string]$a.basisCiResult -cne 'SUCCESS'){throw 'P15C reauthorization formal-closure basis mismatch.'}
    if([string]$a.previousAuthorizationHead -cne '5eb02ca3b50a6d88dc027b97f2dc76fa5b39490e' -or [int]$a.previousAuthorizationCiRunNumber -ne 1122 -or [string]$a.previousAuthorizationResult -cne 'TARGET_FAIL_CLOSED_NO_PHYSICAL_FAN_WRITE' -or -not [bool]$a.previousAuthorizationRevoked){throw 'P15C reauthorization prior target-attempt history mismatch.'}
    Assert-True ([bool]$a.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P15C reauthorization must require same-HEAD CI success.'
    Assert-True ([bool]$a.staleSafetyCorrectionClosed) 'P15C reauthorization requires closed stale-Safety correction.'
    foreach($p in @('p15aAuthorizationOpened','p15bAuthorizationOpened','userFacingManualGateOpened','automaticAuthorizationOpened','candidateCurveAuthorizationOpened','m9cQualificationConstructionOpened','m9dQualificationConstructionOpened','hardwareExecutionAtAuthorizationCommit')){Assert-False ([bool]$a.$p) ("P15C reauthorization opened forbidden scope: {0}" -f $p)}
    Assert-True ([bool]$g.executionAuthorized) 'P15C reauthorization must open dedicated parent harness gate.'
    Assert-True ([bool]$g.controllerPhysicalExecutionAuthorized) 'P15C reauthorization must open dedicated GUI qualification gate.'
    Assert-False ([bool]$g.physicalPassed) 'P15C reauthorization must not pre-claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'P15C reauthorization must not pre-close physical evidence.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P15C reauthorization requires qualification source gate open.'
}

if($status -eq 'P15C_GUI_MANUAL_PHYSICAL_PASS_FORMALLY_CLOSED'){
    Assert-False ([bool]$g.executionAuthorized) 'P15C physical closure must re-block parent harness.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'P15C physical closure must re-block GUI qualification gate.'
    Assert-True ([bool]$g.physicalPassed) 'P15C physical closure must set physicalPassed.'
    Assert-True ([bool]$g.evidenceClosed) 'P15C physical closure must set evidenceClosed.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15C physical closure requires qualification source gate closed.'
    $p=$g.physicalPassClosure
    Assert-True ([bool]$p.closed) 'P15C physical PASS closure must be closed.'
    if([string]$p.result -cne 'PASS' -or
       [string]$p.sourceHead -cne '5be9615dc79dc97285ba2a790e061e0fabd0c398' -or
       [int]$p.sourceCiRunNumber -ne 1127 -or
       [long]$p.sourceCiRunId -ne 36962891511 -or
       [string]$p.sourceCiResult -cne 'SUCCESS'){
        throw 'P15C physical PASS source/CI identity mismatch.'
    }
    if([string]$p.evidenceZipSha256 -cne '057f2d9d978a9d63b9bde456e485b12e65db5daa6a501d2970e1528f821e29d6' -or
       [string]$p.evidenceSidecarSha256 -cne '5932729cf366213ea84284b8879761cb4286ada67a96e548fdfad00ab157cdd0' -or
       [string]$p.packageManifestSha256 -cne '6e7ef70640ed3b1d9923843696dfbe11c089872d5e285d0a6bb077475f358542'){
        throw 'P15C physical PASS package identity mismatch.'
    }
    foreach($flag in @('sidecarReferencesZipSha256','archiveIntegrityVerified','packageManifestEmbeddedHashesVerified','packageManifestSourceIdentityEntriesVerified','repositoryHeadMatched','upstreamHeadMatchedAtExecution','trackedSourceClean','onlyPreservedUntrackedLogsObserved','targetMatched','realP13SurfaceUsed','parentOwnedProofBoundToGuiIdentity','causalPrepareWriteIntentCommitRestoreReleaseVerified','strongRestoreVerified','ffReleaseAndLegacyDefaultPathVerified','localFirmwareAckVerified','watchdogReleaseVerified','finalJournalAbsent','watchdogProcessIdentityStable','initialServiceStatePreserved','failsafeArmed','evidenceIndependentlyReviewed','physicalPassSupported')){
        Assert-True ([bool]$p.$flag) ("P15C physical PASS expected true: {0}" -f $flag)
    }
    foreach($flag in @('failsafeTakeover','userFacingManualExecutionAuthorized','automaticExecutionAuthorized','candidateCurvePhysicallyValidated','candidateCurveAuthorizedForProduction','m9cQualificationConstructionAuthorized','m9dQualificationConstructionAuthorized','nextPhysicalGateOpened','serviceStartedByHarness','serviceRestartedForCleanup')){
        Assert-False ([bool]$p.$flag) ("P15C physical PASS expected false: {0}" -f $flag)
    }
    if([int]$p.packageManifestEmbeddedEvidenceHashCount -ne 16 -or
       [int]$p.packageManifestSourceIdentityEntryCount -ne 11 -or
       [int]$p.exactManualModeRequests -ne 1 -or
       [int]$p.exactApplyManualCalls -ne 1 -or
       [int]$p.exactFirmwareModeRequests -ne 1 -or
       [int]$p.exactAutomaticModeRequests -ne 0 -or
       [int]$p.healthySafetyReadySamples -ne 3 -or
       [int]$p.parentOwnedSetpointSamples -ne 2 -or
       [int]$p.ownedJournalSchemaVersion -ne 2 -or
       [int]$p.ownedJournalGeneration -ne 3 -or
       [int]$p.independentFinalFfFfSamples -ne 2 -or
       [int]$p.cleanupFfFfSamples -ne 2){
        throw 'P15C physical PASS bounded evidence counts mismatch.'
    }
    if([string]$p.equalLevel -cne '30/30' -or
       [int]$p.ownedJournalControllerPid -ne 4376 -or
       [string]$p.ownedJournalControllerStartUtcTicks -cne '639265111894517136' -or
       [int]$p.watchdogPid -ne 7980 -or
       [string]$p.watchdogStartUtcTicks -cne '639264861577277909' -or
       [string]$p.guiFinalAuthority -cne 'Firmware' -or
       [string]$p.guiFinalMode -cne 'Firmware' -or
       [string]$p.initialServiceState -cne 'Manual/Running/PID7980/LocalSystem' -or
       [string]$p.finalServiceState -cne 'Manual/Running/PID7980/LocalSystem'){
        throw 'P15C physical PASS identity/state evidence mismatch.'
    }
    $c=$p.causalEventCounts
    if([int]$c.prepare -ne 1 -or [int]$c.writeIntent -ne 1 -or [int]$c.commit -ne 1 -or [int]$c.restoreBegin -ne 1 -or [int]$c.release -ne 1){
        throw 'P15C physical PASS causal event counts mismatch.'
    }
}

Assert-False ([bool]$contract.safetyBoundary.controlEnabledByDefault) 'P15C must keep default control OFF.'
Assert-False ([bool]$contract.safetyBoundary.automaticPolicyEnabled) 'P15C must keep automatic policy OFF.'
Assert-False ([bool]$contract.safetyBoundary.manualExecutionAuthorized) 'P15C must not open the normal user Manual gate.'
Assert-False ([bool]$contract.safetyBoundary.automaticExecutionAuthorized) 'P15C must keep user Automatic closed.'
Assert-False ([bool]$contract.safetyBoundary.candidateCurvePhysicallyValidated) 'P15C must not validate Candidate V1.'
Assert-False ([bool]$contract.safetyBoundary.candidateCurveAuthorizedForProduction) 'P15C must not authorize Candidate V1.'
Assert-False ([bool]$contract.safetyBoundary.m9cQualificationConstructionAuthorized) 'P15C must not reopen M9C.'
Assert-False ([bool]$contract.safetyBoundary.m9dQualificationConstructionAuthorized) 'P15C must not reopen M9D.'
Assert-False ([bool]$contract.automatic.executionAuthorized) 'P15C must keep Automatic execution closed.'
Assert-False ([bool]$profile.control.enabledByDefault) 'P15C must keep profile default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'P15C must keep profile automatic policy OFF.'
Assert-Contains $userGate 'ManualExecutionAuthorized = false' 'P15C must leave normal user Manual compile gate false.'
Assert-Contains $userGate 'AutomaticExecutionAuthorized = false' 'P15C must leave user Automatic compile gate false.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'P15C must leave Candidate V1 physically unvalidated.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'P15C must leave Candidate V1 production-unauthorized.'

foreach($needle in @(
 'P13ControlInteractionObservation',
 'P13ControlInteractionKind',
 'Hp8C40P15CGuiManualQualificationGate',
 'RequiredToken = "8C40-P15C-GUI-MANUAL30"',
 'QualificationLevel = 30',
 'RequiredHealthyPreWriteSamples = 3',
 'NormalUserExecutionGatesClosed()',
 'MaximumCpuPhysicalC = 90.0',
 'MaximumGpuPhysicalC = 82.0',
 'p15c-parent-owned-verified.txt'
)){Assert-Contains $qualification $needle ("P15C qualification source missing: {0}" -f $needle)}

foreach($needle in @(
 '--8c40-p15c-gui-manual-test',
 '--8c40-p15c-test-token',
 '--8c40-p15c-marker-root',
 'Hp8C40P15CGuiManualQualificationGate.PhysicalExecutionAuthorized',
 'Hp8C40P15CGuiManualQualificationGate.NormalUserExecutionGatesClosed()',
 'Hp8C40TargetProfile.Matches'
)){Assert-Contains $program $needle ("P15C Program boundary missing: {0}" -f $needle)}

foreach($needle in @(
 'ManualFreshSafetyMaximumAttempts = 4',
 'Func<SafetyGateResult?> refreshSafetyProvider',
 'post-admission SafetyGate refresh',
 'Manual command could not obtain a current SafetyGate evaluation after'
)){Assert-Contains $adaptive $needle ("P15C stale-Safety production-adapter correction missing: {0}" -f $needle)}
foreach($needle in @(
 '_controlSafetyProvider,',
 'await _controller.ApplyManualAsync('
)){Assert-Contains $surface $needle ("P15C real-P13 fresh-Safety wiring missing: {0}" -f $needle)}

Assert-NotContains $main 'ApplyManualAsync(' 'P15C MainForm must not bypass the real P13 Manual Apply handler.'
Assert-NotContains $main 'ProcessAutomaticAsync(' 'P15C MainForm must not execute Automatic policy.'

foreach($needle in @(
 '_p15cGuiManualHardwareTest',
 'Hp8C40P15CGuiManualQualificationGate.PhysicalExecutionAuthorized',
 'Hp8C40P15CGuiManualQualificationGate.NormalUserExecutionGatesClosed()',
 'OnP15CControlInteraction',
 'IsP15CControlInteractionAuthorized',
 'HasP15CParentOwnedProofForCurrentProcess',
 'fixedManualQualificationLevel:',
 'TryPublishP15CGuiReadyAsync',
 'p15cManualExecutionAuthorized',
 'manualInteractionReadyProvider',
 'interactionAuthorizationProvider',
 'fixedManualQualificationLevel',
 'interactionObserver',
 'LastRestoreEvidence',
 'ParentOwnedVerifiedFileName'
)){Assert-Contains $main $needle ("P15C MainForm integration missing: {0}" -f $needle)}

foreach($needle in @(
 'manualInteractionReadyProvider',
 'interactionAuthorizationProvider',
 'fixedManualQualificationLevel',
 'TryBeginControlInteraction(',
 'qualification pre-action fence rejected the interaction',
 'interactionObserver',
 'P13ControlInteractionKind.ModeRequest',
 'P13ControlInteractionKind.ManualApply',
 'await _controller.SetModeAsync(',
 'await _controller.ApplyManualAsync('
)){Assert-Contains $surface $needle ("P15C real P13 surface observation missing: {0}" -f $needle)}
foreach($forbidden in @('FanCommand(','Hp8C40FanControlBackend','SetFanLevel(','NamedPipeFanControlWatchdogLeaseClient')){
 Assert-NotContains $surface $forbidden ("P15C must not add a hardware bypass to P13 surface: {0}" -f $forbidden)
}

$applyStart=$surface.IndexOf('private async Task ApplyManualAsync()',[StringComparison]::Ordinal)
$requestStart=$surface.IndexOf('private async Task RequestModeAsync(',[StringComparison]::Ordinal)
$refreshStart=$surface.IndexOf('private void RefreshState(',[StringComparison]::Ordinal)
if($applyStart -lt 0 -or $requestStart -le $applyStart -or $refreshStart -le $requestStart){throw 'P15C could not isolate P13 control methods for pre-action ordering checks.'}
$applyMethod=$surface.Substring($applyStart,$requestStart-$applyStart)
$requestMethod=$surface.Substring($requestStart,$refreshStart-$requestStart)
$applyFence=$applyMethod.IndexOf('TryBeginControlInteraction(',[StringComparison]::Ordinal)
$applyController=$applyMethod.IndexOf('await _controller.ApplyManualAsync(',[StringComparison]::Ordinal)
$requestFence=$requestMethod.IndexOf('TryBeginControlInteraction(',[StringComparison]::Ordinal)
$requestController=$requestMethod.IndexOf('await _controller.SetModeAsync(',[StringComparison]::Ordinal)
if($applyFence -lt 0 -or $applyController -lt 0 -or $applyFence -gt $applyController){throw 'P15C Manual Apply pre-action fence must execute before the production adapter call.'}
if($requestFence -lt 0 -or $requestController -lt 0 -or $requestFence -gt $requestController){throw 'P15C mode-request pre-action fence must execute before the production adapter call.'}

foreach($needle in @(
 'HARD VERSIONED AUTHORIZATION BARRIER',
 'guiManual.executionAuthorized',
 'controllerPhysicalExecutionAuthorized',
 'Assert-RepositoryProvenance',
 'Assert-ExactTarget',
 'Assert-NoConflictingController',
 'Assert-StableSetpoint 255 255',
 '--8c40-p15c-gui-manual-test',
 'p15c-gui-manual-applied.json',
 'Assert-StableSetpoint 30 30',
 'Assert-OwnedJournal',
 'P15C-PARENT-OWNED-VERIFIED',
 'Wait-P15BTrackedChildExitCode',
 'Assert-CausalServiceLog',
 'Assert-StableSetpoint 255 255',
 'package-p15c-evidence.ps1',
 'FAIL_CLOSED'
)){Assert-Contains $harness $needle ("P15C parent harness invariant missing: {0}" -f $needle)}
foreach($forbidden in @('SetFanLevel(','--restore-hp-auto','Start-Service -Name $serviceName','Stop-Service -Name $serviceName','Set-Service','New-Service','git clean')){
 Assert-NotContains $harness $forbidden ("P15C parent harness contains forbidden direct operation: {0}" -f $forbidden)
}

Assert-Contains $failsafe "TargetProfileId -cne 'HP-8C40-9D0R1LA-F18'" 'P15C failsafe must bind exact target.'
Assert-Contains $failsafe '[int]$Journal.Owned.Cpu -eq 30' 'P15C failsafe must bind exact 30/30 ownership.'
Assert-Contains $failsafe 'P15C FAILSAFE ARMED:' 'P15C failsafe ARMED marker missing.'
foreach($needle in @('Program.cs','Hp8C40PostM9UserControlGate.cs','AdaptiveFanProductionController.cs','watchdog-p15c-service-failsafe-8c40.ps1')){Assert-Contains $packager $needle ("P15C packager missing critical source identity: {0}" -f $needle)}
Assert-Contains $packager 'sourceEvidencePreserved=$true' 'P15C packager preservation marker missing.'
Assert-Contains $packager 'gitCleanUsed=$false' 'P15C packager must record no git clean.'
Assert-NotContains $packager 'git clean' 'P15C packager must never invoke git clean.'
Assert-Contains $doc 'P15C — real GUI Manual qualification' 'P15C documentation section missing.'

Write-Host 'HP 8C40 P15C real-GUI Manual preparation invariant: PASS' -ForegroundColor Green
