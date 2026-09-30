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
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9A must not promote WatchdogRecoveryValidated.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9A must keep production control disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9A must keep automatic/adaptive policy OFF.'
Assert-True ([bool]$profile.lifecycle.watchdogM9ProductionIntegrationPrepared) 'M9 preparation flag missing.'
Assert-False ([bool]$profile.lifecycle.watchdogM9CodeCiPassed) 'M9A profile must remain CI-pending before the closure commit.'
Assert-False ([bool]$profile.lifecycle.watchdogM9NoWritePreflightPassed) 'M9B no-write preflight must not be implied by M9A.'
Assert-False ([bool]$profile.lifecycle.watchdogM9PhysicalPassed) 'M9 physical PASS must not be implied by code preparation.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9A must keep production construction blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.physicalExecutionAuthorized) 'M9A must authorize no physical execution.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.promotion.authorized) 'M9 promotion must remain blocked.'

foreach($needle in @(
    'public static readonly bool ProductionConstructionAuthorized = false;',
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
    'M9 production watchdog gate is closed by default',
    'factory rejects supplied 8C40 production lease before backend/hardware construction',
    'fakeLease.Calls == 0'
)){
    Assert-Contains $selfTest $needle ("M9 deterministic self-test missing: {0}" -f $needle)
}

Assert-Contains $program 'Hp8C40ProductionWatchdogGateSelfTest.Run' 'M9 self-test is not part of --hp-backend-self-test.'

foreach($needle in @(
    'M9A CODE PREPARED / CI PENDING',
    'ProductionConstructionAuthorized',
    'M9B - read-only production preflight',
    'M9C - bounded normal-production-path smoke',
    'M9D - last-mile recovery/lifecycle regression',
    'automaticPolicyEnabled=false',
    'control.enabledByDefault=false'
)){
    Assert-Contains $doc $needle ("M9 documentation invariant missing: {0}" -f $needle)
}

Assert-Contains $m8Race 'M9 production-watchdog integration/promotion' 'M8 race audit does not name the post-M8 M9 boundary.'

Write-Host 'HP 8C40 M9 production-watchdog preparation invariant: PASS' -ForegroundColor Green
