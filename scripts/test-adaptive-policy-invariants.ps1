$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$engine=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicyEngine.cs') -Raw
$selfTest=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicySelfTest.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$factory=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlBackendFactory.cs') -Raw
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

Assert-NotContains $mainForm 'AdaptiveFanPolicyEngine' 'Adaptive policy must not be wired into the GUI before M8 physical closure.'
Assert-NotContains $factory 'AdaptiveFanPolicyEngine' 'Adaptive policy must not be wired into production backend construction.'
Assert-Contains $program 'automatic fan policy remains OFF' 'Program banner must keep automatic fan policy OFF.'
Assert-Contains $program 'AdaptiveFanPolicySelfTest.Run' 'Adaptive policy test-only CLI route missing.'
Assert-Contains $workflow '--adaptive-policy-self-test' 'Adaptive policy deterministic self-test is not wired into CI.'
Assert-Contains $workflow 'test-adaptive-policy-invariants.ps1' 'Adaptive policy static invariant is not wired into CI.'

Assert-False ([bool]$profile.control.enabledByDefault) 'Production fan control must remain disabled by default.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Automatic/adaptive policy must remain OFF during M8.'
Assert-False ([bool]$profile.loadThermalM8Qualification.watchdogRecoveryValidated) 'Watchdog recovery must remain unpromoted.'

foreach($needle in @(
    'Production integration is disabled',
    'one equal CPU/GPU fan level',
    'no baked-in production curve',
    'Hardware integration remains blocked until M8 is physically closed'
)){
    Assert-Contains $doc $needle ("Adaptive policy documentation invariant missing: {0}" -f $needle)
}

Write-Host 'Adaptive fan policy isolation/invariant self-test: PASS' -ForegroundColor Green
