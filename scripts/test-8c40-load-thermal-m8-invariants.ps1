$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$profilePath=Join-Path $repoRoot 'profiles\HP-8C40.json'
$specPath=Join-Path $repoRoot 'docs\LOAD_THERMAL_8C40_M8.md'
$targetPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40TargetProfile.cs'
$safetyPath=Join-Path $repoRoot 'src\VictusFanControl\Safety\SafetyGate.cs'
$coordinatorPath=Join-Path $repoRoot 'src\VictusFanControl\Control\FanControlCoordinator.cs'
$backendPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$factoryPath=Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\HpFanControlBackendFactory.cs'
$thermalConfirmationPath=Join-Path $repoRoot 'src\VictusFanControl\Safety\Hp8C40ThermalEmergencyConfirmation.cs'
$mainFormPath=Join-Path $repoRoot 'src\VictusFanControl.App\MainForm.cs'

$profile=Get-Content $profilePath -Raw | ConvertFrom-Json
$spec=Get-Content $specPath -Raw
$target=Get-Content $targetPath -Raw
$safety=Get-Content $safetyPath -Raw
$coordinator=Get-Content $coordinatorPath -Raw
$backend=Get-Content $backendPath -Raw
$factory=Get-Content $factoryPath -Raw
$thermalConfirmation=Get-Content $thermalConfirmationPath -Raw
$mainForm=Get-Content $mainFormPath -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}
}
function Assert-Equal($Actual,$Expected,[string]$Message){
    if($Actual -ne $Expected){throw ("{0} Expected='{1}' Actual='{2}'" -f $Message,$Expected,$Actual)}
}
function Assert-True([bool]$Value,[string]$Message){
    if(-not $Value){throw $Message}
}
function Assert-False([bool]$Value,[string]$Message){
    if($Value){throw $Message}
}

# Exact-target/profile boundary.
Assert-Equal $profile.productId '8C40' 'M8 target product ID changed.'
Assert-Equal $profile.boardVersionObserved '63.43' 'M8 board revision changed.'
Assert-Equal $profile.biosVersionValidated 'F.18' 'M8 BIOS boundary changed.'
Assert-Equal $profile.validatedTarget.systemProductName 'Victus by HP Gaming Laptop 15-fa1xxx' 'M8 system product changed.'
Assert-Equal $profile.validatedTarget.systemSkuPrefix '9D0R1LA' 'M8 SKU prefix changed.'
Assert-Equal $profile.validatedTarget.gpuName 'NVIDIA GeForce RTX 4060 Laptop GPU' 'M8 GPU identity changed.'
Assert-Equal $profile.validatedTarget.physicalCoreCount 14 'M8 physical-core count changed.'

# M6/M7 must remain closed while production watchdog/policy remain blocked.
Assert-True ([bool]$profile.lifecycle.modernStandbyM6PhysicalPassed) 'M8 requires M6 physical PASS.'
Assert-True ([bool]$profile.lifecycle.hibernationM7PhysicalPassed) 'M8 requires M7 physical PASS.'
Assert-False ([bool]$profile.lifecycle.watchdogRecoveryValidated) 'M8 preparation must not set WatchdogRecoveryValidated=true.'
Assert-False ([bool]$profile.control.enabledByDefault) 'M8 preparation must not enable automatic control by default.'

# Fan command envelope must not move.
Assert-Equal $profile.control.validatedMinimumLevel 10 'M8 minimum fan level must remain 10.'
Assert-Equal $profile.control.validatedMaximumLevel 50 'M8 maximum fan level must remain 50.'
Assert-False ([bool]$profile.control.supportsIndependentLevels) 'M8 must remain equal-only.'
Assert-Contains $target 'MinimumPhysicallyQualifiedFanLevel = 10' 'HP 8C40 target minimum is no longer 10.'
Assert-Contains $target 'MaximumPhysicallyQualifiedFanLevel = 50' 'HP 8C40 target maximum is no longer 50.'
Assert-Contains $target 'SupportsIndependentFanLevels: false' 'HP 8C40 target must reject asymmetric fan commands.'
Assert-Contains $target 'WatchdogRecoveryValidated: false' 'HP 8C40 production watchdog must remain blocked during M8 preparation.'

# Reuse the existing thermal contract exactly.
Assert-Contains $safety 'public const double CpuEmergencyC = 95.0;' 'M8 CPU emergency threshold drifted.'
Assert-Contains $safety 'public const double GpuEmergencyC = 87.0;' 'M8 GPU emergency threshold drifted.'
Assert-Contains $safety 'var effectiveCpuTemperature = snapshot?.CpuControlTemperatureC;' 'M8 must use the existing effective CPU aggregate.'
Assert-Contains $safety 'effectiveCpuTemperature.Value >= CpuEmergencyC' 'M8 CPU emergency decision path is missing.'
Assert-Contains $safety 'snapshot!.GpuTemperatureC!.Value >= GpuEmergencyC' 'M8 GPU emergency decision path is missing.'
Assert-Contains $safety 'ThermalEmergency: thermalEmergency' 'M8 requires the real SafetyGate ThermalEmergency result.'

# Exact HP 8C40 CPU transient confirmation sits above the stateless raw SafetyGate.
Assert-Contains $thermalConfirmation 'RequiredConsecutiveCpuSamples = 5' 'M8 CPU confirmation must require five unique consecutive samples.'
Assert-Contains $thermalConfirmation 'CpuHardEmergencyC = 99.0' 'M8 HP 8C40 CPU hard boundary must remain 99 C.'
Assert-Contains $thermalConfirmation 'snapshot.Timestamp > _lastObservedSnapshotTimestamp.Value' 'M8 CPU confirmation must count unique newer telemetry only.'
Assert-Contains $thermalConfirmation 'MaximumConfirmationSampleGap' 'M8 CPU confirmation must not bridge lifecycle/telemetry gaps.'
Assert-Contains $thermalConfirmation 'gpuTemperature.Value >= SafetyGate.GpuEmergencyC' 'M8 GPU emergency must remain immediate.'
Assert-Contains $thermalConfirmation 'effectiveCpu.Value >= CpuHardEmergencyC' 'M8 CPU hard emergency must remain immediate.'
Assert-Contains $mainForm '_thermalEmergencyConfirmation.Apply(' 'Production control safety evaluation must apply HP 8C40 temporal confirmation.'
Assert-Contains $mainForm '_thermalEmergencyConfirmation.Preview(' 'Display safety must preview without consuming confirmation samples.'

# Coordinator preemption/restore machinery must remain present.
Assert-Contains $coordinator 'if (!safety.CustomControlPermitted)' 'M8 requires unsafe SafetyGate enforcement.'
Assert-Contains $coordinator 'CancelActiveCommand();' 'M8 requires in-flight command preemption.'
Assert-Contains $coordinator 'Safety supervisor handoff:' 'M8 requires causal safety handoff detail.'
Assert-Contains $coordinator 'await RestoreLockedAsync(' 'M8 requires coordinator firmware restore.'

# Ownership/guard semantics must remain fail-closed and read-confirmed.
Assert-Contains $backend 'RequiredConsecutiveUnexpectedGuardSamples = 2' 'M8 guard confirmation hardening changed.'
Assert-Contains $backend 'VerifyExistingOwnership' 'M8 requires ownership rechecks.'
Assert-Contains $backend '_hardware.SetFanLevel(cpuTarget, gpuTarget);' 'M8 must continue through HP fan-level hardware abstraction.'
Assert-Contains $backend 'WaitForTachometerResponseAsync' 'M8 requires dual-tach hardware acknowledgement.'
Assert-Contains $backend 'HP 8C40 production watchdog/unattended recovery promotion remains blocked.' 'M8 preparation must keep the public watchdog path blocked.'

# Qualification bypass must not leak into the production factory.
Assert-NotContains $factory 'CreateLifecycleQualificationBackend' 'Production factory must not use a qualification-only bypass.'
Assert-Contains $factory 'production watchdog/unattended recovery' 'Factory must report the current production-watchdog block.'
Assert-Contains $factory 'automatic policy and watchdog remain OFF' 'Factory must keep automatic policy/watchdog OFF.'

# Specification itself locks the intended M8 safety boundary.
foreach($required in @(
    'M8A - representative-load admission',
    'M8B - watchdog-backed 50/50 under representative load',
    'M8C - physical preemption path with qualification-only thermal injection',
    'CPU raw threshold: effective CPU >= 95 C',
    'GPU emergency: GPU >= 87 C',
    'five consecutive',
    '99 C',
    '82 C GPU',
    'fan-stop / level 0 is forbidden',
    'asymmetric CPU/GPU commands remain unqualified',
    'EC 0x62/0x63 remain read-only diagnostics',
    'GPU temperature 0 C remains invalid/fail-closed',
    'if an independent delayed failsafe takes control',
    'Automatic/adaptive policy remains a later gate and stays OFF throughout M8'
)){
    Assert-Contains $spec $required ("M8 specification invariant missing: {0}" -f $required)
}

Write-Host 'HP 8C40 M8 load/thermal specification invariant self-test: PASS' -ForegroundColor Green
