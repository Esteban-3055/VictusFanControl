$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$meta=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualificationTest.cs') -Raw
$appProgram=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$bootstrap=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Hp8C40WatchdogServiceBootstrap.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}

Assert-True ([bool]$profile.lifecycle.watchdogM9DCodePrepared) 'M9D code-prepared flag missing.'
Assert-True ([bool]$profile.lifecycle.watchdogM9DCodeCiPassed) 'M9D code/CI PASS must remain recorded after closure.'
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed) 'M9D EC-contention fix requires formally closed M9C physical PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9D execution must be reblocked while EC-contention fix CI is pending.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9D construction must be reblocked while EC-contention fix CI is pending.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized) 'Consumed M9D retry authorization must be reblocked after FAIL_CLOSED.'
if([string]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.result -cne 'FAIL_CLOSED'){throw 'M9D failed retry result must remain FAIL_CLOSED.'}
if([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.operatorSleepTransitionReached){throw 'M9D failed retry must remain recorded as pre-Modern-Standby.'}
if([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.fanWriteAttempted){throw 'M9D failed retry must remain recorded as pre-write.'}
if([string]$profile.watchdogM9ProductionIntegration.m9d.ecMutexContentionFix.status -cne 'CODE_PREPARED_CI_PENDING'){throw 'M9D EC mutex fix must remain CI-pending in this preparation commit.'}
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9D must not promote watchdog recovery.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9D must not promote normal production construction.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9D must keep control disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9D must keep automatic/adaptive policy OFF.'
if([string]$profile.watchdogM9ProductionIntegration.m9d.codeCi.result -cne 'PASS'){throw 'M9D code/CI result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9d.codeCi.commit -cne '91332ac590a456c0489406e9262d25b85a6528ca'){throw 'M9D code/CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9d.codeCi.runNumber -ne 894){throw 'M9D code/CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9d.codeCi.runId -ne 36803864224){throw 'M9D code/CI run id changed.'}

foreach($needle in @(
    'M9DPhysicalQualificationToken = "8C40-M9D-PRODUCTION-LIFECYCLE30"',
    'M9DPhysicalQualificationConstructionAuthorized = false;',
    'EnterM9DPhysicalQualificationConstructionScope',
    'IsM9DPhysicalQualificationScopeActive',
    'Nested/overlapping M9 production-watchdog construction scopes are forbidden'
)){
    Assert-Contains $gate $needle ("M9D gate invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'PhysicalExecutionAuthorized = false;',
    'QualificationLevel = 30',
    'M9DPhysicalQualificationToken'
)){
    Assert-Contains $meta $needle ("M9D metadata invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    '--8c40-m9d-production-lifecycle-test',
    '--8c40-m9d-test-token',
    '--8c40-m9d-marker-root',
    'Hp8C40M9DProductionLifecycleQualificationTest.PhysicalExecutionAuthorized',
    'Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationConstructionAuthorized',
    'm9dProductionLifecycleHardwareTest'
)){
    Assert-Contains $appProgram $needle ("M9D app startup invariant missing: {0}" -f $needle)
}

$programBarrier=$appProgram.IndexOf('Hp8C40M9DProductionLifecycleQualificationTest.PhysicalExecutionAuthorized',[StringComparison]::Ordinal)
$programModules=$appProgram.IndexOf('var modulesDirectory = ResolveModulesDirectory(args);',[StringComparison]::Ordinal)
if($programBarrier-lt 0 -or $programModules-lt 0 -or $programBarrier-ge $programModules){
    throw 'M9D hard execution barrier must precede modules/backend construction.'
}

foreach($needle in @(
    '_m9dProductionLifecycleHardwareTest',
    'EnterM9DPhysicalQualificationConstructionScope',
    'HpFanControlBackendFactory.Create(',
    'IsM9DPhysicalQualificationScopeActive',
    'm9d-production-lifecycle.ready',
    'm9d-production-lifecycle.presleep',
    'm9d-production-lifecycle.resume-gate',
    'm9d-production-lifecycle.reentry',
    'm9d-production-lifecycle.result',
    'M9D refuses to overwrite existing evidence marker',
    'DisplayAware8C40LifecycleHardwareTest',
    'WaitForHardwareReadQuiescenceAsync',
    'AllowCustomAdmissionAfterRecoveryAsync',
    'M6WatchdogStateReader.RequireOwned30',
    'constructionRoute=',
    'production-factory-public-backend',
    'Hp8C40WatchdogServiceBootstrap',
    'EnsureM9DQualificationReady',
    'serviceBootstrap='
)){
    Assert-Contains $mainForm $needle ("M9D MainForm/lifecycle invariant missing: {0}" -f $needle)
}

$m9dBranch=$mainForm.IndexOf('if (_m9dProductionLifecycleHardwareTest)',[StringComparison]::Ordinal)
$scope=$mainForm.IndexOf('EnterM9DPhysicalQualificationConstructionScope',[Math]::Max(0,$m9dBranch),[StringComparison]::Ordinal)
$factory=$mainForm.IndexOf('HpFanControlBackendFactory.Create(',[Math]::Max(0,$scope),[StringComparison]::Ordinal)
$scopeProof=$mainForm.IndexOf('IsM9DPhysicalQualificationScopeActive',[Math]::Max(0,$factory),[StringComparison]::Ordinal)
if($m9dBranch-lt 0 -or $scope-lt 0 -or $factory-lt 0 -or $scopeProof-lt 0 -or
   -not ($m9dBranch-lt $scope -and $scope-lt $factory -and $factory-lt $scopeProof)){
    throw 'M9D production-path construction order must be mode -> temporary scope -> normal factory -> scope-closed proof.'
}

$m9dBlockEnd=$mainForm.IndexOf('else if (DisplayAware8C40LifecycleHardwareTest)',[Math]::Max(0,$m9dBranch),[StringComparison]::Ordinal)
if($m9dBlockEnd-le $m9dBranch){throw 'M9D backend block boundary missing.'}
$m9dBlock=$mainForm.Substring($m9dBranch,$m9dBlockEnd-$m9dBranch)
Assert-NotContains $m9dBlock 'CreateLifecycleQualificationBackend' 'M9D must use normal factory/public backend rather than the M6 qualification constructor.'

Assert-Contains $doc 'M9D production-path Modern Standby lifecycle preparation' 'M9 documentation must describe M9D.'
Assert-Contains $doc 'M9D FAIL_CLOSED / EC-MUTEX RETRY FIX CODE PREPARED' 'M9D documentation must record EC mutex fail-closed fix preparation.'

foreach($needle in @(
    'ServiceName = "VictusFanControlWatchdogM4"',
    'ServiceStartMode.Manual',
    'M6WatchdogStateReader.RequireReady',
    'snapshot.JournalPresent',
    'EnsureProductionReady',
    'EnsureM9DQualificationReady'
)){
    Assert-Contains $bootstrap $needle ("M9D service-bootstrap invariant missing: {0}" -f $needle)
}
Assert-NotContains $bootstrap 'sc.exe' 'GUI watchdog bootstrap must not reconfigure services.'
Assert-NotContains $bootstrap 'Delete' 'GUI watchdog bootstrap must not delete service/evidence.'
Assert-NotContains $bootstrap 'SetFanLevel' 'GUI watchdog bootstrap must not write fan state.'

Write-Host 'HP 8C40 M9D production-lifecycle preparation invariant: PASS' -ForegroundColor Green
