$ErrorActionPreference='Stop'

$repoRoot=Split-Path -Parent $PSScriptRoot
$preflight=Get-Content (Join-Path $PSScriptRoot 'test-8c40-load-thermal-m8-preflight.ps1') -Raw
$probe=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Hardware\Hp\Hp8C40M8ReadOnlyPreflightProbe.cs') -Raw
$program=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Program.cs') -Raw
$cli=Get-Content (Join-Path $repoRoot 'src\VictusFanControl\Cli\CliOptions.cs') -Raw

function Assert-Contains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -lt 0){throw $Message}
}
function Assert-NotContains([string]$Text,[string]$Needle,[string]$Message){
    if($Text.IndexOf($Needle,[StringComparison]::Ordinal) -ge 0){throw $Message}
}

Assert-Contains $preflight 'M8 LOAD/THERMAL NO-WRITE PREFLIGHT' 'M8 preflight identity is missing.'
Assert-Contains $preflight 'Assert-RepositoryHead' 'M8 preflight must bind evidence to branch/HEAD provenance.'
Assert-Contains $preflight '--untracked-files=all' 'M8 preflight must inspect all untracked paths before allowing historical evidence.'
Assert-Contains $preflight "StartsWith('?? logs/'" 'M8 preflight may exempt only untracked historical evidence under logs/.'
Assert-Contains $preflight '$blockingStatus' 'M8 preflight must still reject every non-evidence working-tree change.'
Assert-Contains $preflight 'Preserved untracked historical evidence under logs/' 'M8 preflight must explicitly report preserved evidence instead of deleting it.'
Assert-Contains $preflight "feature/victus-8c40-m8b-diagnostics" 'M8 preflight must require the dedicated M8B diagnostic qualification branch.'
Assert-Contains $preflight 'Assert-Exact8C40Target' 'M8 preflight must exact-match HP 8C40.'
Assert-Contains $preflight 'Assert-ProfileBoundary' 'M8 preflight must verify M6/M7 and policy/profile boundaries.'
Assert-Contains $preflight 'Assert-AcBatterySane' 'M8 preflight must require AC/battery sanity.'
Assert-Contains $preflight 'Assert-M4ServiceBaseline' 'M8 preflight must require M4 Manual/stopped when installed.'
Assert-Contains $preflight 'Assert-ServiceUnchanged' 'M8 preflight must prove service state was not mutated.'
Assert-Contains $preflight 'Assert-StableFirmwareBaseline' 'M8 preflight must prove two consecutive independent FF/FF reads.'
Assert-Contains $preflight '--8c40-m8-preflight-probe' 'M8 preflight must execute the dedicated read-only telemetry/SafetyGate probe.'
Assert-Contains $preflight 'test-8c40-load-thermal-m8-invariants.ps1' 'M8 preflight must run the base M8 invariant.'
Assert-Contains $preflight 'test-8c40-load-thermal-m8-preflight-invariants.ps1' 'M8 preflight must run its own no-write invariant.'
Assert-Contains $preflight '--safety-self-test' 'M8 preflight must retain SafetyGate synthetic regression.'
Assert-Contains $preflight '--control-self-test' 'M8 preflight must retain coordinator synthetic regression.'
Assert-Contains $preflight '--hp-backend-self-test' 'M8 preflight must retain HP backend regression.'
Assert-Contains $preflight 'watchdogRecoveryValidated' 'M8 preflight must verify watchdog promotion remains false.'
Assert-Contains $preflight 'enabledByDefault' 'M8 preflight must verify automatic policy remains OFF.'
Assert-Contains $preflight 'No fan write, firmware restore, watchdog lease, service mutation' 'M8 preflight must state its no-write/no-mutation contract.'

foreach($forbidden in @(
    'SetFanLevel(',
    '--restore-hp-auto',
    'Start-Service',
    'Stop-Service',
    'Set-Service',
    'sc.exe ',
    '--8c40-m4-lease10',
    '--8c40-m4-lease30',
    '--8c40-m4-lease50',
    '--core-thermal-characterization',
    '--health-test-minutes',
    'shutdown.exe',
    'SetSuspendState',
    'git clean',
    'Remove-Item'
)){
    Assert-NotContains $preflight $forbidden ("M8 no-write preflight contains forbidden operation: {0}" -f $forbidden)
}

Assert-Contains $probe 'Hp8C40TargetProfile.Matches' 'M8 read-only probe must exact-match the target.'
Assert-Contains $probe 'HardwareTelemetryReader' 'M8 read-only probe must use the production telemetry reader.'
Assert-Contains $probe 'SafetyGate.Evaluate' 'M8 read-only probe must evaluate the production SafetyGate.'
Assert-Contains $probe 'SystemState.Healthy' 'M8 read-only probe must evaluate Healthy runtime state.'
Assert-Contains $probe 'fanWritePathPresent: false' 'M8 read-only probe must never advertise a write path.'
Assert-Contains $probe 'safety.PreconditionsReady' 'M8 probe must require SafetyGate preconditions without a fan backend.'
Assert-Contains $probe 'safety.TelemetryDeviceIdentityValid' 'M8 probe must require exact GPU identity.'
Assert-Contains $probe 'gpuTemperature.Value > 0' 'M8 probe must reject zero-degree GPU telemetry.'
Assert-Contains $probe 'CpuPhysicalAbortC = 90.0' 'M8 probe CPU physical abort limit must remain 90 C.'
Assert-Contains $probe 'GpuPhysicalAbortC = 82.0' 'M8 probe GPU physical abort limit must remain 82 C.'
Assert-Contains $probe 'RequiredConsecutiveHealthySamples = 3' 'M8 probe must require three consecutive healthy telemetry samples.'
Assert-Contains $probe 'M8_PREFLIGHT_TELEMETRY_PASS' 'M8 probe must emit an explicit telemetry PASS marker.'

foreach($forbidden in @(
    'SetFanLevel(',
    'RestoreFirmwareAuto',
    'FanControlCoordinator',
    'IFanControlBackend',
    'NamedPipeFanControlWatchdogLeaseClient',
    'CreateLifecycleQualificationBackend'
)){
    Assert-NotContains $probe $forbidden ("M8 read-only probe contains forbidden control dependency: {0}" -f $forbidden)
}

Assert-Contains $program 'options.Hp8C40M8PreflightProbe' 'Program route for M8 preflight probe is missing.'
Assert-Contains $program 'Hp8C40M8ReadOnlyPreflightProbe.RunAsync' 'Program must route M8 preflight to the read-only probe.'
Assert-Contains $cli 'Hp8C40M8PreflightProbe' 'CLI option property for M8 preflight probe is missing.'
Assert-Contains $cli '--8c40-m8-preflight-probe' 'CLI parser/help for M8 preflight probe is missing.'

Write-Host 'HP 8C40 M8 no-write preflight invariant self-test: PASS' -ForegroundColor Green
