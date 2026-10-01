$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$target=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40TargetProfile.cs') -Raw
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}
function Assert-Equal($Actual,$Expected,[string]$Message){
    if($Actual -ne $Expected){throw ("{0} Expected='{1}' Actual='{2}'" -f $Message,$Expected,$Actual)}
}
function Assert-ArrayContainsExact($Values,[string]$Needle,[string]$Message){
    foreach($value in @($Values)){
        if([string]$value -ceq $Needle){return}
    }
    throw $Message
}
function Assert-ArrayContainsSubstring($Values,[string]$Needle,[string]$Message){
    foreach($value in @($Values)){
        if(([string]$value).IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){return}
    }
    throw $Message
}

Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9E preparation must not promote profile watchdog recovery.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.promotion.authorized) 'M9E preparation must not authorize production promotion.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9e.promotionAuthorized) 'M9E itself must remain non-promoting.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9e.physicalExecutionAuthorized) 'M9E must have no physical execution path.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9E must keep control disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9E must keep automatic/adaptive policy OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M9E must keep M8C physical gate closed.'
if(-not [bool]$profile.watchdogM9ProductionIntegration.m9c.physicalPassed){throw 'M9E readiness audit must observe formally recorded M9C physical PASS.'}
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalExecutionAuthorized) 'M9E must observe M9C execution reclosed after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.qualificationConstructionAuthorized) 'M9E must observe M9C construction reclosed after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9c.physicalAuthorization.authorized) 'M9E must observe M9C authorization consumed/reblocked after PASS.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalAuthorization.authorized) 'M9E must observe consumed M9D retry reblocked after FAIL_CLOSED.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.physicalExecutionAuthorized) 'M9E must observe M9D execution reblocked during EC contention fix qualification.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9d.qualificationConstructionAuthorized) 'M9E must observe M9D construction reblocked during EC contention fix qualification.'
if(-not [bool]$profile.lifecycle.watchdogM9PromotionReadinessCodeCiPassed){throw 'M9E promotion-readiness code/CI PASS flag must remain recorded.'}
if([string]$profile.watchdogM9ProductionIntegration.m9e.codeCi.result -cne 'PASS'){throw 'M9E promotion-readiness code/CI result must remain PASS.'}
if([string]$profile.watchdogM9ProductionIntegration.m9e.codeCi.commit -cne 'ac9041ffef21adc96f8f2190455d3468cf13204a'){throw 'M9E promotion-readiness CI evidence commit changed.'}
if([int]$profile.watchdogM9ProductionIntegration.m9e.codeCi.runNumber -ne 908){throw 'M9E promotion-readiness CI run number changed.'}
if([long]$profile.watchdogM9ProductionIntegration.m9e.codeCi.runId -ne 36806074535){throw 'M9E promotion-readiness CI run id changed.'}

Assert-Equal ([int]$profile.control.validatedMinimumLevel) 10 'M9E minimum fan level drifted.'
Assert-Equal ([int]$profile.control.validatedMaximumLevel) 50 'M9E maximum fan level drifted.'
Assert-False ([bool]$profile.control.supportsIndependentLevels) 'M9E must remain equal-only.'

Assert-Contains $target 'WatchdogRecoveryValidated: false' 'Target runtime watchdog promotion gate must remain false before physical closure.'
Assert-Contains $gate 'public static readonly bool ProductionConstructionAuthorized = false;' 'Production construction gate must remain false before physical closure.'
Assert-Contains $gate 'M9CPhysicalQualificationConstructionAuthorized = false;' 'M9C construction gate must be reclosed after bounded physical PASS.'
Assert-Contains $gate 'M9DPhysicalQualificationConstructionAuthorized = false;' 'M9D construction gate must be reclosed during EC contention fix qualification.'

foreach($needle in @(
    'M9B read-only physical preflight PASS',
    'M9C normal production factory/public-backend physical smoke PASS',
    'M9D GUI-side service bootstrap + production-path Modern Standby lifecycle PASS'
)){
    Assert-ArrayContainsSubstring $profile.watchdogM9ProductionIntegration.m9e.prerequisites $needle ("M9E profile prerequisite missing: {0}" -f $needle)
}

foreach($needle in @(
    'Hp8C40TargetProfile.Instance WatchdogRecoveryValidated: false -> true',
    'Hp8C40ProductionWatchdogGate.ProductionConstructionAuthorized: false -> true'
)){
    Assert-ArrayContainsExact $profile.watchdogM9ProductionIntegration.m9e.atomicPromotionChanges $needle ("M9E profile atomic promotion change missing: {0}" -f $needle)
}

foreach($needle in @(
    'control.enabledByDefault=false',
    'automaticPolicyEnabled=false'
)){
    Assert-ArrayContainsExact $profile.watchdogM9ProductionIntegration.m9e.invariantsThatMustRemain $needle ("M9E profile preserved invariant missing: {0}" -f $needle)
}

Assert-Contains $doc 'M9E promotion-readiness auditor' 'M9 documentation does not define M9E.'
Assert-Contains $doc 'A half-promotion is explicitly invalid' 'M9 documentation must prohibit half-promotion.'
Assert-Contains $doc 'contains no mutator' 'M9E must document that promotion is never automatic.'

Write-Host 'HP 8C40 M9 promotion-readiness invariant: PASS' -ForegroundColor Green
