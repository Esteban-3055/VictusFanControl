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
    'P15D1_TRAY_EXIT_IMPLEMENTATION_CI_PENDING_GATE_CLOSED',
    'P15D1_TRAY_EXIT_HARDENING_CI_PENDING_GATE_CLOSED',
    'P15D1_TRAY_EXIT_PREPARATION_CI_PASS_GATE_CLOSED',
    'P15D1_TRAY_EXIT_AUTHORIZED_AWAITING_SAME_HEAD_CI'
)){
    throw "Unexpected P15D1 preparation status: $($contract.status)"
}

$d=$contract.guiLifecycleTrayExit
Assert-True ([bool]$contract.guiManual.physicalPassed) 'P15D1 requires P15C physical PASS.'
Assert-True ([bool]$contract.guiManual.evidenceClosed) 'P15D1 requires P15C evidence closure.'
Assert-False ([bool]$contract.guiManual.executionAuthorized) 'P15C execution must remain re-blocked.'
Assert-False ([bool]$contract.guiManual.controllerPhysicalExecutionAuthorized) 'P15C controller gate must remain re-blocked.'

Assert-True ([bool]$d.preparationImplemented) 'P15D1 preparation must be implemented.'
if([string]$contract.status -in @('P15D1_TRAY_EXIT_HARDENING_CI_PENDING_GATE_CLOSED','P15D1_TRAY_EXIT_PREPARATION_CI_PASS_GATE_CLOSED','P15D1_TRAY_EXIT_AUTHORIZED_AWAITING_SAME_HEAD_CI')){
    Assert-True ([bool]$d.preparationCiValidated) 'P15D1 hardening/closure requires validated software CI.'
}else{
    Assert-False ([bool]$d.preparationCiValidated) 'Pending P15D1 preparation must not pre-claim CI.'
}
if([string]$contract.status -in @('P15D1_TRAY_EXIT_IMPLEMENTATION_CI_PENDING_GATE_CLOSED','P15D1_TRAY_EXIT_HARDENING_CI_PENDING_GATE_CLOSED')){
    Assert-True ([bool]$d.runtimeImplementationComplete) 'P15D1 runtime implementation must be complete.'
    Assert-False ([bool]$d.runtimeImplementationCiValidated) 'P15D1 final runtime implementation must not pre-claim CI.'
}
if([string]$contract.status -eq 'P15D1_TRAY_EXIT_HARDENING_CI_PENDING_GATE_CLOSED'){
    Assert-True ([bool]$d.preparationCiValidated) 'P15D1 hardening requires the #1131 software implementation CI baseline.'
    Assert-False ([bool]$d.preparationClosure.closed) 'P15D1 hardening must reopen software closure after failed #1132.'
    $base=$d.runtimeImplementationBaseCi
    if([string]$base.head -cne '7ac16fbc824fc992ce436ff68cc4ad7cea100c96' -or
       [int]$base.runNumber -ne 1131 -or
       [long]$base.runId -ne 36966420676 -or
       [string]$base.result -cne 'SUCCESS'){
        throw 'P15D1 hardening base CI identity mismatch.'
    }
    $h=$d.hardeningReview
    Assert-True ([bool]$h.required) 'P15D1 hardening review must be required.'
    Assert-True ([bool]$h.implementationComplete) 'P15D1 hardening implementation must be complete.'
    Assert-False ([bool]$h.ciValidated) 'P15D1 hardening must not pre-claim CI.'
    Assert-False ([bool]$h.hardwareExecution) 'P15D1 hardening must remain software-only.'
    Assert-False ([bool]$h.physicalGatesOpened) 'P15D1 hardening must keep physical gates closed.'
}
if([string]$contract.status -in @('P15D1_TRAY_EXIT_PREPARATION_CI_PASS_GATE_CLOSED','P15D1_TRAY_EXIT_AUTHORIZED_AWAITING_SAME_HEAD_CI')){
    Assert-True ([bool]$d.runtimeImplementationCiValidated) 'P15D1 formal closure requires final runtime implementation CI.'
    Assert-True ([bool]$d.preparationClosure.closed) 'P15D1 formal closure must be closed.'
    $pc=$d.preparationClosure
    if([string]$pc.result -cne 'PASS' -or
       [string]$pc.implementationHead -cne '67bf967f06b41869ca9113e6844f0a99721b8646' -or
       [int]$pc.sourceCiRunNumber -ne 1134 -or
       [long]$pc.sourceCiRunId -ne 36967539573 -or
       [string]$pc.sourceCiResult -cne 'SUCCESS'){
        throw 'P15D1 formal preparation closure CI identity mismatch.'
    }
    foreach($p in @('p15aClosureInvariantValidated','p15bClosureInvariantValidated','p15cClosureInvariantValidated','p15d1InvariantValidated','evidencePackagingSelfTestValidated','powerShell51CompatibilityValidated','warningsAsErrorsBuildValidated','runtimeWiringBuildValidated','hardeningValidated')){
        Assert-True ([bool]$pc.$p) ("P15D1 formal closure missing validation: {0}" -f $p)
    }
    Assert-False ([bool]$pc.hardwareExecution) 'P15D1 formal closure must record no hardware execution.'
    Assert-False ([bool]$pc.physicalGatesOpened) 'P15D1 formal closure must keep physical gates closed.'
    $hc=$d.hardeningReview.closure
    Assert-True ([bool]$d.hardeningReview.ciValidated) 'P15D1 formal closure requires hardening CI validation.'
    Assert-True ([bool]$hc.closed) 'P15D1 hardening closure must be closed.'
    if([string]$hc.result -cne 'PASS' -or [string]$hc.implementationHead -cne '67bf967f06b41869ca9113e6844f0a99721b8646' -or [int]$hc.sourceCiRunNumber -ne 1134 -or [long]$hc.sourceCiRunId -ne 36967539573 -or [string]$hc.sourceCiResult -cne 'SUCCESS'){
        throw 'P15D1 hardening closure CI identity mismatch.'
    }
}else{
    Assert-False ([bool]$d.preparationClosure.closed) 'P15D1 preparation must not pre-close CI.'
}
$failed=@($d.preparationClosure.failedCiHistoryPreserved)
if($failed.Count -ne 3 -or
   [int]$failed[0].runNumber -ne 1129 -or
   [long]$failed[0].runId -ne 36964880389 -or
   [string]$failed[0].head -cne 'ff68a44ff5e11a65890005ae281a5ef26d510d53' -or
   [string]$failed[0].result -cne 'FAILURE' -or
   [string]$failed[0].failureStep -cne 'HP 8C40 P15A startup no-write preparation invariant' -or
   [int]$failed[1].runNumber -ne 1132 -or
   [long]$failed[1].runId -ne 36966794012 -or
   [string]$failed[1].head -cne 'b5270dd594cca53c58595f89242c267908494eaf' -or
   [string]$failed[1].result -cne 'FAILURE' -or
   [string]$failed[1].failureStep -cne 'PowerShell syntax check' -or
   [int]$failed[2].runNumber -ne 1133 -or
   [long]$failed[2].runId -ne 36967229515 -or
   [string]$failed[2].head -cne '1abc160f173bd21a967e0a853bd190b04bd6809b' -or
   [string]$failed[2].result -cne 'FAILURE' -or
   [string]$failed[2].failureStep -cne 'HP 8C40 P15D1 tray-exit lifecycle preparation invariant'){
    throw 'P15D1 failed-CI history mismatch.'
}
foreach($entry in $failed){
    Assert-False ([bool]$entry.hardwareExecution) 'P15D1 failed CI must record no hardware execution.'
    Assert-False ([bool]$entry.physicalGatesOpened) 'P15D1 failed CI must record physical gates closed.'
}
if([string]$contract.status -eq 'P15D1_TRAY_EXIT_AUTHORIZED_AWAITING_SAME_HEAD_CI'){
    Assert-True ([bool]$d.executionAuthorized) 'P15D1 authorization must open the dedicated parent harness gate.'
    Assert-True ([bool]$d.controllerPhysicalExecutionAuthorized) 'P15D1 authorization must open the dedicated GUI gate.'
    Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P15D1 authorization requires the dedicated source gate open.'
    $a=$d.authorization
    Assert-True ([bool]$a.sameHeadCiSuccessRequiredBeforePhysicalExecution) 'P15D1 authorization must require same-head CI.'
    if([string]$a.preparationClosureHead -cne '2c784698a7a44afd17a8e02d05e69f0be4b09752' -or
       [int]$a.preparationClosureCiRunNumber -ne 1135 -or
       [long]$a.preparationClosureCiRunId -ne 36967880997 -or
       [string]$a.preparationClosureCiResult -cne 'SUCCESS' -or
       [string]$a.implementationHead -cne '67bf967f06b41869ca9113e6844f0a99721b8646' -or
       [int]$a.implementationCiRunNumber -ne 1134 -or
       [long]$a.implementationCiRunId -ne 36967539573 -or
       [string]$a.implementationCiResult -cne 'SUCCESS'){
        throw 'P15D1 authorization basis identity mismatch.'
    }
    if([string]$a.sourceGateScope -cne 'P15D1 tray-exit qualification only'){
        throw 'P15D1 authorization scope mismatch.'
    }
    foreach($p in @('p15aAuthorizationOpened','p15bAuthorizationOpened','p15cAuthorizationOpened','userFacingManualGateOpened','automaticAuthorizationOpened','candidateCurveAuthorizationOpened','m9cQualificationConstructionOpened','m9dQualificationConstructionOpened','hardwareExecutionAtAuthorizationCommit')){
        Assert-False ([bool]$a.$p) ("P15D1 authorization widened forbidden scope: {0}" -f $p)
    }
}else{
    Assert-False ([bool]$d.executionAuthorized) 'P15D1 physical execution must remain closed during preparation/closure.'
    Assert-False ([bool]$d.controllerPhysicalExecutionAuthorized) 'P15D1 GUI/controller execution must remain closed during preparation/closure.'
    Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15D1 dedicated source gate must be hard-closed.'
}
Assert-False ([bool]$d.physicalPassed) 'P15D1 cannot pre-claim physical PASS.'
Assert-False ([bool]$d.evidenceClosed) 'P15D1 cannot pre-close physical evidence.'
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

foreach($needle in @(
 'guiManual.physicalPassed',
 'guiManual.evidenceClosed',
 'guiManual.executionAuthorized',
 'guiManual.controllerPhysicalExecutionAuthorized',
 'test-p15d1-tray-exit-invariants.ps1',
 'test-p15c-gui-manual-invariants.ps1'
)){Assert-Contains $harness $needle ("P15D1 parent harness prerequisite/regression fence missing: {0}" -f $needle)}
Assert-NotContains $harness 'test-p15d1-gui-manual-invariants.ps1' 'P15D1 harness must not reference the nonexistent invariant filename.'
Assert-Contains $packager "role='parent-owned-proof'" 'P15D1 evidence packager must preserve the pre-hide parent OWNED proof.'
Assert-Contains $main 'Visible || ShowInTaskbar' 'P15D1 tray Exit must require the GUI to still be hidden.'
Assert-Contains $main 'Window was reopened after the qualified X/hide transition' 'P15D1 must fail closed if the hidden GUI is reopened before tray Exit.'

foreach($forbidden in @('SetFanLevel','--restore-hp-auto','git clean')){Assert-NotContains $harness $forbidden ("P15D1 parent harness forbidden bypass: {0}" -f $forbidden)}
Assert-NotContains $harness 'Start-Service' 'P15D1 parent harness must not manually start watchdog.'
Assert-NotContains $harness 'Stop-Service' 'P15D1 parent harness must not manually stop watchdog.'
Assert-NotContains $harness 'Restart-Service' 'P15D1 parent harness must not manually restart watchdog.'
if([string]$contract.status -eq 'P15D1_TRAY_EXIT_AUTHORIZED_AWAITING_SAME_HEAD_CI'){
    Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = true;' 'P15D1 authorized state requires source gate open.'
}else{
    Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15D1 non-authorized state requires source gate closed.'
}

Write-Host 'PASS: P15D1 tray-exit lifecycle preparation is hard-closed and current production shutdown ordering is statically preserved.' -ForegroundColor Green
