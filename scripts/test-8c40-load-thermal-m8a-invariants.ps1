$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$script=Get-Content (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8a.ps1') -Raw
$source=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8RepresentativeLoadQualificationTest.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$cli=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs') -Raw
$profile=Get-Content (Join-Path $repoRoot 'profiles\HP-8C40.json') -Raw | ConvertFrom-Json
$doc=Get-Content (Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}
}
function Assert-Equal($Actual,$Expected,[string]$Message){
    if($Actual -ne $Expected){throw ("{0} Expected='{1}' Actual='{2}'" -f $Message,$Expected,$Actual)}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}

Assert-Contains $script 'M8A REPRESENTATIVE-LOAD ADMISSION (NO-WRITE)' 'M8A script identity missing.'
Assert-Contains $script 'test-8c40-load-thermal-m8-preflight.ps1' 'M8A must rerun the versioned M8 no-write preflight.'
Assert-Contains $script 'type M8A and press Enter' 'M8A must require explicit operator confirmation after real workload is active.'
Assert-Contains $script '--8c40-m8a-representative-load' 'M8A script must invoke the dedicated read-only mode.'
Assert-Contains $script '--8c40-m8a-result-path' 'M8A must persist machine-readable evidence.'
Assert-Contains $script 'Assert-FinalStableFirmwareOwnership' 'M8A must independently prove final FF/FF twice.'
Assert-Contains $script 'M8B remains blocked' 'M8A PASS must not authorize M8B automatically.'

Assert-NotContains $source '_ = EvaluateAndValidateSnapshot(' 'M8A warm-up sample must not require complete differential telemetry.'

foreach($forbidden in @(
    'SetFanLevel(',
    '--restore-hp-auto',
    'RestoreFirmwareAuto',
    'Start-Service',
    'Stop-Service',
    'Set-Service',
    'sc.exe ',
    '--8c40-m4-lease10',
    '--8c40-m4-lease30',
    '--8c40-m4-lease50',
    'shutdown.exe',
    'SetSuspendState',
    'git clean',
    'Remove-Item'
)){
    Assert-NotContains $script $forbidden ("M8A no-write script contains forbidden operation: {0}" -f $forbidden)
}

foreach($required in @(
    'QualificationSamples = 60',
    'SampleIntervalMilliseconds = 1000',
    'MinimumRepresentativeSamples = 45',
    'MinimumConsecutiveRepresentativeSamples = 10',
    'MinimumGpuLoadPercent = 35.0',
    'MinimumGpuPowerW = 20.0',
    'MinimumCpuLoadPercent = 5.0',
    'MinimumCpuPackagePowerW = 15.0',
    'CpuPhysicalAbortC = 90.0',
    'GpuPhysicalAbortC = 82.0',
    'EnsurePhysicalAbortLimits(warmup);',
    'telemetry.ResetHealthWindow();',
    'EcEvidenceIntervalSamples = 5',
    'MaximumUnexpectedEcConfirmationReads = 3',
    'RequiredConsecutiveUnexpectedEcSamples = 2',
    'UnexpectedEcConfirmationDelayMilliseconds = 25',
    'fanWritePathPresent: false',
    'snapshot.IsComplete',
    'snapshot.CpuCoreTelemetryComplete',
    'safety.PreconditionsReady',
    'safety.TelemetryDeviceIdentityValid',
    'SystemPowerStatusReader.Read()',
    'ReadControlEvidence()',
    'M8A evidence:'
)){
    Assert-Contains $source $required ("M8A source invariant missing: {0}" -f $required)
}

foreach($forbidden in @(
    'SetFanLevel(',
    'RestoreFirmwareAuto',
    'FanControlCoordinator',
    'IFanControlBackend',
    'NamedPipeFanControlWatchdogLeaseClient',
    'CreateLifecycleQualificationBackend'
)){
    Assert-NotContains $source $forbidden ("M8A read-only source contains forbidden control dependency: {0}" -f $forbidden)
}

Assert-Contains $program 'Hp8C40M8RepresentativeLoadQualificationTest.RunAsync' 'Program M8A physical route missing.'
Assert-Contains $program 'Hp8C40M8RepresentativeLoadQualificationTest' 'Program M8A self-test route missing.'
Assert-Contains $cli '--8c40-m8a-representative-load' 'CLI M8A mode missing.'
Assert-Contains $cli '--8c40-m8a-result-path' 'CLI M8A result path missing.'
Assert-Contains $cli '--8c40-m8a-self-test' 'CLI M8A classifier self-test missing.'

Assert-Equal $profile.loadThermalM8Qualification.representativeLoadHarnessScript 'scripts/test-8c40-load-thermal-m8a.ps1' 'M8A profile harness path changed.'
Assert-False ([bool]$profile.loadThermalM8Qualification.m8a.physicalPassed) 'M8A code preparation must not mark physical PASS.'
Assert-False ([bool]$profile.loadThermalM8Qualification.watchdogRecoveryValidated) 'M8A must not promote watchdog recovery.'
Assert-False ([bool]$profile.loadThermalM8Qualification.automaticPolicyEnabled) 'M8A must not enable automatic policy.'

Assert-Contains $doc '60 samples at 1 second' 'M8A numeric duration criteria must be versioned in docs.'
Assert-Contains $doc '45/60' 'M8A representative-sample criterion must be versioned in docs.'
Assert-Contains $doc '10 consecutive' 'M8A sustained-streak criterion must be versioned in docs.'
Assert-Contains $doc 'GPU load >= 35%' 'M8A GPU-load threshold must be versioned in docs.'
Assert-Contains $doc 'GPU power >= 20 W' 'M8A GPU-power threshold must be versioned in docs.'
Assert-Contains $doc 'CPU load >= 5%' 'M8A CPU-load threshold must be versioned in docs.'
Assert-Contains $doc 'CPU package power >= 15 W' 'M8A CPU-power threshold must be versioned in docs.'

Write-Host 'HP 8C40 M8A representative-load no-write harness invariant self-test: PASS' -ForegroundColor Green
