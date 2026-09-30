$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$filter=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\CpuTemperatureComfortFilter.cs') -Raw
$postcool=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\TimedPostCoolingShadowStateMachine.cs') -Raw
$model=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\FanCurveEditorModel.cs') -Raw
$selftest=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveComfortShadowSelfTest.cs') -Raw
$engine=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Control\Adaptive\AdaptiveFanPolicyEngine.cs') -Raw
$editor=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\ShadowFanCurveEditorControl.cs') -Raw
$form=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\ShadowFanCurveEditorForm.cs') -Raw
$appProgram=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\Program.cs') -Raw
$mainForm=Get-Content (Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs') -Raw
$coreProgram=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\ADAPTIVE_COMFORT_SHADOW.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}

foreach($needle in @(
    'WindowSize = 5',
    'CpuControlTemperatureC',
    'FilteredTemperatureC',
    'TrendCPerSecond',
    'duplicate snapshot timestamp',
    'continuity reset'
)){
    Assert-Contains $filter $needle ("CPU comfort filter invariant missing: {0}" -f $needle)
}

Assert-NotContains $filter 'CpuCoreAverageTemperatureC' 'CPU comfort filter must not spatially average core sensors.'

foreach($needle in @(
    'CpuTemperatureTrendCurve',
    'CpuTemperatureTrendCPerSecond',
    'MaximumUpStepPerSample',
    'MaximumDownStepPerSample'
)){
    Assert-Contains $engine $needle ("Adaptive trend/slew invariant missing: {0}" -f $needle)
}

foreach($needle in @(
    'TimedPostCoolingShadowState',
    'HeavyLoadQualificationDuration',
    'MinimumPostCoolingDuration',
    'MaximumPostCoolingDuration',
    'no post-cooling pulse',
    'LifecycleReady'
)){
    Assert-Contains $postcool $needle ("Post-cooling invariant missing: {0}" -f $needle)
}

foreach($source in @($filter,$postcool,$model,$selftest,$editor,$form)){
    foreach($forbidden in @(
        'FanControlCoordinator',
        'Hp8C40FanControlBackend',
        'SetFanLevel(',
        'HpOmenBiosWmiClient',
        'PawnIoModuleSession',
        'NamedPipeFanControlWatchdogLeaseClient',
        'ApplyAsync(',
        'RestoreFirmwareAuto'
    )){
        Assert-NotContains $source $forbidden ("Adaptive comfort shadow component must remain hardware-independent: {0}" -f $forbidden)
    }
}

Assert-Contains $appProgram '--adaptive-curve-editor-shadow' 'Explicit shadow curve-editor CLI mode is missing.'
Assert-Contains $appProgram 'ShadowFanCurveEditorForm' 'Shadow curve-editor form is not reachable from the explicit simulation mode.'
Assert-NotContains $mainForm 'ShadowFanCurveEditor' 'Shadow editor must not be wired into production MainForm.'
Assert-Contains $coreProgram 'AdaptiveComfortShadowSelfTest.Run' 'Adaptive comfort self-test is not included in the existing adaptive self-test route.'

Assert-Contains $editor 'SetTelemetryIndicators' 'Editor filtered/raw telemetry indicator boundary is missing.'
Assert-Contains $editor 'instantaneousCpuMaxC' 'Editor raw instantaneous marker is missing.'
Assert-Contains $model 'MovePoint' 'Editor draggable-point model is missing.'
Assert-Contains $model 'Interpolate' 'Editor line interpolation is missing.'

if([bool]$profile.control.enabledByDefault){throw 'Production fan control must remain disabled by default.'}
if([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled){throw 'Automatic/adaptive policy must remain OFF.'}
if([bool]$profile.loadThermalM8Qualification.watchdogRecoveryValidated){throw 'WatchdogRecoveryValidated must remain false.'}

foreach($needle in @(
    'SHADOW ONLY',
    'median of five',
    'SafetyGate still evaluates raw telemetry',
    'no express light-load cooling pulses',
    'M8B remains open'
)){
    Assert-Contains $doc $needle ("Adaptive comfort shadow documentation invariant missing: {0}" -f $needle)
}

Write-Host 'Adaptive comfort shadow isolation/invariant self-test: PASS' -ForegroundColor Green
