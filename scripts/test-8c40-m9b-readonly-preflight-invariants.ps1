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
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyExecutionAuthorized) 'M9B read-only execution must be re-blocked after physical PASS closure.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.physicalWriteAuthorized) 'M9B must never authorize fan writes.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9B must keep M9C physical execution blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9B must keep M9C construction blocked.'
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.m9b.noWritePreflightPassed) 'M9B physical read-only PASS must be formally recorded.'
Assert-True ([bool]$profile.lifecycle.watchdogM9NoWritePreflightPassed) 'Lifecycle must record the M9B no-write physical PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.authorized) 'M9B read-only authorization must be closed after PASS.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.status -cne 'COMPLETED_REBLOCKED_AFTER_PHYSICAL_PASS'){throw 'M9B completed authorization status changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.authorizedExecutionHead -cne '63409c4d734bcc2c39ab968adb3470f18676463d'){throw 'M9B authorized execution HEAD changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.authorizedExecutionCiRunNumber -ne 924){throw 'M9B authorized execution CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.authorizedExecutionCiRunId -ne 36813663295){throw 'M9B authorized execution CI run id changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.authorizedExecutionCiResult -cne 'SUCCESS'){throw 'M9B authorized execution CI result changed.'}
Assert-True ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.completed) 'M9B authorization must be marked completed.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.result -cne 'PASS'){throw 'M9B completed authorization result must be PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.status -cne 'CODE_CI_PASS'){throw 'M9B runtime dependency bootstrap must remain CODE/CI PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.codeCi.result -cne 'PASS'){throw 'M9B runtime bootstrap CI result changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.codeCi.commit -cne '3d61b8721a9c67a509e4cff6459940378dacd553'){throw 'M9B runtime bootstrap CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.codeCi.runNumber -ne 921){throw 'M9B runtime bootstrap CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.codeCi.runId -ne 36813276213){throw 'M9B runtime bootstrap CI run id changed.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.codeCi.physicalExecution) 'M9B runtime bootstrap CI must remain software-only.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.hardwareMutation) 'M9B runtime bootstrap must not mutate hardware.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.runtimeDependencyBootstrap.serviceMutation) 'M9B runtime bootstrap must not mutate the watchdog service.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.result -cne 'PASS'){throw 'M9B physical evidence result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.evidenceHead -cne '63409c4d734bcc2c39ab968adb3470f18676463d'){throw 'M9B physical evidence HEAD changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.evidenceZipSha256 -cne 'd34ec42b4f22f47e383900b4f22e8dd37e26e4752cfd211a0f607e5e080698f1'){throw 'M9B evidence ZIP SHA-256 changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.resultSha256 -cne '215dd6c2f5b06258a47c756dcb4a2dcdc7b61750cf828449996e17a5bb9c0dc4'){throw 'M9B result SHA-256 changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.telemetrySha256 -cne 'ead64100edea3c9f1d14b950933687645e5b37f3695635da0191e33767439879'){throw 'M9B telemetry SHA-256 changed.'}
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.firmwareProof.passed){throw 'M9B stable FF/FF proof must remain PASS.'}
if([bool]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.journalPresent){throw 'M9B physical evidence must retain journal absence.'}
foreach($field in @('fanWriteAttempted','firmwareRestoreAttempted','watchdogLeaseAttempted','serviceMutationAttempted','destructiveOperations')){ if([bool]$profile.watchdogM9ProductionIntegration.m9b.physicalEvidence.$field){throw "M9B closure unexpectedly records mutation in $field."} }
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.physicalWriteAuthorized) 'M9B authorization must not authorize fan writes.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.serviceMutationAuthorized) 'M9B authorization must not authorize service mutation.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.readOnlyAuthorization.watchdogLeaseAcquisitionAuthorized) 'M9B authorization must not authorize watchdog lease acquisition.'
if([string]$profile.watchdogM9ProductionIntegration.m9b.codeCi.result -cne 'PASS'){throw 'M9B code/CI result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.codeCi.commit -cne '2ec00f047943c46885a15ab96642ec7c69a1e6dd'){throw 'M9B code/CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9b.codeCi.runNumber -ne 838){throw 'M9B code/CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9b.codeCi.runId -ne 36779270309){throw 'M9B code/CI run id changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.branch -cne 'feature/victus-8c40-m9-canonical-prehardware'){throw 'M9B canonical branch binding changed.'}
if([string]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.status -cne 'CODE_CI_PASS'){throw 'M9B canonical rebind CODE/CI closure changed.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9b.canonicalRebind.readOnlyExecutionAuthorized) 'Canonical M9B read-only authorization must be closed after physical PASS.'
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
Assert-Contains $doc 'M9B PHYSICAL READ-ONLY PASS / FORMALLY CLOSED' 'M9B physical closure documentation status missing.'
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
