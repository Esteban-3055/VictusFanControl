$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$policy=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogServicePolicy.cs') -Raw
$stage1=Get-Content (Join-Path $PSScriptRoot 'test-8c40-production-watchdog-m9e-arm.ps1') -Raw
$stage2=Get-Content (Join-Path $PSScriptRoot 'test-8c40-production-watchdog-m9e-postreboot.ps1') -Raw
$pack=Get-Content (Join-Path $PSScriptRoot 'package-m9e-evidence.ps1') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9E preparation must not promote watchdog recovery.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9E preparation must keep default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9E preparation must keep automatic/adaptive policy OFF.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9E preparation must keep production construction blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9e.stage1.physicalAuthorization.authorized) 'M9E stage1 physical authorization must remain false.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9e.stage1.serviceConfigurationAuthorized) 'M9E SCM mutation must remain blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9e.physicalPassed) 'M9E preparation must not imply physical PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9e.promotionAuthorized) 'M9E preparation must not authorize final promotion.'

foreach($needle in @(
    'ServiceName = "VictusFanControlWatchdogM4"',
    'PipeName = FanControlWatchdogLeaseContract.Hp8C40M4PipeName',
    'ServiceModeArgument = "--m4-8c40-lease-service"',
    'FailureResetSeconds = 86400',
    'FirstRestartDelayMs = 5000',
    'SecondRestartDelayMs = 5000',
    'ThirdRestartDelayMs = 10000',
    'AutomaticStart = true',
    'DelayedAutomaticStart = false',
    'ServiceConfigurationAuthorized = false'
)){Assert-Contains $policy $needle ("M9E policy invariant missing: {0}" -f $needle)}

$barrier=$stage1.IndexOf('# HARD M9E PHYSICAL/SERVICE-MUTATION BARRIER',[StringComparison]::Ordinal)
$admin=$stage1.IndexOf('Assert-Administrator',[StringComparison]::Ordinal)
$sc=$stage1.IndexOf('& sc.exe failure $serviceName',[StringComparison]::Ordinal)
if($barrier-lt 0 -or $admin-lt 0 -or $sc-lt 0 -or -not ($barrier-lt $admin -and $barrier-lt $sc)){
    throw 'M9E profile barrier must precede Administrator and all SCM mutation.'
}

foreach($needle in @(
    'm9b.noWritePreflightPassed',
    'm9c.physicalPassed',
    'm9d.physicalPassed',
    'stage1.physicalAuthorization.authorized',
    'stage1.serviceConfigurationAuthorized',
    "expectedBranch='feature/victus-8c40-m9-preproduction'",
    'Assert-QualificationBaseline',
    'Manual/Stopped/PID0',
    'restart/$FirstRestartDelayMs/restart/5000/restart/10000',
    'sc.exe failureflag $serviceName 1',
    'sc.exe config $serviceName start= auto',
    'Start-Service -Name $serviceName',
    'M9E-STAGE1',
    'rebootDispatched=$false',
    'No fan write or watchdog lease occurred'
)){Assert-Contains $stage1 $needle ("M9E stage1 invariant missing: {0}" -f $needle)}

foreach($forbidden in @(
    'SetFanLevel(',
    '--8c40-m4-lease10',
    '--8c40-m4-lease30',
    '--8c40-m4-lease50',
    'NamedPipeFanControlWatchdogLeaseClient',
    '--restore-hp-auto',
    'shutdown.exe',
    'Restart-Computer',
    'SetSuspendState',
    'install-watchdog-m4-8c40.ps1',
    'Remove-Item'
)){Assert-NotContains $stage1 $forbidden ("M9E stage1 forbidden authority/destructive operation: {0}" -f $forbidden)}

foreach($needle in @(
    'requires the preserved stage1 reboot-arm marker',
    'requires an actual reboot after stage1 arm',
    "StartMode-cne 'Auto'",
    "State-cne 'Running'",
    '5000\s*ms.*5000\s*ms.*10000\s*ms',
    'M9E-POSTREBOOT',
    'serviceMutationAttempted=$false',
    'fanWriteAttempted=$false',
    'watchdogLeaseAttempted=$false'
)){Assert-Contains $stage2 $needle ("M9E post-reboot invariant missing: {0}" -f $needle)}

foreach($forbidden in @('SetFanLevel(','Start-Service','Stop-Service','Set-Service','sc.exe config','sc.exe failure ','sc.exe failureflag ','NamedPipeFanControlWatchdogLeaseClient','--restore-hp-auto','shutdown.exe','Restart-Computer','Remove-Item')){
    Assert-NotContains $stage2 $forbidden ("M9E post-reboot must be read-only; forbidden operation: {0}" -f $forbidden)
}

Assert-Contains $pack 'destructiveOperations=$false' 'M9E packager must declare no destructive operations.'
Assert-NotContains $pack 'Remove-Item' 'M9E packager must never delete evidence.'
foreach($needle in @(
    'M9E production watchdog service lifecycle',
    'Automatic (non-delayed)',
    '5000/5000/10000',
    'does not replace the installed watchdog binary',
    'actual reboot'
)){Assert-Contains $doc $needle ("M9E documentation invariant missing: {0}" -f $needle)}

Write-Host 'HP 8C40 M9E production service lifecycle preparation invariant: PASS' -ForegroundColor Green
