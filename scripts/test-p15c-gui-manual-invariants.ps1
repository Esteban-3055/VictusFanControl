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
 'P15C_GUI_MANUAL_PHYSICAL_PASS_FORMALLY_CLOSED'
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
if($failedPreparation.Count -ne 1 -or
   [int]$failedPreparation[0].runNumber -ne 1117 -or
   [long]$failedPreparation[0].runId -ne 36941758398 -or
   [string]$failedPreparation[0].head -cne '33bb63828561ff88664f1896f76a8e2c95863698' -or
   [string]$failedPreparation[0].result -cne 'FAILURE' -or
   [string]$failedPreparation[0].failureStep -cne 'Build'){
    throw 'P15C failed preparation CI history mismatch.'
}
Assert-False ([bool]$failedPreparation[0].hardwareExecution) 'P15C failed preparation CI must record no hardware execution.'
Assert-False ([bool]$failedPreparation[0].physicalGatesOpened) 'P15C failed preparation CI must record physical gates closed.'
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
    Assert-False ([bool]$g.executionAuthorized) 'Closed P15C preparation must keep harness gate closed.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'Closed P15C preparation must keep GUI qualification gate closed.'
    Assert-False ([bool]$g.physicalPassed) 'P15C preparation closure cannot pre-claim physical PASS.'
    Assert-False ([bool]$g.evidenceClosed) 'P15C preparation closure cannot pre-close physical evidence.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15C preparation closure requires qualification source gate closed.'
}

if($status -eq 'P15C_GUI_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI'){
    Assert-True ([bool]$g.preparationCiValidated) 'P15C authorization requires CI-validated preparation.'
    Assert-True ([bool]$g.preparationClosure.closed) 'P15C authorization requires formally closed preparation.'
    Assert-True ([bool]$g.executionAuthorized) 'P15C authorization must open only the dedicated parent harness gate.'
    Assert-True ([bool]$g.controllerPhysicalExecutionAuthorized) 'P15C authorization must open only the dedicated GUI qualification gate.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P15C authorized state requires qualification source gate open.'
}

if($status -eq 'P15C_GUI_MANUAL_PHYSICAL_PASS_FORMALLY_CLOSED'){
    Assert-False ([bool]$g.executionAuthorized) 'P15C physical closure must re-block parent harness.'
    Assert-False ([bool]$g.controllerPhysicalExecutionAuthorized) 'P15C physical closure must re-block GUI qualification gate.'
    Assert-True ([bool]$g.physicalPassed) 'P15C physical closure must set physicalPassed.'
    Assert-True ([bool]$g.evidenceClosed) 'P15C physical closure must set evidenceClosed.'
    Assert-Contains $qualification 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15C physical closure requires qualification source gate closed.'
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
 'TryBeginControlInteraction(',
 'qualification pre-action fence rejected the interaction',
 'interactionObserver',
 'LastRestoreEvidence',
 'ParentOwnedVerifiedFileName'
)){Assert-Contains $main $needle ("P15C MainForm integration missing: {0}" -f $needle)}

foreach($needle in @(
 'manualInteractionReadyProvider',
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
