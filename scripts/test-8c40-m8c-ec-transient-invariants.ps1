$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$backendPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$selfTestPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackendSelfTest.cs'
$controllerPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8CPhysicalThermalPreemptionQualificationTest.cs'
$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$docPath=Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md'

$backend=Get-Content $backendPath -Raw
$selfTest=Get-Content $selfTestPath -Raw
$controller=Get-Content $controllerPath -Raw
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json
$doc=Get-Content $docPath -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}

foreach($needle in @(
    'MaximumTransientTachSnapshotReadFailures = 2',
    'catch (IOException ex)',
    'lost EC observability after',
    'The real command remains uncommitted and must be restored fail-closed',
    'transient EC snapshot failures='
)){
    Assert-Contains $backend $needle ("M8C EC transient backend invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'TestTransientEcReadDuringTachAckRecoversAsync',
    'TestRepeatedEcReadDuringTachAckFailsClosedAsync',
    'TransientEcReadFailuresDuringTachAck = 1',
    'TransientEcReadFailuresDuringTachAck = 3',
    'commitCalls == 0'
)){
    Assert-Contains $selfTest $needle ("M8C EC transient self-test invariant missing: {0}" -f $needle)
}

Assert-Contains $controller '"APPLY_50_BEGIN"' 'M8C controller must persist the real write-dispatch boundary.'
Assert-Contains $controller '"APPLY_50_COMPLETE"' 'M8C controller must persist completed setpoint/tach/Commit acknowledgement.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M8C physical execution must be re-blocked after attempt-2 PASS.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.ecTransientHardening.ci.backendSelfTest) 'M8C EC transient hardening backend self-test CI evidence must remain PASS.'
if([string]$profile.loadThermalM8Qualification.m8c.ecTransientHardening.ci.commit -cne 'ac036a246f0bb4703bf9b346658d191b9d507de5'){throw 'M8C EC transient hardening CI commit changed.'}
if([int]$profile.loadThermalM8Qualification.m8c.ecTransientHardening.ci.runNumber -ne 828){throw 'M8C EC transient hardening CI run changed.'}
Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalRetry2Authorization.authorized) 'M8C attempt-2 authorization must be explicit.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M8C attempt-2 physical PASS must remain recorded.'
Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M8C compile-time physical gate must be closed after PASS.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalAttempts[0].realWriteAttempted) 'Attempt 1 must record that the real write boundary was crossed.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalAttempts[0].watchdogCommitObserved) 'Attempt 1 must record that watchdog Commit was not reached.'
Assert-Contains $doc 'M8C attempt-1 evidence review - real write reached, tach EC snapshot failed before Commit' 'M8C attempt-1 evidence-review documentation missing.'
Assert-Contains $doc 'at most **two** failed high-level control-state snapshots' 'M8C bounded transient policy documentation missing.'

Write-Host 'HP 8C40 M8C EC transient hardening invariant self-test: PASS' -ForegroundColor Green
