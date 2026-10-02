$ErrorActionPreference='Stop'
function Assert-True([bool]$v,[string]$m){if(-not $v){throw $m}}
function Assert-False([bool]$v,[string]$m){if($v){throw $m}}
function Assert-Contains([string]$t,[string]$n,[string]$m){if($t.IndexOf($n,[StringComparison]::Ordinal)-lt 0){throw $m}}
function Assert-NotContains([string]$t,[string]$n,[string]$m){if($t.IndexOf($n,[StringComparison]::Ordinal)-ge 0){throw $m}}
$root=Split-Path -Parent $PSScriptRoot
$contract=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json
$profile=Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$controller=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40P15BManual30QualificationTest.cs') -Raw
$adaptive=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionController.cs') -Raw
$backend=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs') -Raw
$bios=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40BiosFanControl.cs') -Raw
$prodGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$userGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$harness=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15b-manual30.ps1') -Raw
$packager=Get-Content -LiteralPath (Join-Path $root 'scripts\package-p15b-evidence.ps1') -Raw
$failsafe=Get-Content -LiteralPath (Join-Path $root 'scripts\watchdog-p15b-service-failsafe-8c40.ps1') -Raw
$cli=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Cli\CliOptions.cs') -Raw
$program=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Program.cs') -Raw
$baselineHelper=Get-Content -LiteralPath (Join-Path $root 'scripts\p15b-service-baseline.ps1') -Raw
$baselineSelfTest=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15b-service-baseline-selftest.ps1') -Raw

$status=[string]$contract.status
$postP15BState=$status.StartsWith('P15C_',[StringComparison]::Ordinal) -or
    $status.StartsWith('P15D',[StringComparison]::Ordinal)
if($status -notin @('P15B_MANUAL30_PREPARATION_CI_PENDING_GATE_CLOSED','P15B_MANUAL30_PREPARATION_CI_PASS_GATE_CLOSED','P15B_MANUAL30_AUTHORIZED_AWAITING_SAME_HEAD_CI','P15B_MANUAL30_INHERITED_RUNNING_SERVICE_PREFLIGHT_FAIL_CLOSED_GATE_CLOSED','P15B_MANUAL30_SERVICE_BASELINE_CORRECTION_CI_PENDING_GATE_CLOSED','P15B_MANUAL30_SERVICE_BASELINE_CORRECTION_CI_PASS_GATE_CLOSED','P15B_MANUAL30_POWERSHELL_PID_COLLISION_PREFLIGHT_FAIL_CLOSED_GATE_CLOSED','P15B_MANUAL30_POWERSHELL_PID_COLLISION_CORRECTION_CI_PENDING_GATE_CLOSED','P15B_MANUAL30_POWERSHELL_PID_COLLISION_CORRECTION_CI_PASS_GATE_CLOSED','P15B_MANUAL30_PHYSICAL_PASS_FORMALLY_CLOSED') -and -not $postP15BState){throw 'P15B preparation/authorization/correction state mismatch.'}
Assert-True ([bool]$contract.startupNoWrite.physicalPassed) 'P15B requires P15A physical PASS.'
Assert-True ([bool]$contract.startupNoWrite.evidenceClosed) 'P15B requires P15A evidence closed.'
Assert-False ([bool]$contract.startupNoWrite.executionAuthorized) 'P15A must remain re-blocked.'
if([string]$contract.startupNoWrite.physicalPassClosure.evidenceZipSha256 -cne 'cd8e7dfe2fef365e75b886ccca1c6c101ce12030754f21a8f9bc7f305f76c24e'){throw 'P15B P15A evidence prerequisite mismatch.'}
Assert-True ([bool]$contract.manual30.preparationImplemented) 'P15B preparation must be implemented.'
if($status -eq 'P15B_MANUAL30_PREPARATION_CI_PENDING_GATE_CLOSED'){
    Assert-False ([bool]$contract.manual30.preparationCiValidated) 'Pending P15B preparation must not pre-claim CI validation.'
    Assert-False ([bool]$contract.manual30.preparationClosure.closed) 'Pending P15B preparation must not pre-close.'
}
if($status -in @('P15B_MANUAL30_PREPARATION_CI_PASS_GATE_CLOSED','P15B_MANUAL30_AUTHORIZED_AWAITING_SAME_HEAD_CI','P15B_MANUAL30_INHERITED_RUNNING_SERVICE_PREFLIGHT_FAIL_CLOSED_GATE_CLOSED','P15B_MANUAL30_SERVICE_BASELINE_CORRECTION_CI_PENDING_GATE_CLOSED','P15B_MANUAL30_SERVICE_BASELINE_CORRECTION_CI_PASS_GATE_CLOSED','P15B_MANUAL30_POWERSHELL_PID_COLLISION_PREFLIGHT_FAIL_CLOSED_GATE_CLOSED','P15B_MANUAL30_POWERSHELL_PID_COLLISION_CORRECTION_CI_PENDING_GATE_CLOSED','P15B_MANUAL30_POWERSHELL_PID_COLLISION_CORRECTION_CI_PASS_GATE_CLOSED','P15B_MANUAL30_PHYSICAL_PASS_FORMALLY_CLOSED') -or $postP15BState){
    Assert-True ([bool]$contract.manual30.preparationCiValidated) 'Closed P15B preparation must be CI validated.'
    Assert-True ([bool]$contract.manual30.preparationClosure.closed) 'Closed P15B preparation must record closure.'
    $pc=$contract.manual30.preparationClosure
    if([string]$pc.result -cne 'PASS' -or
       [string]$pc.implementationHead -cne '111d1e7a817c2468a4d6196447ec335d57c59d65' -or
       [int]$pc.sourceCiRunNumber -ne 1103 -or
       [long]$pc.sourceCiRunId -ne 36930333854 -or
       [string]$pc.sourceCiResult -cne 'SUCCESS'){throw 'P15B preparation closure CI identity mismatch.'}
    foreach($p in @('powerShellSyntaxValidated','p15aClosureInvariantValidated','p15bInvariantValidated','trackedChildSelfTestValidated','evidencePackagingSelfTestValidated','powerShell51CompatibilityValidated','warningsAsErrorsBuildValidated')){Assert-True ([bool]$pc.$p) ("P15B preparation closure missing validation: {0}" -f $p)}
    Assert-False ([bool]$pc.hardwareExecution) 'P15B preparation closure must record no hardware execution.'
    $failed=@($pc.failedCiHistoryPreserved)
    if($failed.Count -ne 2 -or [int]$failed[0].runNumber -ne 1101 -or [int]$failed[1].runNumber -ne 1102){throw 'P15B preparation failed-CI history mismatch.'}
    foreach($entry in $failed){Assert-False ([bool]$entry.hardwareExecution) 'P15B failed preparation CI must record no hardware execution.'}
}
if($status -eq 'P15B_MANUAL30_INHERITED_RUNNING_SERVICE_PREFLIGHT_FAIL_CLOSED_GATE_CLOSED'){
    Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B authorization must be revoked after target preflight FAIL_CLOSED.'
    Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B controller authorization must be revoked after target preflight FAIL_CLOSED.'
    Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15B controller gate must be closed after target preflight FAIL_CLOSED.'
    $pf=$contract.manual30.priorFailClosedAttempt
    if([string]$pf.sourceHead -cne '6fe617693d4de54bfc709e07728c825f2246072d' -or
       [int]$pf.sourceCiRunNumber -ne 1106 -or
       [long]$pf.sourceCiRunId -ne 36931559263 -or
       [string]$pf.result -cne 'FAIL_CLOSED' -or
       [int]$pf.observedServicePid -ne 7980){throw 'P15B target preflight FAIL_CLOSED identity mismatch.'}
    foreach($p in @('repositoryHeadMatched','onlyPreservedUntrackedLogsObserved','targetFingerprintPassed','administratorCheckPassed','acBatterySanityPassed','watchdogJournalAbsent')){Assert-True ([bool]$pf.$p) ("P15B target preflight expected true: {0}" -f $p)}
    foreach($p in @('conflictingControllerProcessObserved','operatorTokenPromptReached','ecSetpointProbeExecuted','watchdogLeaseAcquired','failsafeArmed','controllerStarted','fanWriteExecuted','firmwareRestoreExecuted','evidencePackageProduced','physicalPassClaimed','automaticAuthorizationOpened')){Assert-False ([bool]$pf.$p) ("P15B target preflight expected false: {0}" -f $p)}
    $corr=$contract.manual30.serviceBaselineCorrection
    Assert-True ([bool]$corr.required) 'P15B inherited-running service correction must be required.'
    Assert-True ([bool]$corr.inheritedRunningBaselineObserved) 'P15B correction must record inherited Running baseline.'
    Assert-True ([bool]$corr.acceptExactQualifiedStoppedBaselinePlanned) 'P15B correction must preserve stopped baseline support.'
    Assert-True ([bool]$corr.acceptExactQualifiedRunningReadyNoJournalBaselinePlanned) 'P15B correction must plan safe inherited Running support.'
    Assert-True ([bool]$corr.preserveInitialServiceStatePlanned) 'P15B correction must preserve initial service state.'
    Assert-False ([bool]$corr.implementationComplete) 'P15B correction cannot pre-claim implementation.'
    Assert-False ([bool]$corr.ciValidated) 'P15B correction cannot pre-claim CI.'
    Assert-False ([bool]$corr.closure.closed) 'P15B correction cannot pre-close.'
}
if($status -in @('P15B_MANUAL30_SERVICE_BASELINE_CORRECTION_CI_PENDING_GATE_CLOSED','P15B_MANUAL30_SERVICE_BASELINE_CORRECTION_CI_PASS_GATE_CLOSED')){
    Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B must remain re-blocked during/after service-baseline correction until fresh authorization.'
    Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B controller must remain re-blocked during/after service-baseline correction until fresh authorization.'
    Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15B controller compile-time gate must remain closed during correction/closure.'
    $corr=$contract.manual30.serviceBaselineCorrection
    Assert-True ([bool]$corr.required) 'P15B service-baseline correction must remain required.'
    Assert-True ([bool]$corr.implementationComplete) 'P15B service-baseline correction implementation must be complete.'
    Assert-True ([bool]$corr.selfTestImplemented) 'P15B service-baseline correction self-test must be implemented.'
    Assert-True ([bool]$contract.manual30.preserveInitialServiceState) 'P15B correction must preserve the initial service state.'
    if(@($contract.manual30.allowedInitialServiceStates).Count -ne 2){throw 'P15B correction must define exactly two accepted initial service states.'}
    if($status -eq 'P15B_MANUAL30_SERVICE_BASELINE_CORRECTION_CI_PENDING_GATE_CLOSED'){
        Assert-False ([bool]$corr.ciValidated) 'Pending P15B service-baseline correction must not pre-claim CI validation.'
        Assert-False ([bool]$corr.closure.closed) 'Pending P15B service-baseline correction must not pre-close.'
    }else{
        Assert-True ([bool]$corr.ciValidated) 'Closed P15B service-baseline correction must be CI validated.'
        Assert-True ([bool]$corr.closure.closed) 'Closed P15B service-baseline correction must record closure.'
        $cc=$corr.closure
        if([string]$cc.result -cne 'PASS' -or
           [string]$cc.implementationHead -cne 'b24308e6b72e50e9a2ae40501c95f77bcfcf9f43' -or
           [int]$cc.sourceCiRunNumber -ne 1108 -or
           [long]$cc.sourceCiRunId -ne 36932942781 -or
           [string]$cc.sourceCiResult -cne 'SUCCESS'){throw 'P15B service-baseline correction closure identity mismatch.'}
        foreach($p in @('powerShellSyntaxValidated','p15aInvariantValidated','p15bInvariantValidated','serviceBaselineSelfTestValidated','powerShell51CompatibilityValidated','warningsAsErrorsBuildValidated','retainedRcRegressionValidated')){Assert-True ([bool]$cc.$p) ("P15B service-baseline correction closure missing validation: {0}" -f $p)}
        Assert-False ([bool]$cc.hardwareExecution) 'P15B service-baseline correction closure must record no hardware execution.'
    }
}
if($status -eq 'P15B_MANUAL30_POWERSHELL_PID_COLLISION_PREFLIGHT_FAIL_CLOSED_GATE_CLOSED'){
    Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B must be re-blocked after PowerShell preflight failure.'
    Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B controller must be re-blocked after PowerShell preflight failure.'
    Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15B controller gate must be closed after PowerShell preflight failure.'
    $pf=$contract.manual30.priorFailClosedAttempt2
    if([string]$pf.sourceHead -cne 'df355a0a49c76115f6cac7885650b5a7bec9aa86' -or [int]$pf.sourceCiRunNumber -ne 1110 -or [long]$pf.sourceCiRunId -ne 36933720023 -or [string]$pf.result -cne 'FAIL_CLOSED'){throw 'P15B PowerShell preflight failure identity mismatch.'}
    foreach($p in @('watchdogJournalAbsent')){Assert-True ([bool]$pf.$p) ("P15B PowerShell preflight expected true: {0}" -f $p)}
    foreach($p in @('operatorTokenPromptReached','evidenceDirectoryCreated','ecSetpointProbeExecuted','watchdogLeaseAcquired','failsafeArmed','controllerStarted','fanWriteExecuted','firmwareRestoreExecuted','evidencePackageProduced','physicalPassClaimed')){Assert-False ([bool]$pf.$p) ("P15B PowerShell preflight expected false: {0}" -f $p)}
    $corr=$contract.manual30.powerShellPidCollisionCorrection
    Assert-True ([bool]$corr.required) 'P15B PowerShell PID-collision correction must be required.'
    Assert-True ([bool]$corr.addRegressionGuardAgainstPidAutomaticVariableCollision) 'P15B PID-collision correction must require a regression guard.'
    Assert-False ([bool]$corr.implementationComplete) 'P15B PID-collision correction cannot pre-claim implementation.'
    Assert-False ([bool]$corr.ciValidated) 'P15B PID-collision correction cannot pre-claim CI.'
    Assert-False ([bool]$corr.closure.closed) 'P15B PID-collision correction cannot pre-close.'
}
if($status -in @('P15B_MANUAL30_POWERSHELL_PID_COLLISION_CORRECTION_CI_PENDING_GATE_CLOSED','P15B_MANUAL30_POWERSHELL_PID_COLLISION_CORRECTION_CI_PASS_GATE_CLOSED','P15B_MANUAL30_PHYSICAL_PASS_FORMALLY_CLOSED') -or $postP15BState){
    Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B must remain re-blocked during/after PID-collision correction until fresh authorization.'
    Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B controller must remain re-blocked during/after PID-collision correction until fresh authorization.'
    Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15B controller gate must remain closed during PID-collision correction/closure.'
    $pcorr=$contract.manual30.powerShellPidCollisionCorrection
    Assert-True ([bool]$pcorr.required) 'P15B PID-collision correction must remain required.'
    Assert-True ([bool]$pcorr.implementationComplete) 'P15B PID-collision correction implementation must be complete.'
    Assert-True ([bool]$pcorr.regressionGuardImplemented) 'P15B PID-collision regression guard must be implemented.'
    if($harness -match '(?im)^\s*\$pid\b\s*='){throw 'P15B harness must never assign PowerShell automatic variable $PID (case-insensitive).'}
    Assert-Contains $harness '$servicePid=[int]$svc.ProcessId' 'P15B PID-collision correction must use $servicePid.'
    if($status -eq 'P15B_MANUAL30_POWERSHELL_PID_COLLISION_CORRECTION_CI_PENDING_GATE_CLOSED'){
        Assert-False ([bool]$pcorr.ciValidated) 'Pending P15B PID-collision correction must not pre-claim CI.'
        Assert-False ([bool]$pcorr.closure.closed) 'Pending P15B PID-collision correction must not pre-close.'
    }else{
        Assert-True ([bool]$pcorr.ciValidated) 'Closed P15B PID-collision correction must be CI validated.'
        Assert-True ([bool]$pcorr.closure.closed) 'Closed P15B PID-collision correction must record closure.'
        $cc=$pcorr.closure
        if([string]$cc.result -cne 'PASS' -or [string]$cc.implementationHead -cne '3e2ee4851e7bc5f393e23f782c09ef7f5db92b43' -or [int]$cc.sourceCiRunNumber -ne 1112 -or [long]$cc.sourceCiRunId -ne 36935048160 -or [string]$cc.sourceCiResult -cne 'SUCCESS'){throw 'P15B PID-collision correction closure identity mismatch.'}
        foreach($p in @('powerShellSyntaxValidated','p15bInvariantValidated','pidAutomaticVariableRegressionGuardValidated','powerShell51CompatibilityValidated','warningsAsErrorsBuildValidated','retainedRcRegressionValidated')){Assert-True ([bool]$cc.$p) ("P15B PID-collision correction closure missing validation: {0}" -f $p)}
        Assert-False ([bool]$cc.hardwareExecution) 'P15B PID-collision correction closure must record no hardware execution.'
    }
}
if($status -eq 'P15B_MANUAL30_AUTHORIZED_AWAITING_SAME_HEAD_CI'){
    Assert-True ([bool]$contract.manual30.executionAuthorized) 'P15B harness execution authorization must be open.'
    Assert-True ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B qualification controller authorization must be open.'
    $a=$contract.manual30.authorization
    if([string]$a.scope -cne 'P15B one-shot Manual equal 30/30 plus production strong restore only' -or
       [string]$a.basisHead -cne 'ccd52aaab7e7d3ac2a33c9419c2cd0a06b341147' -or
       [int]$a.basisCiRunNumber -ne 1113 -or
       [long]$a.basisCiRunId -ne 36935433765 -or
       [string]$a.basisCiResult -cne 'SUCCESS'){throw 'P15B final corrected authorization basis mismatch.'}
    Assert-True ([bool]$a.serviceBaselineCorrectionClosed) 'P15B authorization requires closed service-baseline correction.'
    Assert-True ([bool]$a.powerShellPidCollisionCorrectionClosed) 'P15B authorization requires closed PID-collision correction.'
    if([string]$a.serviceBaselineCorrectionImplementationHead -cne 'b24308e6b72e50e9a2ae40501c95f77bcfcf9f43' -or [int]$a.serviceBaselineCorrectionCiRunNumber -ne 1108){throw 'P15B service-baseline correction identity mismatch.'}
    if([string]$a.powerShellPidCollisionCorrectionImplementationHead -cne '3e2ee4851e7bc5f393e23f782c09ef7f5db92b43' -or [int]$a.powerShellPidCollisionCorrectionCiRunNumber -ne 1112){throw 'P15B PID-collision correction identity mismatch.'}
    Assert-True ([bool]$a.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P15B authorization must require same-HEAD CI success.'
    foreach($p in @('p15aAuthorizationOpened','userFacingManualGateOpened','automaticAuthorizationOpened','candidateCurveAuthorizationOpened','m9cQualificationConstructionOpened','m9dQualificationConstructionOpened','hardwareExecutionAtAuthorizationCommit')){Assert-False ([bool]$a.$p) ("P15B authorization opened forbidden scope: {0}" -f $p)}
    $attempts=@($a.priorTargetAttempts)
    if($attempts.Count -ne 2 -or [string]$attempts[0].authorizationHead -cne '6fe617693d4de54bfc709e07728c825f2246072d' -or [int]$attempts[0].authorizationCiRunNumber -ne 1106 -or [string]$attempts[1].authorizationHead -cne 'df355a0a49c76115f6cac7885650b5a7bec9aa86' -or [int]$attempts[1].authorizationCiRunNumber -ne 1110){throw 'P15B prior target-attempt history mismatch.'}
    foreach($attempt in $attempts){Assert-False ([bool]$attempt.physicalWriteExecuted) 'Prior P15B refused preflight must record no fan write.';Assert-True ([bool]$attempt.authorizationRevoked) 'Prior P15B authorization must have been revoked.'}
    $failedAuth=@($a.earlierAuthorizationBasis.failedSameHeadCiHistory)
    if($failedAuth.Count -ne 1 -or [int]$failedAuth[0].runNumber -ne 1105 -or [long]$failedAuth[0].runId -ne 36931207120 -or [string]$failedAuth[0].head -cne 'cf7ba5cd0ec31aeddd62cc7f86160c9328e3c82d' -or [string]$failedAuth[0].result -cne 'FAILURE'){throw 'P15B earlier failed authorization CI history mismatch.'}
    Assert-False ([bool]$failedAuth[0].hardwareExecution) 'Failed P15B authorization CI must record no hardware execution.'
    Assert-True ([bool]$contract.manual30.serviceBaselineCorrection.ciValidated) 'P15B authorization requires CI-validated service-baseline correction.'
    Assert-True ([bool]$contract.manual30.serviceBaselineCorrection.closure.closed) 'P15B authorization requires formally closed service-baseline correction.'
    Assert-True ([bool]$contract.manual30.powerShellPidCollisionCorrection.ciValidated) 'P15B authorization requires CI-validated PID-collision correction.'
    Assert-True ([bool]$contract.manual30.powerShellPidCollisionCorrection.closure.closed) 'P15B authorization requires formally closed PID-collision correction.'
    if($harness -match '(?im)^\s*\$pid\b\s*='){throw 'P15B authorized harness must not assign PowerShell automatic variable $PID.'}
    Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P15B authorized state requires dedicated qualification controller gate open.'
}else{
    Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B physical execution must remain CLOSED during preparation/closure.'
    Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B controller physical gate must remain CLOSED during preparation/closure.'
    Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15B preparation/closure requires qualification controller gate closed.'
}
if($status -eq 'P15B_MANUAL30_PHYSICAL_PASS_FORMALLY_CLOSED' -or $postP15BState){
    Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B execution must be re-blocked after physical PASS.'
    Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B qualification controller must be re-blocked after physical PASS.'
    Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15B controller hard gate must be closed after physical PASS.'
    Assert-True ([bool]$contract.manual30.physicalPassed) 'P15B physical PASS closure must set physicalPassed.'
    Assert-True ([bool]$contract.manual30.evidenceClosed) 'P15B physical PASS closure must close evidence.'
    $p=$contract.manual30.physicalPassClosure
    Assert-True ([bool]$p.closed) 'P15B physical PASS closure must be closed.'
    if([string]$p.result -cne 'PASS' -or [string]$p.sourceHead -cne 'f6261383c1cdc40808bf1ac232c93e3686478716' -or [int]$p.sourceCiRunNumber -ne 1114 -or [long]$p.sourceCiRunId -ne 36935852739 -or [string]$p.sourceCiResult -cne 'SUCCESS'){throw 'P15B physical PASS source/CI identity mismatch.'}
    if([string]$p.evidenceZipSha256 -cne 'a34ea5d14de161be685e767e5d54e727b17d9b8c2cc5396c9ffa6ec7e61335da' -or [string]$p.packageManifestSha256 -cne 'bfb9ec3aa79001ca4e351900c39c3da2cdf7486f4cfd9c66630d313c5f4fed1a' -or [string]$p.harnessSummarySha256 -cne '7c8111bea09227fc057aaa58a49800f2a9503893ffc820f85c25b044757f7f83' -or [string]$p.controllerResultSha256 -cne 'a5c96550bd454b33556a413818768ad61f67872f9f57cb8f6d26a6a0a4160bba'){throw 'P15B physical PASS evidence identity mismatch.'}
    if([string]$p.evidenceSidecarSha256 -cne '2f16fe448f18b15a0b2c7e69637df117e50a006dfcff6e00cf509b8dbc6f96e8'){throw 'P15B physical PASS sidecar identity mismatch.'}
    foreach($n in @('archiveIntegrityVerified','packageManifestEmbeddedHashesVerified','packageManifestSourceIdentityEntriesVerified','repositoryHeadMatched','upstreamHeadMatchedAtExecution','trackedSourceClean','onlyPreservedUntrackedLogsObserved','targetMatched','causalPrepareWriteIntentCommitRestoreReleaseVerified','strongRestoreVerified','localFirmwareAckVerified','watchdogReleaseVerified','finalJournalAbsent','watchdogProcessIdentityStable','initialServiceStatePreserved','failsafeArmed','evidenceIndependentlyReviewed','physicalPassSupported')){Assert-True ([bool]$p.$n) ("P15B physical PASS closure expected true: {0}" -f $n)}
    if([int]$p.packageManifestEmbeddedEvidenceHashCount -ne 13 -or [int]$p.packageManifestSourceIdentityEntryCount -ne 5){throw 'P15B physical PASS manifest verification counts mismatch.'}
    if([int]$p.exactApplyManualCalls -ne 1 -or [string]$p.equalLevel -cne '30/30' -or [int]$p.preWriteSamples -ne 3 -or [int]$p.supervisionSamples -ne 3){throw 'P15B physical PASS bounded Manual transaction mismatch.'}
    if([int]$p.ownedJournalSchemaVersion -ne 2 -or [int]$p.ownedJournalGeneration -ne 3 -or [int]$p.ownedJournalControllerPid -ne 22568 -or [long]$p.ownedJournalControllerStartUtcTicks -ne 639264920440344831){throw 'P15B physical PASS OWNED journal binding mismatch.'}
    if([int]$p.controllerFinalFirmwareSamples -ne 2 -or [int]$p.independentFinalFfFfSamples -ne 2 -or [int]$p.cleanupFfFfSamples -ne 2){throw 'P15B physical PASS final FF/FF proof count mismatch.'}
    if([string]$p.initialServiceState -cne 'Manual/Running/PID7980/LocalSystem' -or [string]$p.finalServiceState -cne 'Manual/Running/PID7980/LocalSystem' -or [int]$p.watchdogPid -ne 7980 -or [long]$p.watchdogStartUtcTicks -ne 639264861577277909){throw 'P15B physical PASS preserved service baseline mismatch.'}
    foreach($n in @('serviceStartedByHarness','serviceRestartedForCleanup','failsafeTakeover','userFacingManualExecutionAuthorized','automaticExecutionAuthorized','candidateCurveAuthorizedForProduction','m9cQualificationConstructionAuthorized','m9dQualificationConstructionAuthorized','nextPhysicalGateOpened')){Assert-False ([bool]$p.$n) ("P15B physical PASS closure expected false: {0}" -f $n)}
}else{
    Assert-False ([bool]$contract.manual30.physicalPassed) 'P15B cannot pre-claim physical PASS.'
    Assert-False ([bool]$contract.manual30.evidenceClosed) 'P15B cannot pre-close physical evidence.'
}
if([int]$contract.manual30.equalLevel -ne 30 -or [int]$contract.manual30.exactApplyManualCalls -ne 1){throw 'P15B must be exactly one equal 30/30 Manual call.'}
Assert-True ([bool]$contract.manual30.strongRestoreRequired) 'P15B strong restore must be required.'
foreach($p in @('productionRestorePathRequired','ffReleaseRequired','legacyDefaultRequired','localFfFfAckRequired','watchdogRestoreBeginRequired','watchdogReleaseRequired','stableIndependentFfFfRequired','watchdogJournalAbsentRequired','watchdogProcessIdentityStableRequired')){Assert-True ([bool]$contract.manual30.strongRestore.$p) ("P15B strong restore contract missing: {0}" -f $p)}
if([int]$contract.manual30.strongRestore.requiredConsecutiveFinalFfFfSamples -ne 2){throw 'P15B final FF/FF proof must require two consecutive independent samples.'}
Assert-False ([bool]$contract.automatic.executionAuthorized) 'Automatic execution must remain CLOSED.'
Assert-False ([bool]$contract.safetyBoundary.manualExecutionAuthorized) 'User-facing Manual gate must remain CLOSED.'
Assert-False ([bool]$contract.safetyBoundary.automaticExecutionAuthorized) 'User-facing Automatic gate must remain CLOSED.'
Assert-False ([bool]$contract.safetyBoundary.controlEnabledByDefault) 'Default control must remain OFF.'
Assert-False ([bool]$contract.safetyBoundary.automaticPolicyEnabled) 'Automatic policy must remain OFF.'
Assert-False ([bool]$profile.control.enabledByDefault) 'Profile default control must remain OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Profile automatic policy must remain OFF.'
Assert-Contains $prodGate 'public static readonly bool ProductionConstructionAuthorized = true;' 'P15B requires already-promoted production watchdog construction.'
Assert-Contains $prodGate 'M9CPhysicalQualificationConstructionAuthorized = false' 'P15B must not reopen M9C qualification.'
Assert-Contains $prodGate 'M9DPhysicalQualificationConstructionAuthorized = false' 'P15B must not reopen M9D qualification.'
Assert-Contains $userGate 'ManualExecutionAuthorized = false' 'P15B must not open user-facing Manual.'
Assert-Contains $userGate 'AutomaticExecutionAuthorized = false' 'P15B must not open user-facing Automatic.'
foreach($n in @('Hp8C40ProductionWatchdogGate.CreateLeaseIfAuthorized','HpFanControlBackendFactory.Create','new AdaptiveFanProductionController','manualExecutionAuthorized: true','automaticExecutionAuthorized: false','AdaptiveFanProductionMode.Manual','ApplyManualAsync(','QualificationLevel','ReleaseToFirmwareAsync(','LastRestoreEvidence','WatchdogReleaseVerified','ReadStableFirmwareOwnedAsync','SupervisionSamples = 3')){Assert-Contains $controller $n ("P15B controller route invariant missing: {0}" -f $n)}
Assert-NotContains $controller 'EnterM9CPhysicalQualificationConstructionScope' 'P15B must not reopen M9C construction scope.'
Assert-NotContains $controller 'EnterM9DPhysicalQualificationConstructionScope' 'P15B must not reopen M9D construction scope.'
Assert-Contains $adaptive 'new FanCommand(' 'Adaptive Manual path must continue issuing coordinator FanCommand.'
Assert-Contains $adaptive '$"manual equal target {equalFanLevel}/{equalFanLevel}"' 'Adaptive Manual path must remain equal-only.'
Assert-Contains $backend '_hardware!.RestoreFirmwareAuto();' 'P15B production backend must route restore through validated HP firmware-auto method.'
Assert-Contains $backend 'WaitForSetpointAsync(' 'P15B production backend must verify restore setpoint acknowledgement.'
Assert-Contains $backend 'await _watchdogLease.RestoreBeginAsync(' 'P15B production backend must begin watchdog restore handoff.'
Assert-Contains $backend 'await _watchdogLease.ReleaseAsync(' 'P15B production backend must verify watchdog RELEASE.'
Assert-Contains $bios 'BuildReleaseFanLevelRequest()' 'P15B strong restore must include FF/FF release.'
Assert-Contains $bios 'BuildLegacyDefaultRequest()' 'P15B strong restore must include LegacyDefault.'
Assert-Contains $bios 'ExecuteRestoreSequence(' 'P15B restore must execute both release and LegacyDefault attempts.'
foreach($n in @('HARD VERSIONED AUTHORIZATION BARRIER','manual30.executionAuthorized','controllerPhysicalExecutionAuthorized','Assert-RepositoryProvenance','Assert-ExactTarget','Get-ValidatedServiceBaseline','Resolve-P15BServiceBaselineMode','initialServiceMode','initialServiceStatePreserved','Assert-StableSetpoint 255 255','Start-Service -Name $serviceName','Start-P15BFailsafe','--8c40-p15b-manual30','Assert-OwnedJournal','Assert-StableSetpoint 30 30','P15B-CONTINUE','Assert-CausalServiceLog','Assert-StableSetpoint 255 255','Stop-Service -Name $serviceName','package-p15b-evidence.ps1','FAIL_CLOSED')){Assert-Contains $harness $n ("P15B harness invariant missing: {0}" -f $n)}
foreach($n in @('Stopped','Running','JournalPresent','ReadyVerified')){Assert-Contains $baselineHelper $n ("P15B baseline helper invariant missing: {0}" -f $n)}
foreach($n in @('Inherited Running baseline','Running without Ready must fail.','Running with retained journal must fail.','Stopped baseline')){Assert-Contains $baselineSelfTest $n ("P15B baseline self-test invariant missing: {0}" -f $n)}
foreach($n in @('SetFanLevel(','--restore-hp-auto','git clean','Set-Service','New-Service','sc.exe ')){Assert-NotContains $harness $n ("P15B harness contains forbidden direct operation: {0}" -f $n)}
Assert-Contains $failsafe "TargetProfileId -cne 'HP-8C40-9D0R1LA-F18'" 'P15B failsafe must bind exact target.'
Assert-Contains $failsafe '[int]$Journal.Owned.Cpu -eq 30' 'P15B failsafe must bind exact 30/30 ownership.'
Assert-Contains $packager 'sourceEvidencePreserved=$true' 'P15B packager preservation marker missing.'
Assert-Contains $packager 'gitCleanUsed=$false' 'P15B packager must record no git clean.'
Assert-NotContains $packager 'git clean' 'P15B packager must never invoke git clean.'
Assert-Contains $cli '--8c40-p15b-manual30' 'P15B CLI switch missing.'
Assert-Contains $program 'Hp8C40P15BManual30QualificationTest.RunAsync' 'P15B Program dispatch missing.'
Write-Host 'HP 8C40 P15B Manual30 preparation invariant: PASS' -ForegroundColor Green
