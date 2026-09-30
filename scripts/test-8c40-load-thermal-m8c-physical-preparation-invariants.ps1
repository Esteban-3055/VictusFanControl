$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$controllerPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8CPhysicalThermalPreemptionQualificationTest.cs'
$controller=Get-Content $controllerPath -Raw
$cli=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$factory=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-ge 0){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}

Assert-Contains $controller 'public static readonly bool PhysicalExecutionAuthorized = false;' 'M8C physical controller must be re-blocked after attempt 1 FAIL_CLOSED pending evidence review.'
Assert-Contains $controller '8C40-M8C-THERMAL50' 'M8C future explicit token changed.'
Assert-Contains $controller 'Hp8C40M8CPhysicalCase.CpuConfirmed95' 'M8C CPU physical subcycle missing.'
Assert-Contains $controller 'Hp8C40M8CPhysicalCase.GpuImmediate87' 'M8C GPU physical subcycle missing.'

$authIndex=$controller.IndexOf('if (!PhysicalExecutionAuthorized)',[StringComparison]::Ordinal)
foreach($hardwareAnchor in @(
    'HardwareIdentityReader.ReadCurrent()',
    'new Hp8C40EcControlStateProbe(',
    'new HardwareTelemetryReader(',
    'new NamedPipeFanControlWatchdogLeaseClient(',
    'new Hp8C40FanHardware(',
    'new Hp8C40FanControlBackend('
)){
    $index=$controller.IndexOf($hardwareAnchor,[StringComparison]::Ordinal)
    if($authIndex -lt 0 -or $index -lt 0 -or $authIndex -ge $index){
        throw ("M8C compile-time authorization barrier must precede hardware path: {0}" -f $hardwareAnchor)
    }
}

foreach($needle in @(
    'RequiredConsecutiveRepresentativeSamples = 3',
    'IsRepresentativeLoad(snapshot)',
    'SafetyGate.CpuEmergencyC',
    'Hp8C40M8BWatchdogLoadQualificationTest.GpuPhysicalAbortC',
    'new FanCommand(',
    'QualificationLevel,',
    'applyCalls++;',
    'if (applyCalls != 1)',
    'M8C_READY_BEFORE_INJECTION',
    'synthetic-not-yet-injected',
    'WaitForParentContinueAsync',
    'M8C-CONTINUE',
    'CpuConfirmedThreshold(',
    'syntheticEpoch +',
    'TimeSpan.FromMilliseconds(ordinal)',
    'GpuImmediateThreshold(',
    'RequiredConsecutiveCpuSamples',
    'coordinator.EnforceSafetyAsync(',
    'FanAuthority.Firmware',
    'post-preemption local restore',
    'Hp8C40M8CThermalQualificationInjection.EvidenceMarker'
)){
    Assert-Contains $controller $needle ("M8C physical preparation invariant missing: {0}" -f $needle)
}

# The physical controller may construct hardware only behind the compile-time
# barrier, but production factory/UI must not make it reachable.
Assert-NotContains $factory 'Hp8C40M8CPhysicalThermalPreemptionQualificationTest' 'Production factory must not expose M8C physical qualification controller.'

foreach($needle in @(
    '--8c40-m8c-physical-thermal',
    '--8c40-m8c-physical-token',
    '--8c40-m8c-physical-case',
    '--8c40-m8c-physical-ready-path',
    '--8c40-m8c-physical-continue-path',
    '--8c40-m8c-physical-result-path',
    'BLOCKED M8C PHYSICAL'
)){
    Assert-Contains $cli $needle ("M8C blocked CLI preparation missing: {0}" -f $needle)
}

Assert-Contains $program 'Hp8C40M8CPhysicalThermalPreemptionQualificationTest.RequiredToken' 'M8C blocked Program token gate missing.'
Assert-Contains $program 'Hp8C40M8CPhysicalThermalPreemptionQualificationTest.RunAsync' 'M8C blocked Program route missing.'

Assert-True ([bool]$profile.loadThermalM8Qualification.m8b.physicalPassed) 'M8C physical preparation now requires recorded M8B physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M8C physical preparation must not mark M8C PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M8C physical retry must remain profile-blocked after attempt 1 FAIL_CLOSED.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'Automatic policy must remain OFF.'

foreach($needle in @(
    'M8C physical-controller preparation',
    'PhysicalExecutionAuthorized=false',
    'M8C_READY_BEFORE_INJECTION',
    'M8C-CONTINUE',
    'M8C attempt 1 - FAIL_CLOSED before READY',
    'M8C attempt-1 evidence review - real write reached, tach EC snapshot failed before Commit'
)){
    Assert-Contains $doc $needle ("M8C physical-controller documentation missing: {0}" -f $needle)
}

Write-Host 'HP 8C40 M8C hard-blocked physical-controller preparation invariant: PASS' -ForegroundColor Green
