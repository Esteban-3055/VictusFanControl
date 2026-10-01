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

if([string]$contract.status -cne 'P15B_MANUAL30_PREPARATION_CI_PENDING_GATE_CLOSED'){throw 'P15B preparation state mismatch.'}
Assert-True ([bool]$contract.startupNoWrite.physicalPassed) 'P15B requires P15A physical PASS.'
Assert-True ([bool]$contract.startupNoWrite.evidenceClosed) 'P15B requires P15A evidence closed.'
Assert-False ([bool]$contract.startupNoWrite.executionAuthorized) 'P15A must remain re-blocked.'
if([string]$contract.startupNoWrite.physicalPassClosure.evidenceZipSha256 -cne 'cd8e7dfe2fef365e75b886ccca1c6c101ce12030754f21a8f9bc7f305f76c24e'){throw 'P15B P15A evidence prerequisite mismatch.'}
Assert-True ([bool]$contract.manual30.preparationImplemented) 'P15B preparation must be implemented.'
Assert-False ([bool]$contract.manual30.preparationCiValidated) 'Pending P15B preparation must not pre-claim CI validation.'
Assert-False ([bool]$contract.manual30.preparationClosure.closed) 'Pending P15B preparation must not pre-close.'
Assert-False ([bool]$contract.manual30.executionAuthorized) 'P15B physical execution must remain CLOSED during preparation.'
Assert-False ([bool]$contract.manual30.controllerPhysicalExecutionAuthorized) 'P15B controller physical gate must remain CLOSED during preparation.'
Assert-False ([bool]$contract.manual30.physicalPassed) 'P15B cannot pre-claim physical PASS.'
Assert-False ([bool]$contract.manual30.evidenceClosed) 'P15B cannot pre-close physical evidence.'
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
Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'P15B qualification controller gate must remain closed during preparation.'
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
foreach($n in @('HARD VERSIONED AUTHORIZATION BARRIER','manual30.executionAuthorized','controllerPhysicalExecutionAuthorized','Assert-RepositoryProvenance','Assert-ExactTarget','Assert-ServiceBaseline','Assert-StableSetpoint 255 255','Start-Service -Name $serviceName','Start-P15BFailsafe','--8c40-p15b-manual30','Assert-OwnedJournal','Assert-StableSetpoint 30 30','P15B-CONTINUE','Assert-CausalServiceLog','Assert-StableSetpoint 255 255','Stop-Service -Name $serviceName','package-p15b-evidence.ps1','FAIL_CLOSED')){Assert-Contains $harness $n ("P15B harness invariant missing: {0}" -f $n)}
foreach($n in @('SetFanLevel(','--restore-hp-auto','git clean','Set-Service','New-Service','sc.exe ')){Assert-NotContains $harness $n ("P15B harness contains forbidden direct operation: {0}" -f $n)}
Assert-Contains $failsafe "TargetProfileId -cne 'HP-8C40-9D0R1LA-F18'" 'P15B failsafe must bind exact target.'
Assert-Contains $failsafe '[int]$Journal.Owned.Cpu -eq 30' 'P15B failsafe must bind exact 30/30 ownership.'
Assert-Contains $packager 'sourceEvidencePreserved=$true' 'P15B packager preservation marker missing.'
Assert-Contains $packager 'gitCleanUsed=$false' 'P15B packager must record no git clean.'
Assert-NotContains $packager 'git clean' 'P15B packager must never invoke git clean.'
Assert-Contains $cli '--8c40-p15b-manual30' 'P15B CLI switch missing.'
Assert-Contains $program 'Hp8C40P15BManual30QualificationTest.RunAsync' 'P15B Program dispatch missing.'
Write-Host 'HP 8C40 P15B Manual30 preparation invariant: PASS' -ForegroundColor Green
