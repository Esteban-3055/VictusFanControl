$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$m9d=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualification.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$app=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M9D requires M8C physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M9D must keep M8C execution closed.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9D preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9D preparation must keep default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9D preparation must keep automatic/adaptive policy OFF.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9D preparation must not authorize M9C.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9D preparation must not open M9C construction.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9D physical execution must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9D temporary construction must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.productionConstructionAuthorized) 'M9D must not imply production promotion.'

foreach($needle in @(
    'public static readonly bool M9DPhysicalQualificationConstructionAuthorized = false;',
    'EnterM9DPhysicalQualificationConstructionScope',
    'M9DQualificationScopeDepth',
    'M9DPhysicalQualificationToken',
    'Nested or overlapping M9C/M9D production-watchdog construction scopes are forbidden.',
    'if (M9DQualificationScopeDepth.Value == 1)'
)){
    Assert-Contains $gate $needle ("M9D production-gate invariant missing: {0}" -f $needle)
}

Assert-Contains $m9d 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M9D compile-time execution gate must remain false.'
Assert-Contains $m9d 'Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationToken' 'M9D qualification class must share the centralized gate token.'
Assert-Contains $gate 'M9DPhysicalQualificationToken = "8C40-M9D-PRODUCTION-LIFECYCLE30"' 'M9D exact token literal is missing from the centralized gate.'

foreach($needle in @(
    '--8c40-m9d-production-lifecycle-test',
    '--8c40-m9d-test-token',
    'Hp8C40M9DProductionLifecycleQualification.RequiredToken',
    '!Hp8C40M9DProductionLifecycleQualification.PhysicalExecutionAuthorized',
    'm9dProductionLifecycleHardwareTest'
)){
    Assert-Contains $program $needle ("M9D App startup invariant missing: {0}" -f $needle)
}

$physicalGateIndex=$program.IndexOf('!Hp8C40M9DProductionLifecycleQualification.PhysicalExecutionAuthorized',[StringComparison]::Ordinal)
$modulesIndex=$program.IndexOf('var modulesDirectory = ResolveModulesDirectory(args);',[StringComparison]::Ordinal)
$mainFormIndex=$program.IndexOf('using var form = new MainForm(',[StringComparison]::Ordinal)
if($physicalGateIndex-lt 0 -or $modulesIndex-lt 0 -or $mainFormIndex-lt 0 -or
   -not ($physicalGateIndex-lt $modulesIndex -and $modulesIndex-lt $mainFormIndex)){
    throw 'M9D compile-time execution barrier must run before module resolution and MainForm construction.'
}

foreach($needle in @(
    '_m9dProductionLifecycleHardwareTest',
    'm9d-production-modern-standby',
    'm9d-production-lifecycle.ready',
    'm9d-production-lifecycle.presleep',
    'm9d-production-lifecycle.resume-gate',
    'm9d-production-lifecycle.reentry',
    'm9d-production-lifecycle.result',
    'EnterM9DPhysicalQualificationConstructionScope',
    'CreateLeaseIfAuthorized',
    'HpFanControlBackendFactory.Create',
    'IsM9DPhysicalQualificationScopeActive',
    'DisplayAware8C40LifecycleHardwareTest',
    'AllowCustomAdmissionAfterRecoveryAsync',
    'M6WatchdogStateReader.RequireOwned30'
)){
    Assert-Contains $app $needle ("M9D MainForm invariant missing: {0}" -f $needle)
}

$m9dBranch=$app.IndexOf('if (_m9dProductionLifecycleHardwareTest)',[StringComparison]::Ordinal)
$scopeIndex=$app.IndexOf('EnterM9DPhysicalQualificationConstructionScope',$m9dBranch,[StringComparison]::Ordinal)
$leaseIndex=$app.IndexOf('CreateLeaseIfAuthorized',$scopeIndex,[StringComparison]::Ordinal)
$factoryIndex=$app.IndexOf('HpFanControlBackendFactory.Create',$leaseIndex,[StringComparison]::Ordinal)
$scopeClosedIndex=$app.IndexOf('IsM9DPhysicalQualificationScopeActive',$factoryIndex,[StringComparison]::Ordinal)
$coordinatorIndex=$app.IndexOf('_fanCoordinator = new FanControlCoordinator(backend);',$scopeClosedIndex,[StringComparison]::Ordinal)
if($m9dBranch-lt 0 -or $scopeIndex-lt 0 -or $leaseIndex-lt 0 -or $factoryIndex-lt 0 -or
   $scopeClosedIndex-lt 0 -or $coordinatorIndex-lt 0 -or
   -not ($m9dBranch-lt $scopeIndex -and $scopeIndex-lt $leaseIndex -and
         $leaseIndex-lt $factoryIndex -and $factoryIndex-lt $scopeClosedIndex -and
         $scopeClosedIndex-lt $coordinatorIndex)){
    throw 'M9D route must be temporary scope -> normal lease helper -> factory -> scope closed -> coordinator.'
}

# M9D must not reuse the M6 qualification-only backend in its construction branch.
$m9dBranchEnd=$app.IndexOf('else',$m9dBranch,[StringComparison]::Ordinal)
if($m9dBranchEnd-lt 0){throw 'M9D construction branch boundary missing.'}
$m9dBlock=$app.Substring($m9dBranch,$m9dBranchEnd-$m9dBranch)
Assert-NotContains $m9dBlock 'CreateLifecycleQualificationBackend' 'M9D must not route through the historical M6 qualification backend.'

foreach($needle in @(
    'M9D full-GUI production-path lifecycle preparation',
    'D1 controller death',
    'D2 Modern Standby lifecycle',
    'No M9D physical execution is authorized'
)){
    Assert-Contains $doc $needle ("M9D documentation invariant missing: {0}" -f $needle)
}

Write-Host 'HP 8C40 M9D full-GUI production lifecycle preparation invariant: PASS' -ForegroundColor Green
