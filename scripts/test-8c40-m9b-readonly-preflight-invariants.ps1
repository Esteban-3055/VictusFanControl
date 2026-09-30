$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$preflight=Get-Content (Join-Path $PSScriptRoot 'test-8c40-production-watchdog-m9b-preflight.ps1') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$packager=Get-Content (Join-Path $PSScriptRoot 'package-m9b-evidence.ps1') -Raw
$packagingSelfTest=Get-Content (Join-Path $PSScriptRoot 'test-8c40-m9b-evidence-packaging.ps1') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-True ([bool]$profile.lifecycle.watchdogM9CodeCiPassed) 'M9B preparation requires closed M9A CODE/CI PASS.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M9B requires M8C physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M9B must keep M8C closed.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9B must not promote watchdog recovery.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9B must keep default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9B must keep automatic/adaptive policy OFF.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9B must keep M9 production construction blocked.'
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyExecutionAuthorized) 'M9B read-only execution authorization must remain recorded after code/CI closure.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.physicalWriteAuthorized) 'M9B must never authorize fan writes.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9B must keep M9C physical execution blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9B must keep M9C construction blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed) 'M9B code/CI closure must not imply the physical read-only preflight already passed.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.codeCi.result -cne 'PASS'){throw 'M9B code/CI result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.codeCi.commit -cne '2ec00f047943c46885a15ab96642ec7c69a1e6dd'){throw 'M9B code/CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9b.codeCi.runNumber -ne 838){throw 'M9B code/CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9b.codeCi.runId -ne 36779270309){throw 'M9B code/CI run id changed.'}

foreach($needle in @(
    'M9B PRODUCTION WATCHDOG READ-ONLY PREFLIGHT',
    'READ-ONLY AUTHORIZATION BARRIER',
    'feature/victus-8c40-m9-production-watchdog',
    'Assert-RepositoryHead',
    'Assert-Exact8C40Target',
    'Assert-ProfileBoundary',
    'Assert-M4ServiceBaseline',
    'VictusFanControlWatchdogM4',
    '--m4-8c40-lease-service',
    'LocalSystem',
    'Assert-ServiceUnchanged',
    'Assert-StableFirmwareBaseline',
    '--8c40-m8-preflight-probe',
    'M8_PREFLIGHT_TELEMETRY_PASS',
    'test-8c40-m9-production-watchdog-invariants.ps1',
    'test-8c40-m9b-readonly-preflight-invariants.ps1',
    'm9b-preflight-result.json',
    'telemetry-output.txt',
    'package-m9b-evidence.ps1',
    'serviceExeSha256',
    'serviceModuleSha256',
    'profileSha256',
    'packageSha256',
    'FAIL_CLOSED'
)){
    Assert-Contains $preflight $needle ("M9B preflight invariant missing: {0}" -f $needle)
}

foreach($forbidden in @(
    'SetFanLevel(',
    '--restore-hp-auto',
    'Start-Service',
    'Stop-Service',
    'Set-Service',
    'New-Service',
    'sc.exe ',
    '--8c40-m4-lease10',
    '--8c40-m4-lease30',
    '--8c40-m4-lease50',
    'NamedPipeFanControlWatchdogLeaseClient',
    'CreateLeaseIfAuthorized',
    'shutdown.exe',
    'SetSuspendState',
    'git clean',
    'Remove-Item'
)){
    Assert-NotContains $preflight $forbidden ("M9B read-only preflight contains forbidden active operation: {0}" -f $forbidden)
}

Assert-Contains $gate 'public static readonly bool ProductionConstructionAuthorized = false;' 'M9 production construction gate must remain closed during M9B.'
Assert-Contains $doc 'M9B READ-ONLY PREFLIGHT CODE/CI PASS AND READ-ONLY EXECUTION AUTHORIZED' 'M9B documentation closure status missing.'
Assert-Contains $doc 'does **not** start or stop the service' 'M9B documentation must preserve the no-service-mutation contract.'

Write-Host 'HP 8C40 M9B read-only preflight invariant: PASS' -ForegroundColor Green


foreach($needle in @(
    'Get-FileHash',
    'm9b-package-manifest.json',
    'Compress-Archive',
    'ZipSha256',
    'destructiveOperations=$false'
)){
    Assert-Contains $packager $needle ("M9B packaging invariant missing: {0}" -f $needle)
}

foreach($forbidden in @(
    'SetFanLevel(',
    'Start-Service',
    'Stop-Service',
    'Set-Service',
    'New-Service',
    'sc.exe ',
    'Remove-Item'
)){
    Assert-NotContains $packager $forbidden ("M9B packager contains forbidden active/destructive operation: {0}" -f $forbidden)
}

Assert-Contains $packagingSelfTest 'HP 8C40 M9B evidence packaging self-test: PASS' 'M9B packaging deterministic self-test missing.'
