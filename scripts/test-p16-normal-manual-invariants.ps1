$ErrorActionPreference='Stop'
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}}

$root=Split-Path -Parent $PSScriptRoot
$p16=Get-Content -LiteralPath (Join-Path $root 'release\p16-target-checkpoint.json') -Raw|ConvertFrom-Json
$p15=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw|ConvertFrom-Json
$profile=Get-Content -LiteralPath (Join-Path $root 'profiles\HP-8C40.json') -Raw|ConvertFrom-Json
$gate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P16NormalManualQualification.cs') -Raw
$userGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$main=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$surface=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P13FanControlSurface.cs') -Raw
$program=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\Program.cs') -Raw
$harness=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p16-normal-manual.ps1') -Raw
$packager=Get-Content -LiteralPath (Join-Path $root 'scripts\package-p16-evidence.ps1') -Raw
$failsafe=Get-Content -LiteralPath (Join-Path $root 'scripts\watchdog-p15d2-service-failsafe-8c40.ps1') -Raw
$workflow=Get-Content -LiteralPath (Join-Path $root '.github\workflows\build.yml') -Raw
$doc=Get-Content -LiteralPath (Join-Path $root 'docs\P16_NORMAL_MANUAL.md') -Raw
$hardeningHelper=Get-Content -LiteralPath (Join-Path $root 'scripts\p16-hardening-helpers.ps1') -Raw
$hardeningSelfTest=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p16-hardening-helpers.ps1') -Raw
$backend=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs') -Raw
$backendSelfTest=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackendSelfTest.cs') -Raw
$hp8c40Probe=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\Hp\Hp8C40EcControlStateProbe.cs') -Raw
$ecReader=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\PawnIo\AcpiEcReader.cs') -Raw
$setpointStabilizer=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Hardware\PawnIo\FanSetpointSnapshotStabilizer.cs') -Raw

$d=$p16.normalManual
$status=[string]$p16.status
if($status -notin @('P16A_NORMAL_MANUAL_IMPLEMENTATION_CI_PENDING_GATE_CLOSED','P16A_NORMAL_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED','P16B_NORMAL_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI','P16B_PHYSICAL_ATTEMPTS_FAIL_CLOSED_GATE_CLOSED','P16B_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED','P16B_HARDENING_CI_PASS_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_CI_PASS_GATE_CLOSED')){throw "Unexpected P16 status: $status"}
$isPostAttempt3Hardening=($status -in @('P16B_ATTEMPT3_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_CI_PASS_GATE_CLOSED'))
$isPostAttempt3HardeningClosed=($status -eq 'P16B_ATTEMPT3_HARDENING_CI_PASS_GATE_CLOSED')
$isP16APrepared=($status -in @('P16A_NORMAL_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED','P16B_NORMAL_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI','P16B_PHYSICAL_ATTEMPTS_FAIL_CLOSED_GATE_CLOSED','P16B_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED','P16B_HARDENING_CI_PASS_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_CI_PASS_GATE_CLOSED'))
$isPhysicalFailClosed=($status -in @('P16B_PHYSICAL_ATTEMPTS_FAIL_CLOSED_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED'))
$isHardened=($status -in @('P16B_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED','P16B_HARDENING_CI_PASS_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_IMPLEMENTED_CI_PENDING_GATE_CLOSED','P16B_ATTEMPT3_HARDENING_CI_PASS_GATE_CLOSED'))
$isHardeningClosed=($status -eq 'P16B_HARDENING_CI_PASS_GATE_CLOSED')
$hasPhysicalHistory=(@($d.physicalAttemptHistory).Count -gt 0)
$isAuthorized=($status -eq 'P16B_NORMAL_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI')

if([string]$p15.status -cne 'P15D2_VARIABLE_MANUAL_PHYSICAL_PASS_FORMALLY_CLOSED'){throw 'P16A requires formally closed P15D2.'}
$d15=$p15.guiManualVariableLevel
Assert-True ([bool]$d15.physicalPassed) 'P16A requires P15D2 physical PASS.'
Assert-True ([bool]$d15.evidenceClosed) 'P16A requires P15D2 evidence closure.'
Assert-False ([bool]$d15.executionAuthorized) 'P16A requires P15D2 parent gate re-blocked.'
Assert-False ([bool]$d15.controllerPhysicalExecutionAuthorized) 'P16A requires P15D2 controller gate re-blocked.'
Assert-False ([bool]$d15.physicalGatesOpened) 'P16A requires P15D2 gates closed.'

$b=$p16.p15Baseline
if([string]$b.closureHead -cne 'c1e04963448e78a87dccc1719f11d9951f2970a7' -or [int]$b.closureCiRunNumber -ne 1180 -or [long]$b.closureCiRunId -ne 37050355620 -or [string]$b.closureCiResult -cne 'SUCCESS'){throw 'P16A P15 baseline identity mismatch.'}
if([string]$p16.expectedBranch -cne 'feature/victus-8c40-p16-normal-manual'){throw 'P16A branch mismatch.'}
if([string]$p16.targetProfileId -cne 'HP-8C40-9D0R1LA-F18'){throw 'P16A target mismatch.'}

Assert-True ([bool]$d.architectureImplemented) 'P16A architecture missing.'
Assert-True ([bool]$d.architectureCiValidated) 'P16A requires architecture CI PASS.'
$a=$d.architectureValidation
if([string]$a.head -cne '8fbe8aba0dc892f0fba988246164a13357325a7c' -or [int]$a.runNumber -ne 1182 -or [long]$a.runId -ne 37057172456 -or [string]$a.result -cne 'SUCCESS'){throw 'P16A architecture CI identity mismatch.'}
Assert-True ([bool]$d.preparationImplemented) 'P16A full preparation must be implemented.'
if($isP16APrepared){
 Assert-True ([bool]$d.preparationCiValidated) 'P16A closed state requires preparation CI validation.'
 Assert-True ([bool]$d.preparationClosure.closed) 'P16A closed state requires formal preparation closure.'
 $pv=$d.preparationValidation
 if([string]$pv.head -cne '635e0a0d83922331a4a940206f150d7c68c8b01b' -or [int]$pv.runNumber -ne 1185 -or [long]$pv.runId -ne 37058666450 -or [string]$pv.result -cne 'SUCCESS'){throw 'P16A preparation validation identity mismatch.'}
 $pc=$d.preparationClosure
 if([string]$pc.result -cne 'PASS' -or [string]$pc.implementationHead -cne '635e0a0d83922331a4a940206f150d7c68c8b01b' -or [int]$pc.sourceCiRunNumber -ne 1185 -or [long]$pc.sourceCiRunId -ne 37058666450 -or [string]$pc.sourceCiResult -cne 'SUCCESS'){throw 'P16A formal preparation closure identity mismatch.'}
 Assert-False ([bool]$pc.hardwareExecution) 'P16A closure must record no hardware execution.'
 Assert-False ([bool]$pc.physicalGatesOpened) 'P16A closure must keep physical gates closed.'
}else{
 Assert-False ([bool]$d.preparationCiValidated) 'P16A pending implementation cannot pre-claim CI.'
 Assert-False ([bool]$d.preparationClosure.closed) 'P16A pending implementation cannot pre-close.'
}
if($isPhysicalFailClosed){
 Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P16B fail-closed state must re-block the dedicated source gate.'
 $attempts=@($d.physicalAttemptHistory)
 if($attempts.Count -ne 5){throw 'P16B fail-closed history must preserve exactly five target attempts.'}
 foreach($attempt in $attempts){if([string]$attempt.result -cne 'FAIL_CLOSED'){throw 'Every preserved P16B target attempt must remain FAIL_CLOSED.'}}
 if([string]$attempts[0].evidenceZipSha256 -cne '2f164727994683bb7f5c1d49eeff871b017d6a129dc4c6de9ca1e5c51d44a178'){throw 'P16B attempt-1 evidence identity mismatch.'}
 if([string]$attempts[1].evidenceZipSha256 -cne 'c26eb3afab86a6998c8f52233165b276e4bd1d52a4ec803ef03bc9192978a661'){throw 'P16B attempt-2 evidence identity mismatch.'}
 if([string]$attempts[2].evidenceZipSha256 -cne '35bfd02daafe126f72af096e57a49c709195e05d563c1fa0666f459bcb2038bd'){throw 'P16B attempt-3 evidence identity mismatch.'}
 if([int]$attempts[3].attempt -ne 4 -or
    [string]$attempts[3].sourceHead -cne 'fdf08d94cddca82e3d1431bdf95e68c37aae24aa' -or
    [int]$attempts[3].sourceCiRunNumber -ne 1198 -or
    [long]$attempts[3].sourceCiRunId -ne 37074191886 -or
    [string]$attempts[3].sourceCiResult -cne 'SUCCESS' -or
    [string]$attempts[3].classification -cne 'NO_WRITE_POWER_TRANSITION_INTERRUPTION' -or
    [string]$attempts[3].evidenceZipSha256 -cne 'f95a529b3e0ccbe38ea1ef4a541e08a0030cd10031ea616deef0c710bbb85da6' -or
    [string]$attempts[3].manifestSha256 -cne 'd3fb715d2fff1940a3c128279749ee6f2048c8dbf62b90d14d50323b035ab7c7' -or
    [string]$attempts[3].harnessSummarySha256 -cne 'b4907576f6fb3e1cf95e964df0a383250fae8afcbc8beaf064ef08321c3f35bf' -or
    [string]$attempts[3].attemptFenceSha256 -cne '861d6d170a1e0981be0b818e75ad8cc1866d0a150c5b90af4b38f2acbed06081'){
   throw 'P16B attempt-4 evidence identity mismatch.'
 }
 $auth=$d.authorization
 if([int]$auth.authorizationGeneration -ne 4 -or -not $auth.authorizationConsumed -or -not $auth.freshAuthorizationRequired -or [int]$auth.consumedByAttempt -ne 5 -or -not $auth.attemptFenceClaimed -or $auth.attemptFenceAuthorizationHead -cne '98fdc73dbf1ac4f81812ae84f45047c9e1845703'){throw 'P16 generation 4 must remain consumed by attempt 5.'}
 if($attempts[4].sourceHead -cne '98fdc73dbf1ac4f81812ae84f45047c9e1845703' -or $attempts[4].evidenceZipSha256 -cne '811fafd002311482e75b21dcfa24528fa05f458f913ba7a9a0afd72c00a3363f' -or [long]$attempts[4].sourceCiRunId -ne 37092681751){throw 'P16 attempt-5 identity altered.'}
 $p5=$d.hardeningRequired.postAttempt5
 if(-not $p5.initial30WmiAckProven -or -not $p5.initial30IndependentOwnershipProven -or $p5.changed40BackendDispatchObserved -or $p5.independentFinalFirmwareProof -or $p5.exactSafetyDenialCauseEstablished -or $p5.safetyPredicateChanged -or $p5.thermalLimitsChanged -or $p5.fanWritesChanged){throw 'P16 attempt-5 scope was overstated.'}
 $investigation=$d.hardeningRequired.softwareConcurrencyInvestigation
 if($null -ne $investigation){
   if(-not $investigation.softwareFixImplemented -or [int]$investigation.deterministicCases -ne 8 -or $investigation.physicalGatesOpened -or $investigation.physicalRootCauseEstablished -or $investigation.hardwareExecution -or -not $investigation.freshAuthorizationRequired){throw 'P16 software concurrency correction must not claim physical qualification.'}
   foreach($unchanged in @('freshSafetyRetryMaximumAttemptsChanged','thermalThresholdsChanged','telemetryAgeLimitChanged','wmiQueriesChanged','fanWriteOrRestoreMechanismsChanged')){
     if([bool]$investigation.$unchanged){throw "P16 supersession correction changed $unchanged."}
   }
 }

 Assert-True ([bool]$d.hardeningRequired.required) 'P16B attempt-4 re-block must require lifecycle hardening.'
 Assert-True ([bool]$d.hardeningRequired.physicalGateMustRemainClosed) 'P16B attempt-4 lifecycle hardening must keep the physical gate closed.'
 $p4=$d.hardeningRequired.postAttempt4
 if($null -eq $p4 -or -not [bool]$p4.required -or
    [string]$p4.result -cne 'FAIL_CLOSED_NO_WRITE_POWER_TRANSITION_INTERRUPTION' -or
    -not [bool]$p4.attemptFenceClaimed -or -not [bool]$p4.initialFirmwareBaselinePassed -or
    -not [bool]$p4.manualModeHoldFirmwareObserved -or [bool]$p4.initialApplyObserved -or
    [bool]$p4.customOwnershipEstablished -or [bool]$p4.fanWriteObserved -or
    -not [bool]$p4.suspendObserved -or -not [bool]$p4.resumeObserved -or
    -not [bool]$p4.resumeHealthyFiveSnapshotsObserved -or
    -not [bool]$p4.postResumeFirmwareHoldObserved -or
    -not [bool]$p4.explicitGuiShutdownObserved -or [bool]$p4.failsafeTakeover -or
    -not [bool]$p4.watchdogIdentityStable -or [bool]$p4.powerTransitionCauseEstablished -or
    -not [bool]$p4.lifecycleInterruptionFenceRequired){
   throw 'P16B attempt-4 no-write power-transition findings are incomplete.'
 }
 Assert-False ([bool]$p4.hardwareExecutionByReblockCommit) 'P16B attempt-4 re-block commit must be software-only.'
 Assert-Contains $doc 'P16B attempt 4 — no-write suspend/resume interruption' 'P16B attempt-4 documentation missing.'
}

if($isAuthorized){
 Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P16B dedicated source gate must be open.'
 $auth=$d.authorization
 if($null -eq $auth){throw 'P16B authorization metadata missing.'}
 if([int]$auth.authorizationGeneration -ne 5 -or
    [string]$auth.openedFromSafetySupersessionCorrectionHead -cne 'fedad87a9dbcc8d50adf3615fade90945712cc4d' -or
    [string]$auth.basisTree -cne '7bf78e369cfb80ea740acb0af4b7ac908f7c8cd3' -or
    [int]$auth.basisCiRunNumber -ne 1211 -or [long]$auth.basisCiRunId -ne 37094404222 -or
    [string]$auth.basisCiResult -cne 'SUCCESS' -or [int]$auth.nextAttempt -ne 6){
   throw 'P16B generation-5 supersession correction authorization basis mismatch.'
 }
 $investigation=$d.hardeningRequired.softwareConcurrencyInvestigation
 $iv=$investigation.validation; $ic=$investigation.closure
 if($investigation.status -cne 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED' -or -not $investigation.ciValidated -or -not $ic.closed -or $ic.result -cne 'PASS' -or
    $iv.head -cne $auth.openedFromSafetySupersessionCorrectionHead -or $iv.tree -cne $auth.basisTree -or [int]$iv.runNumber -ne 1211 -or [long]$iv.runId -ne 37094404222 -or $iv.result -cne 'SUCCESS' -or -not $iv.fullWindowsCi -or [int]$iv.completedSteps -ne 102 -or
    $ic.implementationHead -cne $iv.head -or $ic.implementationTree -cne $iv.tree -or [long]$ic.sourceCiRunId -ne [long]$iv.runId -or $ic.sourceCiResult -cne 'SUCCESS' -or
    $ic.hardwareExecution -or $ic.physicalRootCauseEstablished -or $ic.physicalGatesOpened -or $ic.installedM4WatchdogChanged -or -not $ic.separateAuthorizationHeadCiRequired){
   throw 'P16B generation-5 requires exact software-only supersession correction closure.'
 }
 Assert-True ([bool]$auth.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P16B requires same-head CI before target execution.'
 Assert-True ([bool]$auth.runtimeExactHeadCiBarrierRequired) 'P16 generation-5 requires runtime exact-head CI validation before target access.'
 Assert-False ([bool]$auth.authorizationCommitPerformsHardwareExecution) 'P16B authorization commit itself must perform no hardware execution.'
 Assert-True ([bool]$auth.oneShotAttemptFenceRequired) 'P16B generation-3 authorization must require the durable one-shot attempt fence.'
 Assert-False ([bool]$auth.authorizationConsumed) 'Fresh P16B authorization must be unconsumed before target execution.'
 Assert-False ([bool]$auth.freshAuthorizationRequired) 'Current P16B authorization is already the required fresh authorization.'
 Assert-False ([bool]$auth.permanentUserManualExecutionAuthorized) 'P16B must not promote permanent user Manual.'
 Assert-False ([bool]$auth.automaticExecutionAuthorized) 'P16B must not open Automatic.'
 Assert-False ([bool]$auth.candidateCurvePhysicallyValidated) 'P16B must not validate Candidate V1.'
 Assert-False ([bool]$auth.candidateCurveAuthorizedForProduction) 'P16B must not promote Candidate V1.'
 Assert-False ([bool]$auth.controlEnabledByDefault) 'P16B must keep default control disabled.'
 Assert-False ([bool]$d.hardeningRequired.required) 'Generation-3 authorization requires all outstanding hardening to be formally closed.'
 Assert-True ([bool]$d.hardeningRequired.implementation.ciValidated) 'Generation-3 authorization requires the original P16B hardening CI closure.'

 $hc=$d.hardeningRequired.closure
 if($null -eq $hc -or -not [bool]$hc.closed -or [string]$hc.result -cne 'PASS' -or
    [string]$hc.implementationHead -cne '6539671204d3e9c548d1d9b5b553920ccd553525' -or
    [int]$hc.sourceCiRunNumber -ne 1192 -or [long]$hc.sourceCiRunId -ne 37069306742 -or
    [string]$hc.sourceCiResult -cne 'SUCCESS'){
   throw 'P16B generation-3 authorization requires the original hardening closure.'
 }

 $p3=$d.hardeningRequired.postAttempt3
 $p3c=$p3.closure
 if($null -eq $p3c -or -not [bool]$p3c.closed -or [string]$p3c.result -cne 'PASS' -or
    [string]$p3c.implementationHead -cne '34c6d8d3a7245d0697f5803c74e2194fe89297f5' -or
    [int]$p3c.sourceCiRunNumber -ne 1196 -or [long]$p3c.sourceCiRunId -ne 37072759522 -or
    [string]$p3c.sourceCiResult -cne 'SUCCESS'){
   throw 'P16B generation-3 authorization requires the exact attempt-3 hardening closure.'
 }
 Assert-True ([bool]$p3.implementation.ciValidated) 'P16B generation-3 authorization requires CI-validated attempt-3 hardening.'
 Assert-False ([bool]$p3c.hardwareExecution) 'P16B generation-3 basis must remain software-only.'
 Assert-False ([bool]$p3c.installedM4WatchdogBinaryMutated) 'P16B generation-3 basis must preserve the installed M4 binary.'
 Assert-False ([bool]$p3c.installedM4WatchdogCoherenceBehaviorChanged) 'P16B generation-3 basis must preserve installed M4 runtime behavior.'

 $history=@($d.authorizationHistory)
 if($history.Count -lt 2){throw 'P16B generation-3 authorization must preserve both consumed prior authorization generations.'}
 if([int]$history[0].authorizationGeneration -ne 1 -or
    [string]$history[0].authorizationHead -cne '0eba7426455adcca2594a612abd2c5753a52d235' -or
    -not [bool]$history[0].authorizationConsumed -or [int]$history[0].targetAttemptCount -ne 2){
   throw 'P16B generation-1 consumed authorization history is missing or altered.'
 }
 if([int]$history[1].authorizationGeneration -ne 2 -or
    [string]$history[1].authorizationHead -cne '258d39cd5ab968b55983447f409f2210c7c1fc65' -or
    [int]$history[1].authorizationCiRunNumber -ne 1194 -or
    [long]$history[1].authorizationCiRunId -ne 37070246335 -or
    -not [bool]$history[1].authorizationConsumed -or
    [int]$history[1].targetAttemptCount -ne 1 -or
    [int]$history[1].consumedByAttempt -ne 3 -or
    -not [bool]$history[1].attemptFenceClaimed){
   throw 'P16B generation-2 consumed authorization history is missing or altered.'
 }
 $g3=@($d.authorizationHistory|Where-Object{[int]$_.authorizationGeneration -eq 3})
 if($g3.Count -ne 1 -or -not $g3[0].authorizationConsumed -or [int]$g3[0].consumedByAttempt -ne 4 -or $g3[0].attemptFenceAuthorizationHead -cne 'fdf08d94cddca82e3d1431bdf95e68c37aae24aa'){throw 'P16 generation-3 consumed history missing.'}
  $g4=@($d.authorizationHistory|Where-Object{[int]$_.authorizationGeneration -eq 4})
 if($g4.Count -ne 1 -or -not $g4[0].authorizationConsumed -or [int]$g4[0].consumedByAttempt -ne 5 -or $g4[0].authorizationHead -cne '98fdc73dbf1ac4f81812ae84f45047c9e1845703' -or [long]$g4[0].authorizationCiRunId -ne 37092681751 -or -not $g4[0].attemptFenceClaimed){throw 'P16 generation-4 consumed history missing.'}
 if($history.Count -ne 4 -or @($d.physicalAttemptHistory).Count -ne 5 -or @($d.physicalAttemptHistory|Where-Object{$_.result -cne 'FAIL_CLOSED'}).Count -ne 0){throw 'P16 generation-5 must preserve five failed-closed attempts and four consumed authorizations.'}
 if($d.physicalAttemptHistory[4].evidenceZipSha256 -cne '811fafd002311482e75b21dcfa24528fa05f458f913ba7a9a0afd72c00a3363f'){throw 'P16 attempt-5 evidence identity altered.'}
 Assert-Contains $doc 'P16 generation-5 controlled authorization after supersession correction' 'P16 generation-5 documentation missing.'
 Assert-Contains $doc 'P16 generation-4 controlled WMI authorization' 'P16 generation-4 documentation missing.'
}else{
 Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P16A source gate must remain hard-closed.'
}

foreach($needle in @('p16NormalManualQualificationAuthorized','Hp8C40P16NormalManualQualificationGate.PhysicalExecutionAuthorized','Hp8C40PostM9UserControlGate.ManualExecutionAuthorized ||','Hp8C40TargetProfile.Instance.Id')){Assert-Contains $main $needle ("P16 MainForm bridge missing: {0}" -f $needle)}
foreach($needle in @('_manualLevel.Minimum = 10;','_manualLevel.Maximum = 50;','await _controller.ApplyManualAsync(','P13 mode request {mode}: action={result.Action};','P13 manual request {level}/{level}: action={result.Action};')){Assert-Contains $surface $needle ("P16 P13 prerequisite missing: {0}" -f $needle)}
Assert-NotContains $program '--8c40-p16' 'P16 must not add special command-line mode.'
Assert-NotContains $program 'P16NormalManual' 'P16 must not add P16 Program hardware-test state.'

foreach($needle in @('HARD VERSIONED AUTHORIZATION BARRIER','P16B_NORMAL_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI',"expectedBranch='feature/victus-8c40-p16-normal-manual'",'Start-P15BTrackedChild -Executable $appExe','-Arguments @(''--modules-dir'',$modulesDir)','Recovery completed; telemetry is healthy after 3 complete snapshots','P13 UI: startup mode=Firmware; manualGate=True; automaticGate=False','P13 mode request Manual: action=HoldFirmware; authorized=True; authority=Firmware;','P13 manual request 30/30: action=EnterCustomAndApply; authorized=True; authority=Custom;','P13 manual request 40/40: action=ApplyChangedLevel; authorized=True; authority=Custom;','P13 mode request Firmware: action=RestoreFirmware; authorized=True; authority=Firmware;','Assert-StableSetpoint 30 30','Assert-StableSetpoint 40 40','Assert-OwnedJournal','30 3 $null','40 5 $ownedSessionId','30 7 $ownedSessionId','Assert-CausalServiceLog','Assert-AppInteractionAudit','Assert-StableSetpoint 255 255','Test-FailsafeTakeover')){Assert-Contains $harness $needle ("P16 harness contract missing: {0}" -f $needle)}
foreach($forbidden in @('SetFanLevel','--restore-hp-auto','git clean','Start-Service','Stop-Service','Restart-Service','--8c40-p16')){Assert-NotContains $harness $forbidden ("P16 parent harness forbidden bypass/special mode: {0}" -f $forbidden)}

foreach($needle in @('sourceEvidencePreserved=$true','gitCleanUsed=$false','p15-contract','p16-contract','qualification-gate-source','production-adapter-source','coordinator-source','reused-qualified-failsafe-source')){Assert-Contains $packager $needle ("P16 packager missing: {0}" -f $needle)}
foreach($needle in @('Test-P15D2QualifiedLevelPair','cpu -in @(30,40)','WRITE_ARMED','OWNED','RESTORING','P15D2 FAILSAFE ARMED:','P15D2 FAILSAFE TAKEOVER:')){Assert-Contains $failsafe $needle ("Reused P16 failsafe prerequisite missing: {0}" -f $needle)}

$ri=$d.runtimeImplementation
foreach($flag in @('normalAppGateBridgeImplemented','realP13PathReused','ordinaryAppLogAuditImplemented','initialHealthyRecoveryAuditImplemented','initial30ParentProofImplemented','changed40ParentProofImplemented','return30ParentProofImplemented','sameOwnedSessionAuditImplemented','causalThreeWriteWatchdogAuditImplemented','realFirmwareStrongRestoreAuditImplemented','cleanTrayExitAuditImplemented','nativeTrackedGuiProcess','independentFailsafeImplemented','independentFailsafeReused','evidencePackagerImplemented','evidencePackagingSelfTestImplemented')){Assert-True ([bool]$ri.$flag) ("P16 runtime flag missing: {0}" -f $flag)}
Assert-False ([bool]$ri.specialP16StartupModeIntroduced) 'P16 cannot introduce special startup mode.'
Assert-False ([bool]$ri.physicalExecution) 'P16A implementation must remain software-only.'
if([string]$ri.independentFailsafeSource -cne 'scripts/watchdog-p15d2-service-failsafe-8c40.ps1'){throw 'P16 reused failsafe source mismatch.'}

Assert-False ([bool]$p16.promotion.manualExecutionAuthorized) 'P16A must not promote permanent Manual.'
Assert-False ([bool]$p16.promotion.automaticMayOpen) 'P16 cannot open Automatic.'
Assert-False ([bool]$p16.promotion.candidateCurveMayBePromoted) 'P16 cannot promote Candidate V1.'
Assert-False ([bool]$p16.promotion.controlMayEnableByDefault) 'P16 cannot enable default control.'

Assert-Contains $workflow 'HP 8C40 P16A normal Manual preparation invariant' 'P16 preparation invariant must run in CI.'
Assert-Contains $workflow 'HP 8C40 P16A evidence packaging self-test' 'P16 package self-test must run in CI.'
Assert-Contains $doc 'P16A implementation staged after architecture CI' 'P16 implementation documentation missing.'

# Post-attempt-4 session hardening is software-only and cannot erase history.
$lifecycle=$d.hardeningRequired.postAttempt4.lifecycleImplementation
if($null -eq $lifecycle -or -not $lifecycle.softwareOnly -or $lifecycle.physicalGatesOpened){throw 'P16 lifecycle implementation scope missing.'}
foreach($flag in @('irreversibleSessionLatch','interruptBeforeTelemetryRecovery','resumeWithoutSuspendRejected','inFlightManualCancellation','firmwareReleaseRemainsAvailable','parentCausalAbort')){Assert-True ([bool]$lifecycle.$flag) ("P16 lifecycle guarantee missing: $flag")}
$latch=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40P16QualificationSession.cs') -Raw
$adapter=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\AdaptiveFanProductionController.cs') -Raw
Assert-Contains $latch 'Interlocked.CompareExchange' 'P16 latch must preserve first interruption.'
Assert-NotContains $latch 'Reset(' 'P16 latch cannot reset within the session.'
Assert-Contains $main 'InterruptP16QualificationSession' 'P16 ordinary app must observe power signals.'
Assert-Contains $main 'P16 QUALIFICATION INTERRUPTED:' 'P16 causal interruption log missing.'
Assert-Contains $adapter '_qualificationSession.InterruptionToken' 'P16 adapter must cancel queued/in-flight admission.'
Assert-Contains $hardeningHelper 'Assert-P16QualificationSessionUninterrupted' 'P16 parent causal abort missing.'
Assert-Contains $harness 'Assert-P16QualificationSessionUninterrupted -Lines $segment' 'P16 parent must inspect the entire current app-log session.'
if($lifecycle.status -ceq 'IMPLEMENTED_CI_PENDING_GATE_CLOSED'){
 Assert-False ([bool]$lifecycle.ciValidated) 'P16 lifecycle pending state cannot pre-claim CI.'
 Assert-False ([bool]$lifecycle.closure.closed) 'P16 lifecycle pending state cannot pre-claim closure.'
}elseif($lifecycle.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED'){
 Assert-True ([bool]$lifecycle.ciValidated) 'P16 lifecycle closure requires CI validation.'
 Assert-True ([bool]$lifecycle.closure.closed) 'P16 lifecycle formal closure missing.'
 $lc=$lifecycle.closure
 if($lc.result -cne 'PASS' -or $lc.sourceCiResult -cne 'SUCCESS' -or
    $lc.implementationHead -cne '86d6509bf4a71946067f0c34f53fdda976eec7c7' -or
    $lc.implementationTree -cne 'd6de39a35b591ef7eae1adbd50cab5159648127c' -or
    [int]$lc.sourceCiRunNumber -ne 1203 -or [long]$lc.sourceCiRunId -ne 37088402551 -or
    [int]$lc.deterministicSessionCases -ne 7 -or $lc.hardwareExecution -or $lc.physicalGatesOpened){throw 'P16 lifecycle closure CI identity missing.'}
}else{throw 'Unexpected P16 lifecycle implementation status.'}

foreach($needle in @('Assert-P16AuthorizationCiRun','Assert-P16WmiCommandProof','ExpectedSourceHead','ExpectedCiRunId','p16-authorization-ci.json')){Assert-Contains $harness $needle ("P16 WMI preparation barrier missing: $needle")}
Assert-Contains $main 'P16 WMI COMMAND PROOF:' 'P16 must preserve committed WMI proof in the ordinary app log.'
Assert-Contains $backend 'WmiCommandAcknowledged?.Invoke' 'P16 diagnostic command proof missing.'
$prep=$d.controlledWmiPreparation
if($null -eq $prep -or $prep.baselineHead -cne '572ab08c63cdeb0a0ad3acc59076ca43b9acd758' -or [long]$prep.baselineCiRunId -ne 37090835006 -or $prep.baselineCiResult -cne 'SUCCESS' -or $prep.hardwareExecution -or $prep.physicalGatesOpened -or -not $prep.exactHeadCiRuntimeBarrier -or -not $prep.committedWmiCommandProofAudit){throw 'P16 controlled WMI preparation scope mismatch.'}
if($prep.status -ceq 'IMPLEMENTED_CI_PENDING_GATE_CLOSED'){
 if($null -ne $prep.validation -or $isAuthorized){throw 'P16 WMI preparation cannot preclaim CI or open a pending gate.'}
}elseif($prep.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED'){
 if($prep.validation.result -cne 'SUCCESS' -or -not $prep.validation.fullWindowsCi -or $prep.validation.head -cne 'b2d3040a1fa58b489327e93738da316d9521fd14' -or [long]$prep.validation.runId -ne 37092356506 -or [int]$prep.validation.runNumber -ne 1208){throw 'P16 WMI preparation requires exact full successful Windows CI.'}
}else{throw 'Unexpected P16 WMI preparation state.'}

Write-Host ("PASS: P16 normal-Manual state '{0}' preserves the P15/permanent-Manual/Automatic/Candidate boundaries." -f $status) -ForegroundColor Green
