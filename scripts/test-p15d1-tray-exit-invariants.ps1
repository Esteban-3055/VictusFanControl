$ErrorActionPreference='Stop'

$root=Split-Path -Parent $PSScriptRoot
$contract=Get-Content -LiteralPath (Join-Path $root 'release\p15-target-checkpoint.json') -Raw | ConvertFrom-Json
$main=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\MainForm.cs') -Raw
$coordinator=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\FanControlCoordinator.cs') -Raw
$gate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl.App\P15D1TrayExitQualification.cs') -Raw
$userGate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40PostM9UserControlGate.cs') -Raw
$candidate=Get-Content -LiteralPath (Join-Path $root 'src\VictusFanControl\Control\Adaptive\Hp8C40AdaptiveCandidateV1.cs') -Raw

function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}
function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}}

if([string]$contract.status -cne 'P15D1_TRAY_EXIT_PREPARATION_CI_PENDING_GATE_CLOSED'){
    throw "Unexpected P15D1 preparation status: $($contract.status)"
}

$d=$contract.guiLifecycleTrayExit
Assert-True ([bool]$contract.guiManual.physicalPassed) 'P15D1 requires P15C physical PASS.'
Assert-True ([bool]$contract.guiManual.evidenceClosed) 'P15D1 requires P15C evidence closure.'
Assert-False ([bool]$contract.guiManual.executionAuthorized) 'P15C execution must remain re-blocked.'
Assert-False ([bool]$contract.guiManual.controllerPhysicalExecutionAuthorized) 'P15C controller gate must remain re-blocked.'

Assert-True ([bool]$d.preparationImplemented) 'P15D1 preparation must be implemented.'
Assert-False ([bool]$d.preparationCiValidated) 'P15D1 preparation must not pre-claim CI.'
Assert-False ([bool]$d.preparationClosure.closed) 'P15D1 preparation must not pre-close CI.'
Assert-False ([bool]$d.executionAuthorized) 'P15D1 physical execution must remain closed during preparation.'
Assert-False ([bool]$d.controllerPhysicalExecutionAuthorized) 'P15D1 GUI/controller execution must remain closed during preparation.'
Assert-False ([bool]$d.physicalPassed) 'P15D1 cannot pre-claim physical PASS.'
Assert-False ([bool]$d.evidenceClosed) 'P15D1 cannot pre-close physical evidence.'

Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15D1 dedicated source gate must be hard-closed.'
Assert-Contains $gate 'RequiredToken = "8C40-P15D1-TRAYEXIT30"' 'P15D1 token mismatch.'
Assert-Contains $gate 'QualificationLevel = 30' 'P15D1 level must remain 30.'
Assert-Contains $gate 'NormalUserExecutionGatesClosed()' 'P15D1 must assert normal user gate isolation.'

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

foreach($forbidden in @('SetFanLevel','--restore-hp-auto','Start-Service','Stop-Service','Restart-Service','git clean')){
    Assert-NotContains $MyInvocation.MyCommand.Path $forbidden ("P15D1 invariant script must remain software-only/read-only: {0}" -f $forbidden)
}

Write-Host 'PASS: P15D1 tray-exit lifecycle preparation is hard-closed and current production shutdown ordering is statically preserved.' -ForegroundColor Green
