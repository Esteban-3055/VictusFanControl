$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$engine=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicyEngine.cs') -Raw
$selfTest=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicySelfTest.cs') -Raw
$shadowPlanner=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanControlIntentPlanner.cs') -Raw
$shadowConfig=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicyShadowConfig.cs') -Raw
$shadowEvaluator=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicyShadowEvaluator.cs') -Raw
$shadowReplay=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicyShadowReplay.cs') -Raw
$shadowSelfTest=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicyShadowSelfTest.cs') -Raw
$shadowExample=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.adaptive-shadow.example.json') -Raw | ConvertFrom-Json
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$factory=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$workflow=Get-Content (Join-Path $repoRoot '.github\workflows\build.yml') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\ADAPTIVE_POLICY_PREPARATION.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}

foreach($needle in @(
    'AdaptiveFanPolicyConfig',
    'AdaptiveFanPolicyInput',
    'AdaptiveFanPolicyDecision',
    'Interpolate(',
    'MaximumUpStepPerSample',
    'MaximumDownStepPerSample',
    'DecreaseConfirmationSamples',
    'DecreaseDeadbandLevels',
    'MaximumSampleGap',
    'CpuTemperatureCurve',
    'GpuTemperatureCurve',
    'CpuPowerCurve',
    'GpuPowerCurve',
    'CpuLoadCurve',
    'GpuLoadCurve',
    'Math.Clamp',
    'duplicate/out-of-order telemetry',
    'telemetry continuity gap'
)){
    Assert-Contains $engine $needle ("Adaptive policy engine invariant missing: {0}" -f $needle)
}

foreach($forbidden in @(
    'FanControlCoordinator',
    'Hp8C40FanControlBackend',
    'SetFanLevel(',
    'HpOmenBiosWmiClient',
    'PawnIo',
    'NamedPipeFanControlWatchdogLeaseClient',
    'RestoreFirmwareAuto'
)){
    Assert-NotContains $engine $forbidden ("Adaptive policy engine must remain hardware-independent: {0}" -f $forbidden)
}

foreach($needle in @(
    'TestInterpolation',
    'TestHighestDomainWins',
    'TestUpwardSlew',
    'TestDownwardConfirmation',
    'TestEnvelope',
    'TestDuplicateTimestampRefused',
    'TestGapRefused',
    'TestInvalidTelemetryRefused',
    'TestInvalidConfigRefused'
)){
    Assert-Contains $selfTest $needle ("Adaptive policy self-test coverage missing: {0}" -f $needle)
}

foreach($needle in @(
    'AdaptiveFanControlIntentKind',
    'EnterCustomAndApply',
    'ApplyChangedLevel',
    'HoldCustom',
    'ReleaseToFirmware',
    'without retransmission'
)){
    Assert-Contains $shadowPlanner $needle ("Adaptive shadow intent planner invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'purpose=shadow-only',
    'authorizedForProduction=false',
    'Hp8C40TargetProfile.MinimumValidatedFanLevel',
    'Hp8C40TargetProfile.MaximumValidatedFanLevel'
)){
    Assert-Contains $shadowConfig $needle ("Adaptive shadow config invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'SafetyGate.Evaluate',
    'fanWritePathPresent: false',
    'Hp8C40ThermalEmergencyConfirmation',
    'AdaptiveFanPolicyEngine',
    'AdaptiveFanControlIntentPlanner',
    'HardwareWriteCapable => false'
)){
    Assert-Contains $shadowEvaluator $needle ("Adaptive shadow evaluator invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'Offline replay',
    'ParseCsvLine',
    'ParseCoreTemperatures',
    'SystemState.Healthy',
    'hardware writes       : 0'
)){
    Assert-Contains $shadowReplay $needle ("Adaptive shadow replay invariant missing: {0}" -f $needle)
}

foreach($source in @($shadowPlanner,$shadowConfig,$shadowEvaluator,$shadowReplay)){
    foreach($forbidden in @(
        'FanControlCoordinator',
        'Hp8C40FanControlBackend',
        'SetFanLevel(',
        'HpOmenBiosWmiClient',
        'PawnIoModuleSession',
        'NamedPipeFanControlWatchdogLeaseClient',
        'RestoreFirmwareAuto',
        'ApplyAsync('
    )){
        Assert-NotContains $source $forbidden ("Adaptive shadow/replay path must remain no-write/disconnected: {0}" -f $forbidden)
    }
}

Assert-NotContains $shadowReplay 'HardwareTelemetryReader' 'Offline adaptive replay must not instantiate live hardware telemetry.'
Assert-Contains $shadowSelfTest 'TestPlannerSuppressesRetransmission' 'Adaptive shadow planner self-test missing.'
Assert-Contains $shadowSelfTest 'TestCpuTemporalThermalRelease' 'Adaptive shadow CPU temporal-release self-test missing.'
Assert-Contains $shadowSelfTest 'TestGpuImmediateThermalRelease' 'Adaptive shadow GPU release self-test missing.'
Assert-Contains $shadowSelfTest 'TestDuplicateEpochReleasesNotionalCustom' 'Adaptive shadow duplicate-epoch self-test missing.'
Assert-Contains $shadowSelfTest 'TestWrongTargetNeverRecommendsCustom' 'Adaptive shadow wrong-target self-test missing.'
Assert-Contains $shadowSelfTest 'TestConfigAuthorizationGuards' 'Adaptive shadow config guard self-test missing.'
Assert-Contains $shadowSelfTest 'TestCsvReplayParser' 'Adaptive shadow CSV replay self-test missing.'

Assert-False ([bool]$shadowExample.authorizedForProduction) 'Adaptive shadow example must explicitly remain unauthorized for production.'
if([string]$shadowExample.purpose -ne 'shadow-only'){throw 'Adaptive shadow example purpose must remain shadow-only.'}
if([string]$shadowExample.targetProfileId -ne 'HP-8C40-9D0R1LA-F18'){throw 'Adaptive shadow example must stay bound to the exact HP 8C40 target.'}

Assert-NotContains $mainForm 'AdaptiveFanPolicyEngine' 'Adaptive policy must not be wired into the GUI before M8 physical closure.'
Assert-NotContains $factory 'AdaptiveFanPolicyEngine' 'Adaptive policy must not be wired into production backend construction.'
Assert-Contains $program 'automatic fan policy remains OFF' 'Program banner must keep automatic fan policy OFF.'
Assert-Contains $program 'AdaptiveFanPolicySelfTest.Run' 'Adaptive policy engine self-test CLI route missing.'
Assert-Contains $program 'AdaptiveFanPolicyShadowSelfTest.Run' 'Adaptive policy shadow self-test CLI route missing.'
Assert-Contains $program 'AdaptiveFanPolicyShadowReplay.RunAsync' 'Adaptive policy offline shadow replay CLI route missing.'
Assert-Contains $workflow '--adaptive-policy-self-test' 'Adaptive policy deterministic self-test is not wired into CI.'
Assert-Contains $workflow 'test-adaptive-policy-invariants.ps1' 'Adaptive policy static invariant is not wired into CI.'

Assert-False ([bool]$profile.control.enabledByDefault) 'Production fan control must remain disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Automatic/adaptive policy must remain OFF during M8.'
Assert-False ([bool]$profile.loadThermalM8Qualification.watchdogRecoveryValidated) 'Watchdog recovery must remain unpromoted.'

foreach($needle in @(
    'Production integration is disabled',
    'one equal CPU/GPU fan level',
    'no baked-in production curve',
    'Hardware integration remains blocked until M8 is physically closed',
    'offline shadow replay',
    'does not authorize production'
)){
    Assert-Contains $doc $needle ("Adaptive policy documentation invariant missing: {0}" -f $needle)
}

Write-Host 'Adaptive fan policy isolation/invariant self-test: PASS' -ForegroundColor Green
