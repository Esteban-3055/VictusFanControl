$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$gate=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40ProductionWatchdogGate.cs') -Raw
$target=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40TargetProfile.cs') -Raw
$factory=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs') -Raw
$doc=Get-Content (Join-Path $repoRoot 'docs\PRODUCTION_WATCHDOG_8C40_M9.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){if($Value){throw $Message}}

Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M9F preparation must keep watchdog recovery unpromoted.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9a.productionConstructionAuthorized) 'M9F preparation must keep production construction blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.promotion.authorized) 'M9F preparation must keep profile promotion blocked.'
Assert-False ([bool]$profile.watchdogM9ProductionIntegration.m9f.promotionAuthorized) 'M9F preparation must not self-authorize.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M9F preparation must keep default control OFF.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M9F preparation must keep automatic/adaptive policy OFF.'

foreach($needle in @(
    'public static readonly bool FinalPromotionAuthorized = false;',
    'public static readonly bool ProductionConstructionAuthorized = false;',
    'if (!FinalPromotionAuthorized)',
    'if (!ProductionConstructionAuthorized)',
    'M9B/M9C/M9D/M9E physical closure'
)){Assert-Contains $gate $needle ("M9F central gate invariant missing: {0}" -f $needle)}

Assert-Contains $target 'WatchdogRecoveryValidated: false' 'M9F target profile must remain unpromoted.'
Assert-Contains $factory 'RequireProductionConstructionAuthorized' 'M9F requires the production factory to remain behind the central watchdog gate.'

foreach($needle in @(
    'M9F final promotion plan',
    'FinalPromotionAuthorized == false',
    'control.enabledByDefault=false',
    'automaticPolicyEnabled=false',
    'equal-only 10..50'
)){Assert-Contains $doc $needle ("M9F documentation invariant missing: {0}" -f $needle)}

if([string]$profile.watchdogM9ProductionIntegration.finalPrehardware.canonicalBranch -cne 'feature/victus-8c40-m9-final-prehardware'){
    throw 'M9F canonical final-prehardware branch changed.'
}
if([string]$profile.watchdogM9ProductionIntegration.m9f.status -notlike 'FINAL_PROMOTION_PLAN_*'){
    throw 'M9F preparation metadata is missing.'
}

Write-Host 'HP 8C40 M9F final promotion preparation invariant: PASS' -ForegroundColor Green
