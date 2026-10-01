$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$contract=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9DProductionLifecycleQualification.cs') -Raw
$appProgram=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-True ([bool]$profile.lifecycle.watchdogM9CCodeCiPassed) 'M9D preparation requires M9C full code/CI PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed) 'M9D code preparation must not fabricate M9B physical read-only PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9D preparation must keep M9C physical execution blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9D physical execution must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9D temporary construction must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized) 'M9D physical authorization must remain false.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9D preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9D preparation must not open production construction.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9D preparation must keep control disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9D preparation must keep automatic/adaptive policy OFF.'

foreach($needle in @(
    'M9DPhysicalQualificationToken = "8C40-M9D-LIFECYCLE30"',
    'public static readonly bool M9DPhysicalQualificationConstructionAuthorized = false;',
    'EnterM9DPhysicalQualificationConstructionScope',
    'Nested/cross M9 production-watchdog construction scopes are forbidden',
    'M9DQualificationScopeDepth.Value = 0'
)){
    Assert-Contains $gate $needle ("M9D gate invariant missing: {0}" -f $needle)
}

Assert-Contains $contract 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M9D App execution gate must be compile-time closed.'
Assert-Contains $contract 'QualificationLevel = 30' 'M9D qualification level must remain 30.'

Assert-Contains $appProgram '--8c40-m9d-marker-root' 'M9D App mode must require a unique marker/evidence root.'

$mode=$appProgram.IndexOf('--8c40-m9d-production-lifecycle-test',[StringComparison]::Ordinal)
$barrier=$appProgram.IndexOf('// HARD M9D BARRIER',[StringComparison]::Ordinal)
$modules=$appProgram.IndexOf('var modulesDirectory = ResolveModulesDirectory(args);',[StringComparison]::Ordinal)
$target=$appProgram.IndexOf('Hp8C40TargetProfile.Matches(',[Math]::Max(0,$barrier),[StringComparison]::Ordinal)
$mainFormNew=$appProgram.IndexOf('using var form = new MainForm(',[StringComparison]::Ordinal)
if($mode-lt 0 -or $barrier-lt 0 -or $modules-lt 0 -or $target-lt 0 -or $mainFormNew-lt 0 -or
   -not ($mode-lt $barrier -and $barrier-lt $modules -and $barrier-lt $target -and $barrier-lt $mainFormNew)){
    throw 'M9D hard App authorization barrier must precede module/SMBIOS/MainForm construction.'
}

foreach($needle in @(
    '_m9dProductionLifecycleHardwareTest',
    'M9DMarkerPath("ready.marker")',
    'M9DMarkerPath("presleep.marker")',
    'M9DMarkerPath("resume-gate.marker")',
    'M9DMarkerPath("reentry.marker")',
    'M9DMarkerPath("result.marker")',
    'M9D refuses to overwrite existing marker evidence',
    'EnterM9DPhysicalQualificationConstructionScope',
    'HpFanControlBackendFactory.Create(',
    'IsM9DPhysicalQualificationScopeActive',
    'temporary construction scope remained active',
    'M9D refuses Custom admission because its temporary production construction scope was not proven closed',
    'GUID_SESSION_DISPLAY_STATUS/Off',
    'GUID_SESSION_DISPLAY_STATUS/On',
    'WaitForHardwareReadQuiescenceAsync',
    'AllowCustomAdmissionAfterRecoveryAsync',
    'M6WatchdogStateReader.RequireOwned30'
)){
    Assert-Contains $mainForm $needle ("M9D MainForm/lifecycle invariant missing: {0}" -f $needle)
}

$m9dRoute=$mainForm.IndexOf('if (_m9dProductionLifecycleHardwareTest)',[StringComparison]::Ordinal)
$factory=$mainForm.IndexOf('HpFanControlBackendFactory.Create(',[Math]::Max(0,$m9dRoute),[StringComparison]::Ordinal)
$scopeProof=$mainForm.IndexOf('_m9dConstructionScopeClosedBeforeCustom =',[Math]::Max(0,$factory),[StringComparison]::Ordinal)
$arm=$mainForm.IndexOf('private async Task ArmM6ModernStandbyHardwareTestAsync()',[StringComparison]::Ordinal)
$admissionProof=$mainForm.IndexOf('_m9dConstructionScopeClosedBeforeCustom',[Math]::Max(0,$arm),[StringComparison]::Ordinal)
$enterCustom=$mainForm.IndexOf('TryEnterM6CustomAuthorityAsync(',[Math]::Max(0,$admissionProof),[StringComparison]::Ordinal)
if($m9dRoute-lt 0 -or $factory-lt 0 -or $scopeProof-lt 0 -or $arm-lt 0 -or $admissionProof-lt 0 -or $enterCustom-lt 0 -or
   -not ($m9dRoute-lt $factory -and $factory-lt $scopeProof -and $scopeProof-lt $arm -and $arm-lt $admissionProof -and $admissionProof-lt $enterCustom)){
    throw 'M9D ordering must be mode -> production factory -> scope-closed proof -> arm guard -> Custom admission.'
}

Assert-NotContains $mainForm 'M9DLifecycleReadyPath' 'M9D must not use a fixed shared marker path.'
Assert-Contains $mainForm 'else if (DisplayAware8C40LifecycleHardwareTest)' 'Historical M6/M7 marker behavior must remain isolated from M9D.'
Assert-Contains $mainForm 'if (File.Exists(path))' 'M9D must refuse existing marker evidence instead of deleting it.'
Assert-Contains $doc 'M9D - production lifecycle last-mile regression' 'M9 documentation must define M9D.'

Write-Host 'HP 8C40 M9D production lifecycle preparation invariant: PASS' -ForegroundColor Green
