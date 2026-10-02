$ErrorActionPreference='Stop'

$root=Split-Path -Parent $PSScriptRoot
$contract=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json
$main=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$coordinator=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\FanControlCoordinator.cs') -Raw
$gate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P15D1TrayExitQualification.cs') -Raw
$userGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw
$program=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\Program.cs') -Raw
$surface=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P13FanControlSurface.cs') -Raw
$harness=Get-Content -LiteralPath (Join-Path $root 'scripts\test-p15d1-tray-exit.ps1') -Raw
$packager=Get-Content -LiteralPath (Join-Path $root 'scripts\package-p15d1-evidence.ps1') -Raw
$failsafe=Get-Content -LiteralPath (Join-Path $root 'scripts\watchdog-p15d1-service-failsafe-8c40.ps1') -Raw

function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}}

if([string]$contract.status -notin @(
    'P15D1_TRAY_EXIT_PREPARATION_CI_PENDING_GATE_CLOSED',
    'P15D1_TRAY_EXIT_IMPLEMENTATION_CI_PENDING_GATE_CLOSED'
)){
    throw "Unexpected P15D1 preparation status: $($contract.status)"
}

$d=$contract.guiLifecycleTrayExit
Assert-True ([bool]$contract.guiManual.physicalPassed) 'P15D1 requires P15C physical PASS.'
Assert-True ([bool]$contract.guiManual.evidenceClosed) 'P15D1 requires P15C evidence closure.'
Assert-False ([bool]$contract.guiManual.executionAuthorized) 'P15C execution must remain re-blocked.'
Assert-False ([bool]$contract.guiManual.controllerPhysicalExecutionAuthorized) 'P15C controller gate must remain re-blocked.'

Assert-True ([bool]$d.preparationImplemented) 'P15D1 preparation must be implemented.'
Assert-False ([bool]$d.preparationCiValidated) 'P15D1 preparation must not pre-claim CI.'
if([string]$contract.status -eq 'P15D1_TRAY_EXIT_IMPLEMENTATION_CI_PENDING_GATE_CLOSED'){
    Assert-True ([bool]$d.runtimeImplementationComplete) 'P15D1 runtime implementation must be complete.'
    Assert-False ([bool]$d.runtimeImplementationCiValidated) 'P15D1 runtime implementation must not pre-claim CI.'
}
Assert-False ([bool]$d.preparationClosure.closed) 'P15D1 preparation must not pre-close CI.'
$failed=@($d.preparationClosure.failedCiHistoryPreserved)
if($failed.Count -ne 1 -or
   [int]$failed[0].runNumber -ne 1129 -or
   [long]$failed[0].runId -ne 36964880389 -or
   [string]$failed[0].head -cne 'ff68a44ff5e11a65890005ae281a5ef26d510d53' -or
   [string]$failed[0].result -cne 'FAILURE' -or
   [string]$failed[0].failureStep -cne 'HP 8C40 P15A startup no-write preparation invariant'){
    throw 'P15D1 failed-CI history mismatch.'
}
Assert-False ([bool]$failed[0].hardwareExecution) 'P15D1 failed preparation CI must record no hardware execution.'
Assert-False ([bool]$failed[0].physicalGatesOpened) 'P15D1 failed preparation CI must record physical gates closed.'
Assert-False ([bool]$d.executionAuthorized) 'P15D1 physical execution must remain closed during preparation.'
Assert-False ([bool]$d.controllerPhysicalExecutionAuthorized) 'P15D1 GUI/controller execution must remain closed during preparation.'
Assert-False ([bool]$d.physicalPassed) 'P15D1 cannot pre-claim physical PASS.'
Assert-False ([bool]$d.evidenceClosed) 'P15D1 cannot pre-close physical evidence.'

Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15D1 dedicated source gate must be hard-closed.'
Assert-Contains $gate 'RequiredToken = "8C40-P15D1-TRAYEXIT30"' 'P15D1 token mismatch.'
Assert-Contains $gate 'QualificationLevel = 30' 'P15D1 level must remain 30.'
Assert-Contains $gate 'NormalUserExecutionGatesClosed()' 'P15D1 must assert normal user gate isolation.'
foreach($needle in @('--8c40-p15d1-tray-exit-test','--8c40-p15d1-test-token','--8c40-p15d1-marker-root','Hp8C40P15D1TrayExitQualificationGate.PhysicalExecutionAuthorized','Hp8C40P15D1TrayExitQualificationGate.NormalUserExecutionGatesClosed()')){Assert-Contains $program $needle ("P15D1 Program boundary missing: {0}" -f $needle)}
foreach($needle in @('TryPublishP15D1ReadyAsync','IsP15D1ControlInteractionAuthorized','OnP15D1ControlInteraction','P15D1WindowHiddenPath','P15D1TrayExitRequestedPath','P15D1ShutdownResultPath','HasP15D1ParentOwnedProofForCurrentProcess','HasP15D1ParentHiddenOwnedProofForCurrentProcess','PASS_TRAY_EXIT_STRONG_RESTORE')){Assert-Contains $main $needle ("P15D1 MainForm runtime wiring missing: {0}" -f $needle)}
foreach($needle in @('ApplyManualAsync(','_controlSafetyProvider,','qualification pre-action fence rejected the interaction')){Assert-Contains $surface $needle ("P15D1 hardened P13 surface marker missing: {0}" -f $needle)}
foreach($needle in @('Assert-StableSetpoint 30 30','Assert-OwnedJournal','P15D1-PARENT-OWNED-VERIFIED','P15D1-PARENT-HIDDEN-OWNED-VERIFIED','WINDOW_HIDDEN_TO_TRAY','TRAY_EXIT_REQUESTED','PASS_TRAY_EXIT_STRONG_RESTORE','Assert-CausalServiceLog','Assert-StableSetpoint 255 255','Test-FailsafeTakeover')){Assert-Contains $harness $needle ("P15D1 harness contract missing: {0}" -f $needle)}
foreach($needle in @('sourceEvidencePreserved=$true','gitCleanUsed=$false','coordinator-source','watchdog-log-segment','failsafe-source')){Assert-Contains $packager $needle ("P15D1 packager contract missing: {0}" -f $needle)}
foreach($needle in @('P15D1 FAILSAFE ARMED:','P15D1 FAILSAFE TAKEOVER:','P15D1 FAILSAFE CONTROLLER-KILL:','P15D1 FAILSAFE RECOVERED:')){Assert-Contains $failsafe $needle ("P15D1 failsafe contract missing: {0}" -f $needle)}

foreach($needle in @(
 'if (!_allowExit && e.CloseReason == CloseReason.UserClosing)',
 'e.Cancel = true;',
 'HideToTray();',
 '_allowExit = true;',
 'Close();',
 'Explicit application shutdown started.',
 'await _fanCoordinator.DisposeAsync();',
 'await _worker.DisposeAsync();'
)){
    Assert-Contains $main $needle ("P15D1 required real GUI lifecycle marker missing: {0}" -f $needle)
}

$coordinatorDispose=$coordinator.IndexOf('public async ValueTask DisposeAsync()', [StringComparison]::Ordinal)
$closeFence=$coordinator.IndexOf('_lifecycleFenceRequested = true;', $coordinatorDispose, [StringComparison]::Ordinal)
$cancel=$coordinator.IndexOf('CancelActiveCommand();', $coordinatorDispose, [StringComparison]::Ordinal)
$restore=$coordinator.IndexOf('await BestEffortRestoreLockedAsync(CancellationToken.None)', $coordinatorDispose, [StringComparison]::Ordinal)
$backendDispose=$coordinator.IndexOf('await _backend.DisposeAsync()', $coordinatorDispose, [StringComparison]::Ordinal)
if($coordinatorDispose -lt 0 -or -not ($coordinatorDispose -lt $closeFence -and $closeFence -lt $cancel -and $cancel -lt $restore -and $restore -lt $backendDispose)){
    throw 'P15D1 requires coordinator Dispose ordering: lifecycle fence -> cancel -> best-effort restore -> backend dispose.'
}

$shutdownStart=$main.IndexOf('Explicit application shutdown started.',[StringComparison]::Ordinal)
$coordDispose=$main.IndexOf('await _fanCoordinator.DisposeAsync();',$shutdownStart,[StringComparison]::Ordinal)
$workerDispose=$main.IndexOf('await _worker.DisposeAsync();',$shutdownStart,[StringComparison]::Ordinal)
if($shutdownStart -lt 0 -or -not ($shutdownStart -lt $coordDispose -and $coordDispose -lt $workerDispose)){
    throw 'P15D1 requires explicit GUI shutdown to dispose coordinator before telemetry worker.'
}

Assert-False ([bool]$contract.safetyBoundary.controlEnabledByDefault) 'P15D1 must keep default control OFF.'
Assert-False ([bool]$contract.safetyBoundary.automaticPolicyEnabled) 'P15D1 must keep automatic policy OFF.'
Assert-False ([bool]$contract.safetyBoundary.manualExecutionAuthorized) 'P15D1 must keep normal user Manual closed.'
Assert-False ([bool]$contract.safetyBoundary.automaticExecutionAuthorized) 'P15D1 must keep normal user Automatic closed.'
Assert-False ([bool]$contract.automatic.executionAuthorized) 'P15D1 must keep Automatic physical execution closed.'
Assert-False ([bool]$contract.safetyBoundary.candidateCurvePhysicallyValidated) 'P15D1 must not validate Candidate V1.'
Assert-False ([bool]$contract.safetyBoundary.candidateCurveAuthorizedForProduction) 'P15D1 must not authorize Candidate V1.'
Assert-False ([bool]$contract.safetyBoundary.m9cQualificationConstructionAuthorized) 'P15D1 must not reopen M9C.'
Assert-False ([bool]$contract.safetyBoundary.m9dQualificationConstructionAuthorized) 'P15D1 must not reopen M9D.'
Assert-Contains $userGate 'ManualExecutionAuthorized = false' 'P15D1 normal user Manual compile gate must remain false.'
Assert-Contains $userGate 'AutomaticExecutionAuthorized = false' 'P15D1 user Automatic compile gate must remain false.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'P15D1 Candidate V1 physical flag must remain false.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'P15D1 Candidate V1 production flag must remain false.'

foreach($forbidden in @('SetFanLevel','--restore-hp-auto','git clean')){Assert-NotContains $harness $forbidden ("P15D1 parent harness forbidden bypass: {0}" -f $forbidden)}
Assert-NotContains $harness 'Start-Service' 'P15D1 parent harness must not manually start watchdog.'
Assert-NotContains $harness 'Stop-Service' 'P15D1 parent harness must not manually stop watchdog.'
Assert-NotContains $harness 'Restart-Service' 'P15D1 parent harness must not manually restart watchdog.'
Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15D1 implementation CI must remain physically hard-closed.'

Write-Host 'PASS: P15D1 tray-exit lifecycle preparation is hard-closed and current production shutdown ordering is statically preserved.' -ForegroundColor Green
