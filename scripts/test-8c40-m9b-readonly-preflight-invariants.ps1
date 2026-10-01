$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$preflight=Get-Content (Join-Path $PSScriptRoot 'test-8c40-production-watchdog-m9b-preflight.ps1') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$packager=Get-Content (Join-Path $PSScriptRoot 'package-m9b-evidence.ps1') -Raw
$packagingSelfTest=Get-Content (Join-Path $PSScriptRoot 'test-8c40-m9b-evidence-packaging.ps1') -Raw
$pawnIoSetup=Get-Content (Join-Path $PSScriptRoot 'setup-pawnio-modules.ps1') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-True ([bool]$profile.lifecycle.watchdogM9CodeCiPassed) 'M9B preparation requires closed M9A CODE/CI PASS.'
Assert-True ([bool]$profile.lifecycle.watchdogM9CanonicalPrehardwareCodeCiPassed) 'Canonical M9 pre-hardware rebind must have same-HEAD CODE/CI PASS before read-only authorization.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M9B requires M8C physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M9B must keep M8C closed.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9B must not promote watchdog recovery.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9B must keep default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9B must keep automatic/adaptive policy OFF.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9B must keep M9 production construction blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyExecutionAuthorized) 'M9B runtime-bootstrap preparation must re-block target-side execution until same-HEAD CI closes.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.physicalWriteAuthorized) 'M9B must never authorize fan writes.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9B must keep M9C physical execution blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9B must keep M9C construction blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed) 'M9B authorization must not imply the physical read-only preflight already passed.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.authorized) 'M9B runtime-bootstrap preparation must close read-only authorization.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.status -cne 'BLOCKED_PENDING_RUNTIME_BOOTSTRAP_CI'){throw 'M9B read-only authorization preparation status changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.previousAuthorizedHead -cne '1d5470fe258719b22e61c2770bd2b9afc568b63b'){throw 'M9B previous authorized HEAD changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.previousAuthorizedCiRunNumber -ne 918){throw 'M9B previous authorization CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.previousAuthorizedCiRunId -ne 36811842327){throw 'M9B previous authorization CI run id changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.previousAuthorizedCiResult -cne 'SUCCESS'){throw 'M9B previous authorization CI result changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.status -cne 'CODE_PREPARED_CI_PENDING'){throw 'M9B runtime dependency bootstrap must remain CI-pending in the preparation commit.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.hardwareMutation) 'M9B runtime bootstrap must not mutate hardware.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.serviceMutation) 'M9B runtime bootstrap must not mutate the watchdog service.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.physicalWriteAuthorized) 'M9B authorization must not authorize fan writes.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.serviceMutationAuthorized) 'M9B authorization must not authorize service mutation.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.watchdogLeaseAcquisitionAuthorized) 'M9B authorization must not authorize watchdog lease acquisition.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.codeCi.result -cne 'PASS'){throw 'M9B code/CI result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.codeCi.commit -cne '2ec00f047943c46885a15ab96642ec7c69a1e6dd'){throw 'M9B code/CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9b.codeCi.runNumber -ne 838){throw 'M9B code/CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9b.codeCi.runId -ne 36779270309){throw 'M9B code/CI run id changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.branch -cne 'feature/victus-8c40-m9-canonical-prehardware'){throw 'M9B canonical branch binding changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.status -cne 'CODE_CI_PASS'){throw 'M9B canonical rebind CODE/CI closure changed.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.readOnlyExecutionAuthorized) 'Canonical M9B execution must be re-blocked while runtime-bootstrap CI is pending.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.codeCi.result -cne 'PASS'){throw 'M9B canonical rebind CI result must be PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.codeCi.commit -cne 'e896d0d52f22079608302f81f5b0177e5d47d093'){throw 'M9B canonical rebind CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.codeCi.runNumber -ne 912){throw 'M9B canonical rebind CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.codeCi.runId -ne 36806751677){throw 'M9B canonical rebind CI run id changed.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.codeCi.physicalExecution) 'Canonical rebind CI must remain software-only.'

foreach($needle in @(
    'M9B PRODUCTION WATCHDOG READ-ONLY PREFLIGHT',
    'READ-ONLY AUTHORIZATION BARRIER',
    'feature/victus-8c40-m9-canonical-prehardware',
    'Ensure-LocalPawnIoModules',
    'setup-pawnio-modules.ps1',
    'IntelMSR.bin',
    'LpcACPIEC.bin',
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

foreach($needle in @(
    '$release = ''0.2.11''',
    '$expectedSha256 = ''43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4''',
    'Get-FileHash',
    'IntelMSR.bin',
    'LpcACPIEC.bin'
)){
    Assert-Contains $pawnIoSetup $needle ("M9B PawnIO setup invariant missing: {0}" -f $needle)
}
foreach($forbidden in @('SetFanLevel(','Start-Service','Stop-Service','Set-Service','New-Service','sc.exe ','shutdown.exe','SetSuspendState')){
    Assert-NotContains $pawnIoSetup $forbidden ("M9B PawnIO setup contains forbidden hardware/service operation: {0}" -f $forbidden)
}

Assert-Contains $gate 'public static readonly bool ProductionConstructionAuthorized = false;' 'M9 production construction gate must remain closed during M9B.'
Assert-Contains $doc 'M9B RUNTIME-DEPENDENCY BOOTSTRAP CODE PREPARED / READ-ONLY EXECUTION BLOCKED PENDING CI' 'M9B runtime-bootstrap preparation documentation status missing.'
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
