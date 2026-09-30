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
Assert-Contains $harness 'Start-M8BFailsafe' 'M8B must arm an independent delayed failsafe.'
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
Assert-Contains $harness "controller terminated but ExitCode was unavailable" 'M8B must fail closed if child ExitCode is unavailable.'
Assert-Contains $harness 'The journal was not deleted.' 'M8B must preserve retained ownership evidence.'

$armIndex=$harness.IndexOf('$failsafe=Start-M8BFailsafe',[StringComparison]::Ordinal)
$controllerIndex=$harness.IndexOf('$controller=Start-Process',[StringComparison]::Ordinal)

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
    'SUPERVISION_TELEMETRY_EPOCH_BEGIN'
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

Assert-True ([bool]$profile.loadThermalM8Qualification.m8a.physicalPassed) 'M8B preparation requires closed M8A physical PASS.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8a.m8bAuthorized) 'M8B preparation requires M8A authorization.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.harnessScript 'scripts/test-8c40-load-thermal-m8b.ps1' 'M8B harness path changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.failsafeScript 'scripts/watchdog-m8b-service-failsafe-8c40.ps1' 'M8B failsafe path changed.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8b.physicalPassed) 'M8B must remain physical-pending until real evidence is reviewed.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8b.physicalExecutionAuthorized) 'M8B retry must remain blocked until the telemetry-epoch patch passes CI.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[0].evidenceHead '68750bdd997e9530c5712af5c33e5bf2910a0f55' 'M8B attempt-1 evidence HEAD changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8b.physicalAttempts[0].result 'FAIL_CLOSED' 'M8B attempt-1 result must remain FAIL_CLOSED.'
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
    'telemetry continuity epoch',
    'M8B physical retry remains blocked'
)){
    Assert-Contains $doc $needle ("M8B documentation invariant missing: {0}" -f $needle)
}

Write-Host 'HP 8C40 M8B watchdog-backed representative-load harness invariant self-test: PASS' -ForegroundColor Green
