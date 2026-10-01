$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$gateSelfTest=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGateSelfTest.cs') -Raw
$appProgram=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M9D preparation requires M8 physical closure.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M9D must keep M8C execution closed.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9D preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9D preparation must keep default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9D preparation must keep automatic/adaptive policy OFF.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9D preparation must not authorize M9C.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9D preparation must not open M9C construction.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9D physical execution must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9D construction must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.productionConstructionAuthorized) 'M9D must not imply production promotion.'

foreach($needle in @(
    'M9DPhysicalQualificationToken = "8C40-M9D-PRODUCTION-LIFECYCLE30"',
    'M9DPhysicalQualificationExecutionAuthorized = false',
    'M9DPhysicalQualificationConstructionAuthorized = false',
    'EnterM9DPhysicalQualificationConstructionScope',
    'IsM9DPhysicalQualificationScopeActive',
    'Concurrent M9C/M9D',
    'forbidden while/after production watchdog promotion'
)){Assert-Contains $gate $needle ("M9D central gate invariant missing: {0}" -f $needle)}

Assert-Contains $gateSelfTest 'M9D full-GUI production construction scope is compile-time blocked before M9C physical closure' 'M9 gate self-test must pin M9D closed-by-default behavior.'

foreach($needle in @(
    '--8c40-m9d-production-lifecycle-test',
    '--8c40-m9d-test-token',
    'M9DPhysicalQualificationExecutionAuthorized',
    'M9DPhysicalQualificationConstructionAuthorized',
    'M9D production-path Modern Standby'
)){Assert-Contains $appProgram $needle ("M9D app startup invariant missing: {0}" -f $needle)}

$barrier=$appProgram.IndexOf('HARD M9D BARRIER',[StringComparison]::Ordinal)
$modules=$appProgram.IndexOf('var modulesDirectory = ResolveModulesDirectory(args);',[StringComparison]::Ordinal)
$identity=$appProgram.IndexOf('var hardware = HardwareIdentityReader.ReadCurrent();',[Math]::Max(0,$barrier),[StringComparison]::Ordinal)
if($barrier-lt 0 -or $modules-lt 0 -or $barrier-ge $modules -or $identity-lt 0 -or $barrier-ge $identity){throw 'M9D executable authorization barrier must precede modules resolution and hardware identity reads.'}

foreach($needle in @(
    '_m9dProductionLifecycleHardwareTest',
    'm9d-production-lifecycle.ready',
    'm9d-production-lifecycle.presleep',
    'm9d-production-lifecycle.resume-gate',
    'm9d-production-lifecycle.reentry',
    'm9d-production-lifecycle.result',
    'EnterM9DPhysicalQualificationConstructionScope',
    'HpFanControlBackendFactory.Create',
    'IsM9DPhysicalQualificationScopeActive',
    'SESSION_DISPLAY_STATUS Off',
    'AllowCustomAdmissionAfterRecoveryAsync',
    'M6WatchdogStateReader.RequireOwned30'
)){Assert-Contains $mainForm $needle ("M9D MainForm invariant missing: {0}" -f $needle)}

$m9dEnter=$mainForm.IndexOf('EnterM9DPhysicalQualificationConstructionScope',[StringComparison]::Ordinal)
$m9dFactory=$mainForm.IndexOf('HpFanControlBackendFactory.Create(',[Math]::Max(0,$m9dEnter),[StringComparison]::Ordinal)
$m9dScopeCheck=$mainForm.IndexOf('IsM9DPhysicalQualificationScopeActive',[Math]::Max(0,$m9dFactory),[StringComparison]::Ordinal)
$coordinator=$mainForm.IndexOf('_fanCoordinator = new FanControlCoordinator(backend);',[StringComparison]::Ordinal)
if($m9dEnter-lt 0 -or $m9dFactory-lt 0 -or $m9dScopeCheck-lt 0 -or $coordinator-lt 0 -or -not ($m9dEnter-lt $m9dFactory -and $m9dFactory-lt $m9dScopeCheck -and $m9dScopeCheck-lt $coordinator)){throw 'M9D construction ordering must be scope -> production factory/public backend -> scope closed -> coordinator.'}

foreach($needle in @('M9D full-GUI lifecycle preparation','does **not** use','m9d-production-lifecycle.*','VictusFanControlWatchdogM4','Manual/Stopped','None are enabled here')){Assert-Contains $doc $needle ("M9D documentation invariant missing: {0}" -f $needle)}

Write-Host 'HP 8C40 M9D full-GUI lifecycle preparation invariant: PASS' -ForegroundColor Green
