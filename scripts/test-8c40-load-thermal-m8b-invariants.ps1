$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot

$harness=Get-Content (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8b.ps1') -Raw
$failsafe=Get-Content (Join-Path $PSScriptRoot 'watchdog-m8b-service-failsafe-8c40.ps1') -Raw
$source=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8BWatchdogLoadQualificationTest.cs') -Raw
$cli=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}
}

function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}
}

function Assert-Equal($Actual,$Expected,[string]$Message){
    if($Actual -ne $Expected){
        throw ("{0} Expected='{1}' Actual='{2}'" -f $Message,$Expected,$Actual)
    }
}

function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}

function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}

Assert-Contains $harness 'M8B WATCHDOG-BACKED REPRESENTATIVE LOAD 50/50' 'M8B harness identity missing.'
Assert-Contains $harness 'test-8c40-load-thermal-m8-preflight.ps1' 'M8B must rerun the versioned M8 no-write preflight.'
Assert-Contains $harness 'test-8c40-load-thermal-m8b-invariants.ps1' 'M8B must run its own invariant before the physical boundary.'
Assert-Contains $harness 'type exactly $token' 'M8B must require an explicit operator token after normal load is active.'
Assert-Contains $harness "m8b.physicalExecutionAuthorized" 'M8B hardware harness must enforce versioned physical-execution authorization before service/failsafe/controller launch.'
Assert-Contains $harness "Start-M8BTrackedChild" 'M8B must launch controller with a directly owned native process instead of Start-Process -PassThru.'
Assert-Contains $harness "Wait-M8BTrackedChildExitCode" 'M8B must retain native PID/ExitCode evidence across READY and final exit.'
Assert-Contains $harness "m8b-tracked-child.ps1" 'M8B must source the versioned native process helper.'
Assert-NotContains $harness '$controller=Start-Process' 'M8B controller must not use PowerShell Start-Process -PassThru.'
Assert-Contains $harness 'Start-M8BFailsafe' 'M8B must arm an independent delayed failsafe.'
Assert-Contains $harness 'Start-Sleep -Seconds 5' 'M8B requires a fixed no-write post-token game refocus grace.'
Assert-Contains $harness 'Return to active gameplay/rendering NOW' 'M8B must tell operator to foreground normal rendering.'
Assert-Contains $harness '$earlyResult.FailureReason' 'Early controller failure must surface preserved durable result reason.'
Assert-Contains $harness 'result evidence unavailable' 'Early controller failure must report missing evidence explicitly.'
Assert-Contains $harness 'ARM INDEPENDENT FAILSAFE BEFORE WRITE-CAPABLE CONTROLLER' 'Failsafe ordering must be explicit.'
Assert-Contains $harness '--8c40-m8b-watchdog-load' 'M8B harness must launch the dedicated controller mode.'
Assert-Contains $harness 'Assert-M8BOwnedJournal' 'M8B must externally bind schema-v2 OWNED evidence to exact controller identity.'
Assert-Contains $harness 'Assert-CausalServiceLog' 'M8B must prove the watchdog causal chain from service logs.'
Assert-Contains $harness 'PREPARE -> WRITE_INTENT(50/50) -> COMMIT(50/50) -> RESTORE_BEGIN -> RELEASE' 'M8B causal chain text missing.'
Assert-Contains $harness 'M8B final' 'M8B must perform independent final FF/FF closure.'
Assert-Contains $harness 'Manual' 'M8B must restore/verify M4 Manual/stopped baseline.'
Assert-Contains $harness 'finalJournalAbsent' 'M8B summary must distinguish journal closure.'
Assert-Contains $harness 'finalFirmwareProofPass' 'M8B summary must distinguish FF/FF closure.'
Assert-Contains $harness 'finalServiceBaselinePass' 'M8B summary must distinguish Manual/stopped closure.'
Assert-Contains $harness "M8B cleanup service baseline" 'M8B FAIL_CLOSED cleanup must print service closure evidence.'
Assert-Contains $harness 'failsafePid=$failsafePid' 'M8B summary must persist the independent failsafe PID.'
Assert-Contains $harness 'failsafeLogPresent=(Test-Path $failsafeLog)' 'M8B summary must persist whether failsafe evidence exists.'
Assert-Contains $harness 'M8B independent failsafe did not publish ARMED evidence before controller launch.' 'M8B parent must bind controller launch to a durable failsafe ARMED marker.'
$trackedHelper=Get-Content (Join-Path $PSScriptRoot 'm8b-tracked-child.ps1') -Raw
$trackedSelfTest=Get-Content (Join-Path $PSScriptRoot 'test-8c40-m8b-tracked-child-selftest.ps1') -Raw
Assert-Contains $trackedHelper 'New-Object System.Diagnostics.Process' 'M8B helper must own the native process object.'
Assert-Contains $trackedHelper '$process.Start()' 'M8B helper must start its own native process instance.'
Assert-Contains $trackedHelper '$Process.WaitForExit($Seconds*1000)' 'M8B process wait must remain bounded.'
Assert-Contains $trackedHelper '$Process.ExitCode' 'M8B process exit code must be read from owned native process instance.'
Assert-Contains $trackedHelper 'ExitCode remains unavailable' 'M8B process exit code unavailability must remain fail-closed.'
Assert-Contains $trackedSelfTest 'foreach($expected in @(0,7))' 'M8B helper must be tested for both successful and nonzero child exits.'
Assert-Contains $harness 'The journal was not deleted.' 'M8B must preserve retained ownership evidence.'

$tokenIndex=$harness.IndexOf('$confirm=Read-Host "When the workload is active, type exactly $token"',[StringComparison]::Ordinal)
$refocusIndex=$harness.IndexOf('Start-Sleep -Seconds 5',[StringComparison]::Ordinal)
$watchdogStartIndex=$harness.IndexOf('    Start-Service -Name $serviceName',[StringComparison]::Ordinal)
if($tokenIndex -lt 0 -or $refocusIndex -le $tokenIndex -or $watchdogStartIndex -le $refocusIndex){
    throw 'M8B refocus grace must occur after operator token and before starting M4/watchdog or any write-capable controller.'
}

$armIndex=$harness.IndexOf('$failsafe=Start-M8BFailsafe',[StringComparison]::Ordinal)
$controllerIndex=$harness.IndexOf('$controller=Start-M8BTrackedChild',[StringComparison]::Ordinal)

if($armIndex -lt 0 -or $controllerIndex -lt 0 -or $armIndex -ge $controllerIndex){
    throw 'M8B harness must arm the independent failsafe before launching the write-capable controller.'
}

foreach($forbidden in @(
    'SetFanLevel(',
    'RestoreFirmwareAuto',
    '--restore-hp-auto',
    'git clean',
    'Remove-Item'
)){
    Assert-NotContains $harness $forbidden ("M8B parent harness contains forbidden direct operation: {0}" -f $forbidden)
}

foreach($required in @(
    'RequiredToken = "8C40-M8B-LOAD50"',
    'QualificationLevel = 50',
    'MaximumPreWriteSamples = 10',
    'RequiredConsecutivePreWriteRepresentativeSamples = 3',
    'SupervisionSamples = 30',
    'MinimumRepresentativeSupervisionSamples = 22',
    'MinimumConsecutiveRepresentativeSupervisionSamples = 10',
    'SampleIntervalMilliseconds = 1000',
    'CpuHardAbortC =',
    'GpuPhysicalAbortC = 82.0',
    'Hp8C40ThermalEmergencyConfirmation',
    'NamedPipeFanControlWatchdogLeaseClient',
    'FanControlWatchdogLeaseContract.Hp8C40M4PipeName',
    'new Hp8C40FanControlBackend(',
    'new FanControlCoordinator(',
    'ReadExpectedEcEvidenceConfirmedAsync',
    'backend-ec+tachs+watchdog-owned',
    'NORMAL_RESTORE_BEGIN',
    'NORMAL_RESTORE_COMPLETE',
    'normalRestoreCompleted',
    'finalFirmwareOwned',
    'previousPreWriteTimestamp',
    'previousSupervisionTimestamp',
    'SUPERVISION_TELEMETRY_EPOCH_BEGIN',
    'AuthorityChanged +=',
    'AUTHORITY_TRANSITION',
    'SUPERVISION_HANDOFF',
    'lastCustomHandoffReason',
    'supervisionFailure',
    'SafetyGate reasons='
)){
    Assert-Contains $source $required ("M8B source invariant missing: {0}" -f $required)
}

$applyMatches=[regex]::Matches($source,'coordinator\.ApplyAsync\(')
if($applyMatches.Count -ne 1){
    throw "M8B source must contain exactly one coordinator.ApplyAsync call; observed $($applyMatches.Count)."
}

Assert-NotContains $source 'SetFanLevel(' 'M8B source must not bypass the backend with a direct SetFanLevel call.'
Assert-NotContains $source 'RestoreFirmwareAuto' 'M8B source must restore only through coordinator/backend ownership handling.'
Assert-NotContains $source 'while (true)' 'M8B source must remain bounded.'
Assert-Contains $source 'coordinator.EnforceSafetyAsync(' 'M8B must continuously supervise safety/ownership without retransmitting 50/50.'

foreach($required in @(
    'M8B FAILSAFE ARMED:',
    'M8B FAILSAFE TAKEOVER:',
    'M8B FAILSAFE CONTROLLER-KILL:',
    'WRITE_ARMED',
    'OWNED',
    'RESTORING',
    'Pending.Cpu -eq 50',
    'Owned.Cpu -eq 50',
    'PreviousOwned.Cpu -eq 50',
    'SchemaVersion',
    'TargetProfileId',
    'Controller.ProcessStartUtcTicks',
    'Wait-JournalGone',
    'Start-Service -Name $serviceName',
    'Restart-Service -Name $serviceName -Force'
)){
    Assert-Contains $failsafe $required ("M8B independent failsafe invariant missing: {0}" -f $required)
}

foreach($forbidden in @(
    'SetFanLevel(',
    'RestoreFirmwareAuto',
    'Hp8C40BiosFanControl',
    'Remove-Item',
    'git clean'
)){
    Assert-NotContains $failsafe $forbidden ("M8B failsafe must not perform direct HP/fan/evidence mutation: {0}" -f $forbidden)
}

Assert-Contains $cli '--8c40-m8b-watchdog-load' 'M8B CLI mode missing.'
Assert-Contains $cli '--8c40-m8b-token' 'M8B CLI token missing.'
Assert-Contains $cli '--8c40-m8b-ready-path' 'M8B CLI READY path missing.'
Assert-Contains $cli '--8c40-m8b-result-path' 'M8B CLI result path missing.'
Assert-Contains $program 'Hp8C40M8BWatchdogLoadQualificationTest.RunAsync' 'M8B Program route missing.'
Assert-Contains $program 'Hp8C40M8BWatchdogLoadQualificationTest.RequiredToken' 'M8B Program route must validate exact token.'
Assert-Contains (Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlCoordinatorSelfTest.cs') -Raw) 'backend ownership handoff reason is observable before restore' 'Coordinator self-test must prove backend handoff reasons remain observable.'

Assert-True ([bool]$profile.loadThermalM8Qualification.m8a.physicalPassed) 'M8B preparation requires closed M8A physical PASS.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8a.m8bAuthorized) 'M8B preparation requires M8A authorization.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.harnessScript 'scripts/test-8c40-load-thermal-m8b.ps1' 'M8B harness path changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.failsafeScript 'scripts/watchdog-m8b-service-failsafe-8c40.ps1' 'M8B failsafe path changed.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8b.physicalPassed) 'M8B child PASS/parent FAIL_CLOSED must not be promoted without independent causal evidence audit.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8b.physicalExecutionAuthorized) 'M8B must be physically blocked while retry-5 handoff diagnostics are being hardened.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[3].classification 'CONTROLLER_REPORTED_PASS_PARENT_EXITCODE_UNAVAILABLE' 'M8B attempt-4 controller/parent discrepancy classification changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[4].classification 'FAIL_CLOSED_BACKEND_SUPERVISION_HANDOFF_DETAIL_NOT_PERSISTED' 'M8B retry-5 diagnostic classification changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[4].evidenceHead 'fc31d42b7e2d06d7a52b34cb19e59f3d8e0dffe6' 'M8B retry-5 evidence HEAD changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[4].result 'FAIL_CLOSED' 'M8B retry-5 result changed.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8b.physicalAttempts[4].finalClosurePass) 'M8B retry-5 final closure evidence changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.diagnosticHardening.status 'CODE_PREPARED_CI_PENDING_PHYSICAL_BLOCKED' 'M8B diagnostic hardening status changed.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8b.diagnosticHardening.physicalExecutionAuthorized) 'M8B diagnostic hardening must not authorize hardware before CI.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.processExitHardening.status 'CODE_CI_PASS_RETRY5_EXECUTED' 'M8B tracked-child hardening/retry execution status changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.processExitHardening.ci.commit '1d672389ee2bc7980bdfd38aa56fbeb390567f32' 'M8B native-child helper CI commit changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.processExitHardening.ci.runNumber 801 'M8B native-child CI run changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[2].result 'FAIL_CLOSED_NO_WRITE' 'M8B attempt 3 must remain fail-closed/no-write.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[2].representativeSamples 2 'M8B attempt 3 qualifying sample count changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[2].maximumConsecutiveRepresentative 1 'M8B attempt 3 qualifying streak changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[2].classification 'EXPECTED_FAIL_CLOSED_INSUFFICIENT_CONSECUTIVE_GPU_LOAD_NO_WRITE' 'M8B attempt 3 no-write classification changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.refocusHardening.ci.commit '68e190dea8d3131fb74c87429e3ddbf32b2294c4' 'M8B post-token hardening CI SHA changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.refocusHardening.ci.runNumber 797 'M8B post-token hardening CI run changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[1].result 'FAIL_CLOSED_NO_WRITE' 'M8B attempt 2 must be preserved as no-write FAIL_CLOSED.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[1].representativeSamples 0 'M8B attempt 2 observed zero representative samples.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.refocusHardening.graceSeconds 5 'M8B refocus grace changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[0].evidenceHead '68750bdd997e9530c5712af5c33e5bf2910a0f55' 'M8B attempt-1 evidence HEAD changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[0].result 'FAIL_CLOSED' 'M8B attempt-1 result must remain FAIL_CLOSED.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.telemetryEpochHardening.ci.finalCommit '7b5b104c9cd3c1f695f217693043383299410ec2' 'M8B telemetry-epoch hardening CI commit changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.telemetryEpochHardening.ci.runNumber 726 'M8B telemetry-epoch hardening CI run changed.'
Assert-False ([bool]$profile.loadThermalM8Qualification.watchdogRecoveryValidated) 'M8B must not promote production watchdog recovery.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M8B must not enable automatic/adaptive policy.'

foreach($needle in @(
    'M8B code/spec preparation',
    'exactly one',
    '50/50',
    '3 consecutive',
    '30 samples',
    '22/30',
    'independent delayed failsafe',
    'WRITE_ARMED/OWNED/RESTORING',
    'PREPARE',
    'WRITE_INTENT',
    'COMMIT',
    'RESTORE_BEGIN',
    'RELEASE',
    'M8B attempt 1',
    '3.364 s',
    'continuity epoch after COMMIT/READY',
    'M8B physical attempt 2 is now authorized',
    'M8B physical attempt 2 - FAIL_CLOSED / NO-WRITE',
    '5-second no-write refocus grace',
    'M8B physical attempt 3 is now authorized',
    'M8B physical attempt 3 - FAIL_CLOSED / NO-WRITE',
    '2/10 representative samples',
    'maximum consecutive streak 1/3',
    'M8B attempt 4 remains blocked',
    'M8B physical attempt 4 - CONTROLLER PASS / PARENT FAIL_CLOSED',
    'ExitCode was unavailable',
    '30/30 representative',
    'Full causal chain and no-failsafe-takeover proof are still pending',
    'M8B physical retry 5 - explicitly authorized',
    'Attempt-4 retrospective chain recovered; retry 5 FAIL_CLOSED exposes a diagnostic gap',
    '27/27 representative consecutive supervision samples',
    'M8B FAILSAFE ARMED',
    'Further M8B physical execution is blocked',
    'M8C physical execution remains blocked',
    'Both PowerShell 7 and Windows PowerShell 5.1 exercised successful child exit code 0'
)){
    Assert-Contains $doc $needle ("M8B documentation invariant missing: {0}" -f $needle)
}

Write-Host 'HP 8C40 M8B watchdog-backed representative-load harness invariant self-test: PASS' -ForegroundColor Green
