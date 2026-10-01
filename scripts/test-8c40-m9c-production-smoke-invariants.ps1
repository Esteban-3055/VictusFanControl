$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$controller=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M9CProductionPathQualificationTest.cs') -Raw
$factory=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs') -Raw
$backend=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$cli=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-True ([bool]$profile.lifecycle.watchdogM9CodeCiPassed) 'M9C preparation requires M9A CODE/CI PASS.'
Assert-True ([bool]$profile.lifecycle.watchdogM9BCodeCiPassed) 'M9C preparation requires M9B CODE/CI PASS.'
Assert-True ([bool]$profile.lifecycle.watchdogM9CCodeCiPassed) 'M9C full code/CI PASS must remain recorded after closure.'
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed) 'M9C authorization requires the formally recorded M9B physical read-only PASS.'
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed) 'M9C physical PASS must be formally recorded.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalAuthorization.authorized) 'M9C physical authorization must be re-blocked after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9C physical execution gate must be reclosed after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9C temporary construction gate must be reclosed after PASS.'
if([string]$profile.watchdogM9ProductionIntegration.m9c.physicalEvidence.result -cne 'PASS'){throw 'M9C physical evidence result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9c.physicalEvidence.evidenceHead -cne 'acfa9f670d8d8a36655c7b4ff7d1d8aac7a12dc8'){throw 'M9C physical evidence HEAD changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9c.physicalEvidence.evidenceZipSha256 -cne '4851434d5cbf59b7ae326857fa9757803e855bd03ac447654eddb8c0a1a698f3'){throw 'M9C evidence ZIP hash changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9c.physicalEvidence.applyCalls -ne 1){throw 'M9C physical evidence must record exactly one ApplyAsync.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalEvidence.failsafeTakeover) 'M9C PASS cannot include failsafe takeover.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9C preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9C preparation must not open production construction.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9C preparation must keep control disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9C preparation must keep automatic/adaptive policy OFF.'

foreach($needle in @(
    'public static readonly bool ProductionConstructionAuthorized = false;',
    'public static readonly bool M9CPhysicalQualificationConstructionAuthorized = false;',
    'M9CPhysicalQualificationToken = "8C40-M9C-PRODUCTION30"',
    'AsyncLocal<int>',
    'EnterM9CPhysicalQualificationConstructionScope',
    'Nested M9C production-watchdog construction scopes are forbidden',
    'M9CQualificationScopeDepth.Value = 0'
)){
    Assert-Contains $gate $needle ("M9C construction-gate invariant missing: {0}" -f $needle)
}

$barrier=$controller.IndexOf('if (!PhysicalExecutionAuthorized',[StringComparison]::Ordinal)
$admin=$controller.IndexOf('if (!IsAdministrator())',[StringComparison]::Ordinal)
$identity=$controller.IndexOf('HardwareIdentityReader.ReadCurrent()',[StringComparison]::Ordinal)
$ec=$controller.IndexOf('new Hp8C40EcControlStateProbe(',[StringComparison]::Ordinal)
$lease=$controller.IndexOf('new NamedPipeFanControlWatchdogLeaseClient(',[StringComparison]::Ordinal)
if($barrier-lt 0 -or $admin-lt 0 -or $identity-lt 0 -or $ec-lt 0 -or $lease-lt 0 -or
   -not ($barrier-lt $admin -and $barrier-lt $identity -and $barrier-lt $ec -and $barrier-lt $lease)){
    throw 'M9C physical authorization barrier must precede Administrator/identity/PawnIO/lease construction.'
}

foreach($needle in @(
    'public static readonly bool PhysicalExecutionAuthorized = false;',
    'QualificationLevel = 30',
    'RequiredConsecutivePreWriteSamples = 3',
    'SupervisionSamples = 5',
    'CpuPhysicalAbortC = 90.0',
    'GpuPhysicalAbortC = 82.0',
    'MaximumCpuPackagePowerW = 60.0',
    'MaximumGpuPowerW = 75.0',
    'HpFanControlBackendFactory.Create(',
    'EnterM9CPhysicalQualificationConstructionScope',
    'constructionScopeClosedBeforeCustom',
    'Exactly one production factory/backend ApplyAsync(30/30) begins.',
    'M9C-CONTINUE',
    'WatchdogReleaseVerified',
    'M9C finally fallback'
)){
    Assert-Contains $controller $needle ("M9C controller invariant missing: {0}" -f $needle)
}

Assert-NotContains $controller 'new Hp8C40FanControlBackend(' 'M9C controller must not bypass the normal factory/public backend route.'
Assert-NotContains $controller 'new Hp8C40FanHardware(' 'M9C controller must not construct real fan hardware directly.'
Assert-NotContains $factory 'EnterM9CPhysicalQualificationConstructionScope' 'M9C scope must be owned only by the versioned controller, not silently entered by factory.'
Assert-Contains $factory 'RequireProductionConstructionAuthorized' 'Factory watchdog construction must remain centrally gated.'
Assert-Contains $backend 'RequireProductionConstructionAuthorized' 'Public HP 8C40 watchdog constructor must remain independently gated.'

$scopeEnter=$controller.IndexOf('EnterM9CPhysicalQualificationConstructionScope',[StringComparison]::Ordinal)
$factoryCreate=$controller.IndexOf('HpFanControlBackendFactory.Create(',[StringComparison]::Ordinal)
$scopeProof=$controller.IndexOf(
    'constructionScopeClosedBeforeCustom =',
    [Math]::Max(0,$factoryCreate),
    [StringComparison]::Ordinal)
$customAdmission=$controller.IndexOf(
    'coordinator.TryEnterCustomAsync(',
    [Math]::Max(0,$scopeProof),
    [StringComparison]::Ordinal)
$apply=$controller.IndexOf(
    'await coordinator.ApplyAsync(',
    [Math]::Max(0,$customAdmission),
    [StringComparison]::Ordinal)
if($scopeEnter-lt 0 -or $factoryCreate-lt 0 -or $scopeProof-lt 0 -or $customAdmission-lt 0 -or $apply-lt 0 -or
   -not ($scopeEnter-lt $factoryCreate -and $factoryCreate-lt $scopeProof -and $scopeProof-lt $customAdmission -and $customAdmission-lt $apply)){
    throw 'M9C construction scope ordering must be scope -> factory -> scope-closed proof -> Custom admission -> Apply.'
}

foreach($needle in @(
    'options.Hp8C40M9CProductionSmoke',
    'Hp8C40M9CProductionPathQualificationTest.RequiredToken',
    'Hp8C40M9CProductionPathQualificationTest.RunAsync'
)){
    Assert-Contains $program $needle ("M9C Program route invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'Hp8C40M9CProductionSmoke',
    '--8c40-m9c-production-smoke',
    '--8c40-m9c-token',
    '--8c40-m9c-ready-path',
    '--8c40-m9c-continue-path',
    '--8c40-m9c-result-path'
)){
    Assert-Contains $cli $needle ("M9C CLI invariant missing: {0}" -f $needle)
}

Assert-Contains $doc 'M9C production-path smoke' 'M9 documentation must define M9C production-path smoke.'
Assert-Contains $doc 'M9C PHYSICAL PASS / FORMALLY CLOSED' 'M9C documentation must record the formal physical closure.'

Write-Host 'HP 8C40 M9C production-path smoke preparation invariant: PASS' -ForegroundColor Green


Assert-Contains $controller 'IFanControlWatchdogLeaseClient? lease' 'M9C controller must explicitly own the lease before factory handoff.'
Assert-Contains $controller 'lease = null;' 'M9C controller must mark successful lease ownership transfer to backend.'
Assert-Contains $controller 'await lease.DisposeAsync()' 'M9C controller must dispose an untransferred lease if factory construction fails.'
