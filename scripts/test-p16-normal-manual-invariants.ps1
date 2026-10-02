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

$status=[string]$p16.status
if($status -notin @('P16A_NORMAL_MANUAL_IMPLEMENTATION_CI_PENDING_GATE_CLOSED','P16A_NORMAL_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED')){throw "Unexpected P16A status: $status"}
$isClosed=($status -eq 'P16A_NORMAL_MANUAL_PREPARATION_CI_PASS_GATE_CLOSED')

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

$d=$p16.normalManual
Assert-True ([bool]$d.architectureImplemented) 'P16A architecture missing.'
Assert-True ([bool]$d.architectureCiValidated) 'P16A requires architecture CI PASS.'
$a=$d.architectureValidation
if([string]$a.head -cne '8fbe8aba0dc892f0fba988246164a13357325a7c' -or [int]$a.runNumber -ne 1182 -or [long]$a.runId -ne 37057172456 -or [string]$a.result -cne 'SUCCESS'){throw 'P16A architecture CI identity mismatch.'}
Assert-True ([bool]$d.preparationImplemented) 'P16A full preparation must be implemented.'
if($isClosed){Assert-True ([bool]$d.preparationCiValidated) 'P16A closed state requires preparation CI validation.';Assert-True ([bool]$d.preparationClosure.closed) 'P16A closed state requires formal preparation closure.'}else{Assert-False ([bool]$d.preparationCiValidated) 'P16A pending implementation cannot pre-claim CI.';Assert-False ([bool]$d.preparationClosure.closed) 'P16A pending implementation cannot pre-close.'}
Assert-False ([bool]$d.executionAuthorized) 'P16A parent gate must remain closed.'
Assert-False ([bool]$d.controllerPhysicalExecutionAuthorized) 'P16A source gate must remain closed.'
Assert-False ([bool]$d.physicalGatesOpened) 'P16A physical gates must remain closed.'
Assert-False ([bool]$d.physicalPassed) 'P16A cannot pre-claim physical PASS.'
Assert-False ([bool]$d.evidenceClosed) 'P16A cannot pre-close physical evidence.'
Assert-False ([bool]$d.hardwareExecution) 'P16A must remain software-only.'
Assert-True ([bool]$d.normalApplicationLaunchRequired) 'P16 must use normal app launch.'
Assert-False ([bool]$d.specialQualificationStartupModeAllowed) 'P16 must not introduce special app startup mode.'
Assert-True ([bool]$d.requiresRealP13Surface) 'P16 must reuse P13.'
Assert-True ([bool]$d.requiresProductionAdapter) 'P16 must reuse production adapter.'
Assert-True ([bool]$d.requiresProductionCoordinator) 'P16 must reuse coordinator.'
Assert-True ([bool]$d.requiresProductionWatchdog) 'P16 must reuse production watchdog.'
Assert-True ([bool]$d.requiresAppLogInteractionAudit) 'P16 must audit normal app log.'
Assert-True ([bool]$d.requiresStrongRestore) 'P16 must prove strong restore.'
Assert-True ([bool]$d.requiresCleanTrayExit) 'P16 must prove tray Exit.'
if([string]$d.requiredToken -cne '8C40-P16-NORMAL-MANUAL-30-40-30'){throw 'P16 token mismatch.'}
if([int]$d.initialLevel -ne 30 -or [int]$d.changedLevel -ne 40 -or [int]$d.returnLevel -ne 30){throw 'P16 levels mismatch.'}
if([int]$d.exactManualModeRequests -ne 1 -or [int]$d.exactApplyManualCalls -ne 3 -or [int]$d.exactFirmwareModeRequests -ne 1 -or [int]$d.exactAutomaticModeRequests -ne 0){throw 'P16 interaction counts mismatch.'}
$g=@($d.expectedOwnedGenerations);if($g.Count -ne 3 -or [int]$g[0] -ne 3 -or [int]$g[1] -ne 5 -or [int]$g[2] -ne 7){throw 'P16 generation contract must be 3/5/7.'}

Assert-False ([bool]$p16.safetyBoundary.controlEnabledByDefault) 'P16 must keep default control OFF.'
Assert-False ([bool]$p16.safetyBoundary.automaticPolicyEnabled) 'P16 must keep automatic policy OFF.'
Assert-False ([bool]$p16.safetyBoundary.permanentUserManualExecutionAuthorized) 'P16A must keep permanent Manual false.'
Assert-False ([bool]$p16.safetyBoundary.automaticExecutionAuthorized) 'P16 must keep Automatic false.'
Assert-False ([bool]$p16.safetyBoundary.candidateCurvePhysicallyValidated) 'P16 must keep Candidate V1 unvalidated.'
Assert-False ([bool]$p16.safetyBoundary.candidateCurveAuthorizedForProduction) 'P16 must keep Candidate V1 unpromoted.'
Assert-False ([bool]$profile.control.enabledByDefault) 'P16 must keep profile control disabled by default.'
Assert-Contains $userGate 'ManualExecutionAuthorized = false' 'P16A permanent Manual compile gate must remain false.'
Assert-Contains $userGate 'AutomaticExecutionAuthorized = false' 'P16 Automatic compile gate must remain false.'
Assert-Contains $candidate 'PhysicallyValidated = false' 'P16 Candidate V1 physical false required.'
Assert-Contains $candidate 'AuthorizedForProduction = false' 'P16 Candidate V1 production false required.'
Assert-Contains $gate 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P16A source gate must remain hard-closed.'

foreach($needle in @('p16NormalManualQualificationAuthorized','Hp8C40P16NormalManualQualificationGate.PhysicalExecutionAuthorized','Hp8C40PostM9UserControlGate.ManualExecutionAuthorized ||','Hp8C40TargetProfile.Instance.Id')){Assert-Contains $main $needle ("P16 MainForm bridge missing: {0}" -f $needle)}
foreach($needle in @('_manualLevel.Minimum = 10;','_manualLevel.Maximum = 50;','await _controller.ApplyManualAsync(','P13 mode request {mode}: action={result.Action};','P13 manual request {level}/{level}: action={result.Action};')){Assert-Contains $surface $needle ("P16 P13 prerequisite missing: {0}" -f $needle)}
Assert-NotContains $program '--8c40-p16' 'P16 must not add special command-line mode.'
Assert-NotContains $program 'P16NormalManual' 'P16 must not add P16 Program hardware-test state.'

foreach($needle in @('HARD VERSIONED AUTHORIZATION BARRIER','P16B_NORMAL_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI',"expectedBranch='feature/victus-8c40-p16-normal-manual'","Start-P15BTrackedChild -Executable $appExe -Arguments @('--modules-dir',$modulesDir)",'Recovery completed; telemetry is healthy after 3 complete snapshots','P13 UI: startup mode=Firmware; manualGate=True; automaticGate=False.','P13 mode request Manual: action=HoldFirmware; authorized=True; authority=Firmware;','P13 manual request 30/30: action=EnterCustomAndApply; authorized=True; authority=Custom;','P13 manual request 40/40: action=ApplyChangedLevel; authorized=True; authority=Custom;','P13 mode request Firmware: action=RestoreFirmware; authorized=True; authority=Firmware;','Assert-StableSetpoint 30 30','Assert-StableSetpoint 40 40','Assert-OwnedJournal','30 3 $null','40 5 $ownedSessionId','30 7 $ownedSessionId','Assert-CausalServiceLog','Assert-AppInteractionAudit','Assert-StableSetpoint 255 255','Test-FailsafeTakeover')){Assert-Contains $harness $needle ("P16 harness contract missing: {0}" -f $needle)}
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

Write-Host 'PASS: P16A full software preparation is hard-closed, normal-app based, and preserves P15/Automatic/Candidate boundaries.' -ForegroundColor Green
