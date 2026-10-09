$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot

$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$selfTest=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGateSelfTest.cs') -Raw
$factory=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs') -Raw
$backend=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw
$m8Race=Get-Content (Join-Path $repoRoot 'docs\M8_PRODUCTION_RACE_AUDIT.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}

Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M9 requires M8C physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M9 must keep M8C physical execution closed.'
Assert-True ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9 production promotion must set lifecycle.watchdogRecoveryValidated=true.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9A must keep production control disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9A must keep automatic/adaptive policy OFF.'
Assert-True ([bool]$profile.lifecycle.watchdogM9ProductionIntegrationPrepared) 'M9 preparation flag missing.'
Assert-True ([bool]$profile.lifecycle.watchdogM9CodeCiPassed) 'M9A code/CI PASS must remain recorded after closure.'
Assert-True ([bool]$profile.lifecycle.watchdogM9NoWritePreflightPassed) 'M9B physical read-only PASS must remain formally recorded after closure.'
Assert-True ([bool]$profile.lifecycle.watchdogM9PhysicalPassed) 'M9D formal physical closure must set lifecycle.watchdogM9PhysicalPassed=true.'
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalPassed) 'M9D physical PASS must be formally recorded.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized) 'M9D physical authorization must be consumed/reblocked after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9D execution gate must be reclosed after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9D construction gate must be reclosed after PASS.'
if([string]$profile.watchdogM9ProductionIntegration.m9d.physicalEvidence.evidenceHead -cne '950378ae8debdba897b4fca0cd2dbd35d6c693f8'){throw 'M9D physical evidence HEAD changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9d.physicalEvidence.evidenceZipSha256 -cne 'c613459d172d2c75c6ea0c677d092505481af26cdb69bdcd8fb005bfa5d019b0'){throw 'M9D evidence ZIP hash changed.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9A must keep production construction blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.physicalExecutionAuthorized) 'M9A must authorize no physical execution.'
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.promotion.authorized) 'M9 production promotion must be explicitly authorized after readiness PASS.'
if([string]$profile.watchdogM9ProductionIntegration.m9a.codeCi.result -cne 'PASS'){throw 'M9A CI result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9a.codeCi.commit -cne 'f426df1480d92d87b9c5c5b55eb927a5a83dbf93'){throw 'M9A CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9a.codeCi.runNumber -ne 836){throw 'M9A CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9a.codeCi.runId -ne 36778419056){throw 'M9A CI run id changed.'}

foreach($needle in @(
    'public static readonly bool ProductionConstructionAuthorized = true;',
    'public static readonly bool M9CPhysicalQualificationConstructionAuthorized = false;',
    'public static readonly bool M9DPhysicalQualificationConstructionAuthorized = false;',
    'WatchdogRecoveryValidated',
    'RequireProductionConstructionAuthorized',
    'CreateLeaseIfAuthorized',
    'FanControlWatchdogLeaseContract.Hp8C40M4PipeName'
)){
    Assert-Contains $gate $needle ("M9 gate invariant missing: {0}" -f $needle)
}

Assert-Contains $mainForm 'Hp8C40ProductionWatchdogGate' 'MainForm normal production wiring is not M9-gated.'
Assert-Contains $mainForm 'CreateLeaseIfAuthorized' 'MainForm does not use the M9 no-side-effect lease helper.'

$factoryGate=$factory.IndexOf('RequireProductionConstructionAuthorized',[StringComparison]::Ordinal)
$factoryBackend=$factory.IndexOf('new Hp8C40FanControlBackend(',[StringComparison]::Ordinal)
if($factoryGate -lt 0 -or $factoryBackend -lt 0 -or $factoryGate -ge $factoryBackend){
    throw 'M9 factory gate must execute before HP 8C40 production backend construction.'
}

$backendGate=$backend.IndexOf('RequireProductionConstructionAuthorized',[StringComparison]::Ordinal)
$backendHardware=$backend.IndexOf('_hardware = new Hp8C40FanHardware(modulesDirectory);',[StringComparison]::Ordinal)
if($backendGate -lt 0 -or $backendHardware -lt 0 -or $backendGate -ge $backendHardware){
    throw 'M9 public backend defense must execute before HP 8C40 hardware construction.'
}

foreach($needle in @(
    'M9 production watchdog is promoted while M9C/M9D qualification gates remain closed',
    'exact HP 8C40 production watchdog construction is authorized after M9 promotion',
    'normal M9 wiring creates only the target-bound named-pipe lease client after promotion',
    'production authorization check passes without constructing hardware',
    'M9C temporary construction scope remains re-blocked after production promotion',
    'M9D temporary construction scope remains re-blocked after production promotion'
)){
    Assert-Contains $selfTest $needle ("M9 deterministic self-test missing: {0}" -f $needle)
}

Assert-Contains $program 'Hp8C40ProductionWatchdogGateSelfTest.Run' 'M9 self-test is not part of --hp-backend-self-test.'

foreach($needle in @(
    'M9A CODE/CI PASS',
    'ProductionConstructionAuthorized',
    'M9B - read-only production preflight',
    'M9C - bounded normal-production-path smoke',
    'M9D - last-mile recovery/lifecycle regression',
    'automaticPolicyEnabled=false',
    'control.enabledByDefault=false',
    'M9 PRODUCTION WATCHDOG PROMOTED'
)){
    Assert-Contains $doc $needle ("M9 documentation invariant missing: {0}" -f $needle)
}

Assert-Contains $m8Race 'M9 production-watchdog integration/promotion' 'M8 race audit does not name the post-M8 M9 boundary.'

Write-Host 'HP 8C40 M9 production-watchdog preparation invariant: PASS' -ForegroundColor Green
