$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$injectionPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8CThermalQualificationInjection.cs'
$selfTestPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8CThermalPreemptionSelfTest.cs'
$mainFormPath=Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'
$cliPath=Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs'
$programPath=Join-Path $repoRoot 'src\VictusFanControl\Program.cs'
$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$docPath=Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md'

$injection=Get-Content $injectionPath -Raw
$selfTest=Get-Content $selfTestPath -Raw
$mainForm=Get-Content $mainFormPath -Raw
$cli=Get-Content $cliPath -Raw
$program=Get-Content $programPath -Raw
$profile=Get-Content $profilePath -Raw | ConvertFrom-Json
$doc=Get-Content $docPath -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}
}
function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}
function Assert-Equal($Actual,$Expected,[string]$Message){
    if($Actual -ne $Expected){throw ("{0} Expected='{1}' Actual='{2}'" -f $Message,$Expected,$Actual)}
}

foreach($needle in @(
    'M8C_SYNTHETIC_QUALIFICATION_ONLY',
    'CpuConfirmedThreshold',
    'GpuImmediateThreshold',
    'CpuHardImmediateThreshold',
    'SafetyGate.Evaluate(',
    'confirmation.Apply(',
    'SafetyGate.CpuEmergencyC',
    'SafetyGate.GpuEmergencyC',
    'Hp8C40ThermalEmergencyConfirmation.CpuHardEmergencyC',
    'Hp8C40TargetProfile.Instance.ExpectedPhysicalCoreCount'
)){
    Assert-Contains $injection $needle ("M8C injection invariant missing: {0}" -f $needle)
}

foreach($forbidden in @(
    'SetFanLevel(',
    'HpOmenBiosWmiClient',
    'Hp8C40FanControlBackend',
    'Hp8C40FanHardware',
    'PawnIo',
    'NamedPipeFanControlWatchdogLeaseClient',
    'RestoreFirmwareAuto'
)){
    Assert-NotContains $injection $forbidden ("M8C synthetic injection must remain hardware-free: {0}" -f $forbidden)
}

Assert-NotContains $mainForm 'Hp8C40M8CThermalQualificationInjection' 'M8C synthetic injection leaked into production GUI/runtime.'
Assert-NotContains $mainForm 'M8C_SYNTHETIC_QUALIFICATION_ONLY' 'M8C synthetic evidence marker leaked into production GUI/runtime.'

foreach($needle in @(
    'TestCpuFiveUniqueSamplesAsync',
    'TestGpuImmediateAsync',
    'TestCpuHardImmediateAsync',
    'TestThermalPreemptionCancelsInFlightApplyAsync',
    'coordinator.EnforceSafetyAsync(',
    'coordinator.ApplyAsync(',
    'RequiredConsecutiveCpuSamples',
    'backend.RestoreCalls == 1',
    'FanAuthority.Firmware'
)){
    Assert-Contains $selfTest $needle ("M8C self-test invariant missing: {0}" -f $needle)
}

Assert-NotContains $selfTest 'Hp8C40FanControlBackend(' 'M8C synthetic self-test must not construct the real HP backend.'
Assert-NotContains $selfTest 'Hp8C40FanHardware(' 'M8C synthetic self-test must not construct real HP fan hardware.'
Assert-NotContains $selfTest 'NamedPipeFanControlWatchdogLeaseClient' 'M8C synthetic self-test must not touch the watchdog service.'

Assert-Contains $cli '--8c40-m8c-self-test' 'M8C synthetic CLI switch missing.'
Assert-Contains $program 'Hp8C40M8CThermalPreemptionSelfTest' 'M8C synthetic CLI route missing.'

Assert-True ([bool]$profile.loadThermalM8Qualification.m8a.physicalPassed) 'M8C code preparation requires M8A physical PASS.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8b.physicalPassed) 'M8C must require the now-recorded M8B physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalPassed) 'M8C must remain physically unvalidated.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8c.physicalExecutionAuthorized) 'M8C retry must be blocked after attempt 1 FAIL_CLOSED pending evidence review.'
Assert-True ([bool]$profile.loadThermalM8Qualification.m8c.requiresM8BPhysicalPass) 'M8C must retain the M8B physical prerequisite.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M8C code preparation must not enable automatic policy.'
Assert-False ([bool]$profile.loadThermalM8Qualification.watchdogRecoveryValidated) 'M8C code preparation must not promote watchdog recovery.'
Assert-Equal $profile.loadThermalM8Qualification.m8c.syntheticSelfTestMode '--8c40-m8c-self-test' 'M8C self-test mode changed.'
Assert-Equal $profile.loadThermalM8Qualification.m8c.syntheticEvidenceMarker 'M8C_SYNTHETIC_QUALIFICATION_ONLY' 'M8C evidence marker changed.'

foreach($needle in @(
    'M8C - physical preemption path with qualification-only thermal injection',
    'five unique consecutive synthetic snapshots',
    'GPU = 87 C',
    'effective CPU >=99 C',
    'M8C synthetic preparation',
    'M8C attempt 1 - FAIL_CLOSED before READY'
)){
    Assert-Contains $doc $needle ("M8C documentation invariant missing: {0}" -f $needle)
}

Write-Host 'HP 8C40 M8C synthetic thermal-preemption invariant self-test: PASS' -ForegroundColor Green
