$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot

$coordinator=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlCoordinatorSelfTest.cs') -Raw
$backend=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackendSelfTest.cs') -Raw
$m8c=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8CThermalPreemptionSelfTest.cs') -Raw
$m5d=Get-Content (Join-Path $PSScriptRoot 'test-watchdog-m5d-write-armed-invariants.ps1') -Raw
$m5e=Get-Content (Join-Path $PSScriptRoot 'test-watchdog-m5e-write-armed-double-death-invariants.ps1') -Raw
$workflow=Get-Content (Join-Path $repoRoot '.github\workflows\build.yml') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$audit=Get-Content (Join-Path $repoRoot 'docs\M8_PRODUCTION_RACE_AUDIT.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}

# 1. stale safety vs newer accepted result
foreach($needle in @(
    'TestStaleSafetyEvaluationCannotTearDownNewerSessionAsync',
    'TestStaleCommandSafetyCannotTearDownNewerSessionAsync'
)){
    Assert-Contains $coordinator $needle ("Race audit lost stale-safety coverage: {0}" -f $needle)
}

# 2. safety/thermal preemption vs in-flight ApplyAsync
Assert-Contains $coordinator 'TestSafetyPreemptsInFlightCommandAsync' 'Race audit lost generic ApplyAsync preemption coverage.'
Assert-Contains $m8c 'TestThermalPreemptionCancelsInFlightApplyAsync' 'Race audit lost exact M8C thermal ApplyAsync preemption coverage.'

# 3. lifecycle/admission fence vs command dispatch
Assert-Contains $coordinator 'TestLifecycleFenceClosesBeforeCoordinatorGateAsync' 'Race audit lost lifecycle fence coverage.'
Assert-Contains $backend 'TestCancellationAtPreDispatchPreventsWriteAsync' 'Race audit lost pre-dispatch cancellation coverage.'

# 4. watchdog liveness vs concurrent telemetry/EC failure
Assert-Contains $coordinator 'TestControlDependencyFailurePreemptsUnsafeSafetyAsync' 'Race audit lost watchdog-vs-telemetry coordinator coverage.'
Assert-Contains $backend 'TestWatchdogLossWinsConcurrentEcFailureAsync' 'Race audit lost watchdog-vs-EC backend coverage.'

# 5. external ownership before write and after durable WriteIntent
Assert-Contains $coordinator 'TestOwnershipConflictDoesNotClearExternalOverrideAsync' 'Race audit lost read-only admission ownership-conflict coverage.'
Assert-Contains $backend 'TestFirstCommandExternalOverrideIsNoWriteAsync' 'Race audit lost first-command external override coverage.'
Assert-Contains $backend 'TestWatchdogPostIntentExternalRaceAsync' 'Race audit lost post-WriteIntent external override coverage.'
Assert-Contains $backend 'TestWatchdogCancellationAfterIntentAbortsAsync' 'Race audit lost post-WriteIntent cancellation rollback coverage.'

# 6. transient/torn guard confirmation
Assert-Contains $backend 'TestStatusToleratesSingleGuardTransientAsync' 'Race audit lost one-sample guard transient coverage.'
Assert-Contains $backend 'TestStatusRejectsRepeatedGuardConflictAsync' 'Race audit lost repeated guard conflict coverage.'

# 7. restore/journal cleanup ordering
Assert-Contains $backend 'TestWatchdogCommitFailureRestoresAsync' 'Race audit lost post-write Commit failure restore coverage.'
Assert-Contains $backend 'TestWatchdogRestoreIpcFailureDoesNotBlockLocalRestoreAsync' 'Race audit lost local-restore-before-IPC dependency coverage.'
Assert-Contains $m5d 'WriteIntent -> WMI -> tach ACK -> hook -> Commit' 'Race audit lost M5D durable ordering invariant.'
Assert-Contains $m5d 'Wait-JournalGone' 'Race audit lost M5D journal cleanup proof.'
Assert-Contains $m5e 'Emergency M5E fallback cancelled only after FF/FF + cleared journal proof.' 'Race audit lost M5E cleanup ordering proof.'

# CI must execute the deterministic suites used by this audit.
foreach($needle in @(
    '--control-self-test',
    '--hp-backend-self-test',
    '--8c40-m8c-self-test',
    'test-watchdog-m5d-write-armed-invariants.ps1',
    'test-watchdog-m5e-write-armed-double-death-invariants.ps1',
    'test-8c40-m8-production-race-audit-invariants.ps1'
)){
    Assert-Contains $workflow $needle ("Race audit CI wiring missing: {0}" -f $needle)
}

# Documentation must keep the distinction between deterministic and physical evidence.
foreach($needle in @(
    'stale SafetyGate result vs newer accepted result',
    'safety preemption vs in-flight',
    'lifecycle/admission fence vs command dispatch',
    'watchdog liveness vs concurrent EC/telemetry failure',
    'external override after durable WRITE_INTENT',
    'transient guard / torn-read confirmation',
    'restore / journal cleanup ordering',
    'Code/CI coverage is not a physical PASS'
)){
    Assert-Contains $audit $needle ("Race audit documentation missing: {0}" -f $needle)
}

Assert-True ([bool]$profile.loadThermalM8Qualification.m8b.physicalPassed) 'Race audit must preserve the recorded M8B physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'Race audit must not imply M8C physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Race audit must keep automatic policy OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.watchdogRecoveryValidated) 'Race audit must keep watchdog recovery unpromoted.'

Write-Host 'HP 8C40 M8 production-race code/CI audit invariant self-test: PASS' -ForegroundColor Green
