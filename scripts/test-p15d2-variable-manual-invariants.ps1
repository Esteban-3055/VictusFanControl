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
    'P15D2_VARIABLE_MANUAL_IMPLEMENTATION_CI_PENDING_GATE_CLOSED',
    'P15D2_VARIABLE_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED',
    'P15D2_VARIABLE_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI',
    'P15D2_VARIABLE_MANUAL_EC_TRANSIENT_FAIL_CLOSED_GATE_CLOSED',
    'P15D2_VARIABLE_MANUAL_REAUTHORIZED_AFTER_EC_TRANSIENT_AWAITING_SAME_HEAD_CI',
    'P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PENDING_GATE_CLOSED',
    'P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PASS_GATE_CLOSED'
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
$isInitialAuthorized=($status -eq 'P15D2_VARIABLE_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI')
$isRetryAuthorized=($status -eq 'P15D2_VARIABLE_MANUAL_REAUTHORIZED_AFTER_EC_TRANSIENT_AWAITING_SAME_HEAD_CI')
$isAuthorized=($isInitialAuthorized -or $isRetryAuthorized)
$isFailedClosed=($status -eq 'P15D2_VARIABLE_MANUAL_EC_TRANSIENT_FAIL_CLOSED_GATE_CLOSED')
$isRetryBarrierCorrection=($status -in @('P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PENDING_GATE_CLOSED','P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PASS_GATE_CLOSED'))
$isRetryBarrierCorrectionClosed=($status -eq 'P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PASS_GATE_CLOSED')
$isFormalClosure=($status -in @('P15D2_VARIABLE_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED','P15D2_VARIABLE_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI','P15D2_VARIABLE_MANUAL_EC_TRANSIENT_FAIL_CLOSED_GATE_CLOSED','P15D2_VARIABLE_MANUAL_REAUTHORIZED_AFTER_EC_TRANSIENT_AWAITING_SAME_HEAD_CI','P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PENDING_GATE_CLOSED','P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PASS_GATE_CLOSED'))
if($isFormalClosure){
    Assert-True ([bool]$d.preparationClosure.closed) 'P15D2 formal software closure must close preparation evidence.'
}else{
    Assert-False ([bool]$d.preparationClosure.closed) 'P15D2 pending implementation stage cannot pre-close software evidence.'
}
if($isAuthorized){
    Assert-True ([bool]$d.executionAuthorized) 'P15D2 authorization must open the dedicated parent harness gate.'
    Assert-True ([bool]$d.controllerPhysicalExecutionAuthorized) 'P15D2 authorization must open the dedicated GUI/controller gate.'
    Assert-True ([bool]$d.physicalGatesOpened) 'P15D2 authorization contract must record only the dedicated qualification gates open.'
}else{
    Assert-False ([bool]$d.executionAuthorized) 'P15D2 parent harness physical gate must remain closed.'
    Assert-False ([bool]$d.controllerPhysicalExecutionAuthorized) 'P15D2 GUI physical gate must remain closed.'
    Assert-False ([bool]$d.physicalGatesOpened) 'P15D2 software preparation must keep physical gates closed.'
}
if($isRetryBarrierCorrection){
    $c=$d.retryStatusBarrierCorrection
    Assert-True ([bool]$c.required) 'P15D2 retry status-barrier correction must be required.'
    Assert-True ([bool]$c.discoveredBeforeRetryHardwareExecution) 'P15D2 retry status-barrier defect must be recorded as pre-hardware.'
    Assert-True ([bool]$c.correctionImplemented) 'P15D2 retry status-barrier correction must be implemented.'
    if($isRetryBarrierCorrectionClosed){
        Assert-True ([bool]$c.correctionCiValidated) 'P15D2 closed retry barrier correction must record validated CI.'
        Assert-True ([bool]$c.formalClosure) 'P15D2 closed retry barrier correction must record formal closure.'
        if([string]$c.implementationHead -cne '886b1761058128c784644b8028901217c242f334' -or
           [int]$c.implementationCiRunNumber -ne 1177 -or
           [long]$c.implementationCiRunId -ne 37003333223 -or
           [string]$c.implementationCiResult -cne 'SUCCESS'){
            throw 'P15D2 retry status-barrier correction implementation identity mismatch.'
        }
    }else{
        Assert-False ([bool]$c.correctionCiValidated) 'P15D2 correction-pending state cannot pre-claim correction CI.'
        Assert-False ([bool]$c.formalClosure) 'P15D2 correction-pending state cannot pre-claim formal closure.'
    }
    Assert-False ([bool]$c.hardwareExecution) 'P15D2 retry status-barrier correction must remain software-only.'
    Assert-False ([bool]$c.physicalGatesOpened) 'P15D2 retry status-barrier correction must keep physical gates closed.'
    if([string]$c.reauthorizationHead -cne 'dc48ae17015a3bf9abf1f3c44c363a9add6d2154' -or
       [int]$c.reauthorizationCiRunNumber -ne 1176 -or
       [long]$c.reauthorizationCiRunId -ne 36987541628 -or
       [string]$c.reauthorizationCiResult -cne 'SUCCESS'){
        throw 'P15D2 retry status-barrier correction basis mismatch.'
    }
}

Assert-False ([bool]$d.physicalPassed) 'P15D2 cannot pre-claim physical PASS.'
Assert-False ([bool]$d.evidenceClosed) 'P15D2 cannot pre-close physical evidence.'
Assert-False ([bool]$d.hardwareExecution) 'P15D2 authorization commit must record no hardware execution.'

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
    $generationReview=$d.generationSemanticsReview
    Assert-True ([bool]$generationReview.required) 'P15D2 generation semantics review must remain recorded.'
    Assert-True ([bool]$generationReview.implementationComplete) 'P15D2 generation semantics correction must remain implemented.'
    Assert-True ([bool]$generationReview.ciValidated) 'P15D2 generation semantics correction requires validated target-branch CI.'
    Assert-False ([bool]$generationReview.hardwareExecution) 'P15D2 generation semantics correction must remain software-only.'
    Assert-False ([bool]$generationReview.physicalGatesOpened) 'P15D2 generation semantics correction must keep physical gates closed.'
    if([string]$generationReview.validationHead -cne '6899e93ceb07fc62694e48135c99e8b9a5e05517' -or
       [int]$generationReview.validationCiRunNumber -ne 1171 -or
       [long]$generationReview.validationCiRunId -ne 36977009716 -or
       [string]$generationReview.validationCiResult -cne 'SUCCESS'){
        throw 'P15D2 generation semantics target-CI identity mismatch.'
    }
    Assert-Contains ([string]$generationReview.correction) '3 -> 5 -> 5 -> 7' 'P15D2 generation semantics correction must preserve 3 -> 5 -> 5 -> 7.'

    $runtimeSource=$d.runtimeSourceReview
    if([string]$runtimeSource.sourceBranch -cne 'feature/p15d2-variable-manual-runtime' -or
       [string]$runtimeSource.sourceHead -cne 'b5353e789cef099f77ec7c463a1441f7e13ef41e' -or
       [int]$runtimeSource.sourceCiRunNumber -ne 1170 -or
       [long]$runtimeSource.sourceCiRunId -ne 36974640030 -or
       [string]$runtimeSource.sourceCiResult -cne 'SUCCESS'){
        throw 'P15D2 reviewed runtime-source identity mismatch.'
    }
    Assert-True ([bool]$runtimeSource.sourceCodeReviewedBeforeIntegration) 'P15D2 runtime source must be reviewed before target integration.'
    Assert-True ([bool]$runtimeSource.sameHeadTargetCiStillRequired) 'P15D2 integrated runtime must still require target same-head CI.'
    Assert-False ([bool]$runtimeSource.hardwareExecution) 'P15D2 runtime source review must be software-only.'
    Assert-False ([bool]$runtimeSource.physicalGatesOpened) 'P15D2 runtime source review must keep physical gates closed.'
    Assert-True ([bool]$d.runtimeImplementationComplete) 'P15D2 implementation/closure state requires complete runtime/harness implementation.'
    if($isFormalClosure){
        Assert-True ([bool]$d.runtimeImplementationCiValidated) 'P15D2 formal closure requires same-head target runtime CI validation.'
    }else{
        Assert-False ([bool]$d.runtimeImplementationCiValidated) 'P15D2 implementation-pending state cannot pre-claim runtime CI.'
    }
    $ri=$d.runtimeImplementation
    foreach($flag in @(
        'programBoundaryImplemented','exactTargetBoundaryImplemented','realP13VariableManualPathReused',
        'readyRequiresThreeHealthySafetySamples','initial30ParentFenceImplemented','changed40ParentFenceImplemented',
        'duplicate40HoldCustomFenceImplemented','duplicate40NoRetransmitParentAuditImplemented','return30ParentFenceImplemented',
        'realFirmwareStrongRestoreImplemented','sameOwnedSessionAuditImplemented','causalThreeWriteWatchdogAuditImplemented',
        'nativeTrackedGuiProcess','independentFailsafeImplemented','evidencePackagerImplemented','evidencePackagingSelfTestImplemented'
    )){Assert-True ([bool]$ri.$flag) ("P15D2 runtime implementation flag missing: {0}" -f $flag)}
    Assert-False ([bool]$ri.physicalExecution) 'P15D2 runtime implementation must remain software-only.'
    $hardening=$d.hardeningReview
    Assert-True ([bool]$hardening.required) 'P15D2 pre-authorization hardening review must remain required.'
    Assert-True ([bool]$hardening.implementationComplete) 'P15D2 hardening fixes must be implemented before CI closure.'
    if($isFormalClosure){
        Assert-True ([bool]$hardening.ciValidated) 'P15D2 formal closure requires hardening CI validation.'
        if([string]$hardening.validationHead -cne 'e28dff8540d6508c074eff5f189ce134e488c6bb' -or
           [int]$hardening.validationCiRunNumber -ne 1172 -or
           [long]$hardening.validationCiRunId -ne 36977648743 -or
           [string]$hardening.validationCiResult -cne 'SUCCESS'){
            throw 'P15D2 hardening target-CI identity mismatch.'
        }
    }else{
        Assert-False ([bool]$hardening.ciValidated) 'P15D2 implementation-pending state cannot pre-claim hardening CI.'
    }
    Assert-False ([bool]$hardening.hardwareExecution) 'P15D2 hardening must remain software-only.'
    Assert-False ([bool]$hardening.physicalGatesOpened) 'P15D2 hardening must keep physical gates closed.'
    $findings=@($hardening.findings)
    $fixes=@($hardening.fixes)
    if($findings.Count -ne 2 -or $fixes.Count -ne 2){throw 'P15D2 hardening review must preserve exactly the two pre-authorization findings and fixes.'}
    Assert-Contains ([string]$findings[0]) '30/30-only lease classifier' 'P15D2 hardening must preserve the variable-level failsafe finding.'
    Assert-Contains ([string]$findings[1]) 'every WRITE_INTENT and every COMMIT' 'P15D2 hardening must preserve the watchdog-generation finding.'
    Assert-Contains ([string]$fixes[0]) '30/30 or 40/40' 'P15D2 hardening must cover both qualified failsafe levels.'
    Assert-Contains ([string]$fixes[1]) '3 -> 5 -> 5 -> 7' 'P15D2 hardening must require exact generation progression.'
    $failed=@($d.failedCiHistoryPreserved)
    if($failed.Count -lt 1 -or $failed.Count -gt 2 -or
       [int]$failed[0].runNumber -ne 1148 -or
       [long]$failed[0].runId -ne 36973216494 -or
       [string]$failed[0].head -cne '0e39d6c0583a90b135f2bcb1b903b01a822ed3b5' -or
       [string]$failed[0].result -cne 'FAILURE' -or
       [string]$failed[0].failureStep -cne 'Build'){
        throw 'P15D2 failed-CI history mismatch.'
    }
    if($failed.Count -eq 2){
        if([int]$failed[1].runNumber -ne 1155 -or
           [long]$failed[1].runId -ne 36973835321 -or
           [string]$failed[1].head -cne '7f5cb1d2efa65c1843fffebd5f2c98d76178dbfe' -or
           [string]$failed[1].result -cne 'FAILURE' -or
           [string]$failed[1].failureStep -cne 'HP 8C40 P15D2 variable Manual preparation invariant'){
            throw 'P15D2 transition failed-CI history mismatch.'
        }
    }
    foreach($entry in $failed){
        Assert-False ([bool]$entry.hardwareExecution) 'P15D2 failed CI must record no hardware execution.'
        Assert-False ([bool]$entry.physicalGatesOpened) 'P15D2 failed CI must keep physical gates closed.'
    }

    if($isFormalClosure){
        $pc=$d.preparationClosure
        if([string]$pc.result -cne 'PASS' -or
           [string]$pc.implementationHead -cne 'e28dff8540d6508c074eff5f189ce134e488c6bb' -or
           [int]$pc.sourceCiRunNumber -ne 1172 -or
           [long]$pc.sourceCiRunId -ne 36977648743 -or
           [string]$pc.sourceCiResult -cne 'SUCCESS'){
            throw 'P15D2 formal preparation closure CI identity mismatch.'
        }
        foreach($flag in @(
            'powerShellSyntaxValidated','p15d1ClosureInvariantValidated','p15d2InvariantValidated',
            'evidencePackagingSelfTestValidated','powerShell51CompatibilityValidated',
            'warningsAsErrorsBuildValidated','runtimeWiringBuildValidated',
            'generationSemanticsValidated','hardeningValidated'
        )){Assert-True ([bool]$pc.$flag) ("P15D2 formal closure missing validation: {0}" -f $flag)}
        Assert-False ([bool]$pc.hardwareExecution) 'P15D2 formal closure must record no hardware execution.'
        Assert-False ([bool]$pc.physicalGatesOpened) 'P15D2 formal closure must keep physical gates closed.'
    }
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
$generations=@($d.design.expectedOwnedGenerations)
if($generations.Count -ne 4 -or [long]$generations[0] -ne 3 -or [long]$generations[1] -ne 5 -or [long]$generations[2] -ne 5 -or [long]$generations[3] -ne 7){
    throw 'P15D2 watchdog generation progression must be exactly 3/5/5/7.'
}
Assert-Contains ([string]$d.design.generationSemantics) 'every WRITE_INTENT and COMMIT increments generation' 'P15D2 generation semantics must be explicit.'
Assert-True ([bool]$d.requiresDuplicate40NoRetransmit) 'P15D2 must physically prove duplicate 40/40 no-retransmit.'
Assert-True ([bool]$d.requiresStrongRestore) 'P15D2 must end in production strong restore.'
Assert-False ([bool]$d.candidateCurveMayBePromoted) 'P15D2 must not promote Candidate V1.'
Assert-False ([bool]$d.automaticMayOpen) 'P15D2 must not open Automatic.'
Assert-False ([bool]$d.userFacingManualMayOpen) 'P15D2 must not open normal user Manual.'
Assert-False ([bool]$d.m9cOrM9dQualificationMayReopen) 'P15D2 must not reopen M9C/M9D.'

if($isAuthorized){
    Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P15D2 authorization requires the dedicated source gate open.'
    if($isInitialAuthorized){
        $a=$d.authorization
        Assert-True ([bool]$a.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P15D2 authorization must require same-head CI.'
        if([string]$a.preparationClosureHead -cne '71ec64077b545f19d31f0ecde59169083d1f55ab' -or
           [int]$a.preparationClosureCiRunNumber -ne 1173 -or
           [long]$a.preparationClosureCiRunId -ne 36981777611 -or
           [string]$a.preparationClosureCiResult -cne 'SUCCESS' -or
           [string]$a.implementationHead -cne 'e28dff8540d6508c074eff5f189ce134e488c6bb' -or
           [int]$a.implementationCiRunNumber -ne 1172 -or
           [long]$a.implementationCiRunId -ne 36977648743 -or
           [string]$a.implementationCiResult -cne 'SUCCESS'){
            throw 'P15D2 authorization basis identity mismatch.'
        }
        if([string]$a.sourceGateScope -cne 'P15D2 variable-Manual qualification only'){
            throw 'P15D2 authorization scope mismatch.'
        }
        foreach($flag in @(
            'p15aAuthorizationOpened','p15bAuthorizationOpened','p15cAuthorizationOpened','p15d1AuthorizationOpened',
            'userFacingManualGateOpened','automaticAuthorizationOpened','candidateCurveAuthorizationOpened',
            'm9cQualificationConstructionOpened','m9dQualificationConstructionOpened','hardwareExecutionAtAuthorizationCommit'
        )){Assert-False ([bool]$a.$flag) ("P15D2 authorization widened forbidden scope: {0}" -f $flag)}
    }else{
        $a=$d.reauthorizationAfterEcTransient
        Assert-True ([bool]$a.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P15D2 retry authorization must require same-head CI.'
        if([string]$a.failClosedClosureHead -cne 'ca367dad3be7bb6fbe1a7b9f67e4984548edfff5' -or
           [int]$a.failClosedClosureCiRunNumber -ne 1175 -or
           [long]$a.failClosedClosureCiRunId -ne 36986494813 -or
           [string]$a.failClosedClosureCiResult -cne 'SUCCESS' -or
           [string]$a.priorAuthorizationHead -cne 'aa5be229d7e1fb4c1c0b260f3354db972e98436b' -or
           [int]$a.priorAuthorizationCiRunNumber -ne 1174 -or
           [long]$a.priorAuthorizationCiRunId -ne 36985472454 -or
           [string]$a.priorAuthorizationCiResult -cne 'SUCCESS' -or
           [string]$a.priorAttemptResult -cne 'FAIL_CLOSED'){
            throw 'P15D2 retry authorization basis identity mismatch.'
        }
        if([string]$a.sourceGateScope -cne 'P15D2 variable-Manual qualification retry only'){
            throw 'P15D2 retry authorization scope mismatch.'
        }
        foreach($flag in @(
            'p15aAuthorizationOpened','p15bAuthorizationOpened','p15cAuthorizationOpened','p15d1AuthorizationOpened',
            'userFacingManualGateOpened','automaticAuthorizationOpened','candidateCurveAuthorizationOpened',
            'm9cQualificationConstructionOpened','m9dQualificationConstructionOpened','hardwareExecutionAtAuthorizationCommit'
        )){Assert-False ([bool]$a.$flag) ("P15D2 retry authorization widened forbidden scope: {0}" -f $flag)}
    }
}else{
    Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15D2 source gate must be hard-closed.'
}
if($isFailedClosed){
    $fp=$d.failedPhysicalAttempt
    if([string]$fp.sourceHead -cne 'aa5be229d7e1fb4c1c0b260f3354db972e98436b' -or
       [int]$fp.sourceCiRunNumber -ne 1174 -or
       [long]$fp.sourceCiRunId -ne 36985472454 -or
       [string]$fp.sourceCiResult -cne 'SUCCESS' -or
       [string]$fp.result -cne 'FAIL_CLOSED'){
        throw 'P15D2 failed physical attempt source identity mismatch.'
    }
    if([string]$fp.evidenceZipSha256 -cne '66951d9887dc663ee3109c2106ae7c88b322462218ebd16c2c4b45659b3bed02' -or
       [string]$fp.sidecarFileSha256 -cne 'd646dcf0fccf6174c341bb91ac5580988d3d7203ee3d013c822d717c7f88c132' -or
       [string]$fp.packageManifestSha256 -cne 'b38e81945f1ee1d8d32152d452ab9a064ac725ade300f32b48d4866bcc5bc88d' -or
       [string]$fp.harnessSummarySha256 -cne '263533c7e136724e96219286df75f6bff246a44e5183141d004afc9a59644d4f'){
        throw 'P15D2 failed physical evidence hash mismatch.'
    }
    if([string]$fp.guiStartUtcTicks -cne '639265275494889139' -or
       [string]$fp.watchdogStartUtcTicks -cne '639264861577277909' -or
       [string]$fp.ownedSessionId -cne 'c0837a7f-6a54-40df-9348-24b946fbabf2'){
        throw 'P15D2 failed physical identity evidence mismatch.'
    }
    if([int]$fp.initial30Generation -ne 3 -or [int]$fp.changed40Generation -ne 5 -or
       [int]$fp.duplicate40Generation -ne 5 -or [int]$fp.return30Generation -ne 7 -or
       -not [bool]$fp.duplicateNoRetransmitPass -or
       [int]$fp.manualModeRequestsObserved -ne 1 -or [int]$fp.manualApplyRequestsObserved -ne 4 -or
       [int]$fp.firmwareModeRequestsObserved -ne 1 -or [int]$fp.automaticModeRequestsObserved -ne 0){
        throw 'P15D2 failed physical variable-level progress evidence mismatch.'
    }
    Assert-False ([bool]$fp.firmwareStrongRestoreQualified) 'P15D2 failed attempt cannot qualify final Firmware strong restore.'
    Assert-False ([bool]$fp.cleanGuiExitQualified) 'P15D2 failed attempt cannot qualify clean GUI exit.'
    Assert-False ([bool]$fp.causalChainQualified) 'P15D2 failed attempt cannot qualify final causal chain.'
    Assert-True ([bool]$fp.cleanupFirmwareProofPass) 'P15D2 failed attempt must preserve successful cleanup FF/FF proof.'
    Assert-False ([bool]$fp.failsafeTakeover) 'P15D2 failed attempt must record no independent failsafe takeover.'
    Assert-True ([bool]$fp.hardwareSafetyHandoffSucceeded) 'P15D2 failed attempt must record successful production safety handoff.'
    Assert-True ([bool]$fp.retryRequiresFreshAuthorization) 'P15D2 failed attempt must require fresh authorization before retry.'
}
Assert-Contains $gate 'RequiredToken = "8C40-P15D2-MANUAL30-40-40-30"' 'P15D2 source token mismatch.'
Assert-Contains $gate 'InitialLevel = 30' 'P15D2 initial level mismatch.'
Assert-Contains $gate 'ChangedLevel = 40' 'P15D2 changed level mismatch.'
Assert-Contains $gate 'ReturnLevel = 30' 'P15D2 return level mismatch.'
Assert-Contains $gate 'InitialOwnedGeneration = 3' 'P15D2 initial OWNED generation constant mismatch.'
Assert-Contains $gate 'ChangedOwnedGeneration = 5' 'P15D2 changed OWNED generation constant mismatch.'
Assert-Contains $gate 'DuplicateHoldOwnedGeneration = 5' 'P15D2 duplicate HoldCustom generation constant mismatch.'
Assert-Contains $gate 'ReturnOwnedGeneration = 7' 'P15D2 return OWNED generation constant mismatch.'
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

if($status -in @('P15D2_VARIABLE_MANUAL_IMPLEMENTATION_CI_PENDING_GATE_CLOSED','P15D2_VARIABLE_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED','P15D2_VARIABLE_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI','P15D2_VARIABLE_MANUAL_REAUTHORIZED_AFTER_EC_TRANSIENT_AWAITING_SAME_HEAD_CI','P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PENDING_GATE_CLOSED','P15D2_VARIABLE_MANUAL_RETRY_STATUS_BARRIER_CORRECTION_CI_PASS_GATE_CLOSED')){
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
        'HARD VERSIONED AUTHORIZATION BARRIER','P15D2_VARIABLE_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI','P15D2_VARIABLE_MANUAL_REAUTHORIZED_AFTER_EC_TRANSIENT_AWAITING_SAME_HEAD_CI',
        'Assert-StableSetpoint 30 30','Assert-StableSetpoint 40 40','Assert-OwnedJournal',
        '$ExpectedGeneration','30 3 $null','40 5 $ownedSessionId','30 7 $ownedSessionId',
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

    foreach($needle in @(
        'P15D2 FAILSAFE ARMED:','P15D2 FAILSAFE TAKEOVER:','P15D2 FAILSAFE CONTROLLER-KILL:','P15D2 FAILSAFE RECOVERED:',
        'Test-P15D2QualifiedLevelPair','cpu -in @(30,40)','WRITE_ARMED','OWNED','RESTORING'
    )){
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
