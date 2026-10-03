$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$Path) { Get-Content -LiteralPath (Join-Path $root $Path) -Raw }
function Require-Text([string]$Text, [string]$Needle) {
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) { throw "WMI telemetry invariant missing: $Needle" }
}
$reader = Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanTelemetryReader.cs'
$hardware = Read-Source 'src\VictusFanControl\Telemetry\HardwareTelemetryReader.cs'
$worker = Read-Source 'src\VictusFanControl.App\TelemetryWorker.cs'
$snapshot = Read-Source 'src\VictusFanControl\Telemetry\TelemetrySnapshot.cs'
$workflow = Read-Source '.github\workflows\build.yml'
Require-Text $reader 'Hp8C40BiosFanControl.BuildGetFanLevelRequest()'
Require-Text $reader 'private static readonly SemaphoreSlim ProductionAdmission = new(1, 1);'
Require-Text $reader 'QueryTimeoutMilliseconds = 5000'
Require-Text $reader 'MaximumSampleAgeMilliseconds = 3000'
Require-Text $reader '_epoch != epoch'
Require-Text $reader '_admission.Wait(0)'
foreach ($forbidden in @('SetFanLevel(', 'RestoreFirmwareAuto(', 'AcpiEcReader', 'PawnIo', 'BuildReleaseFanLevelRequest(')) {
    if ($reader.IndexOf($forbidden, [StringComparison]::Ordinal) -ge 0) { throw "WMI reader must be read-only: $forbidden" }
}
Require-Text $hardware '_targetProfile == Hp8C40TargetProfile.Instance'
Require-Text $hardware 'if (_wmiFans is null && _targetProfile is not null) InitializeEc();'
Require-Text $hardware 'if (_wmiFans is null && _targetProfile is not null && _ec is null'
Require-Text $hardware 'wmiFanSample = _wmiFans.ReadCached();'
Require-Text $worker '_reader?.PauseFanTelemetry();'
Require-Text $worker 'await _reader.WaitForFanTelemetryQuiescenceAsync(timeoutCts.Token)'
Require-Text $snapshot 'FanSampleAgeMilliseconds is >= 0 and < HpWmiFanTelemetryReader.MaximumSampleAgeMilliseconds'
Require-Text $workflow '--fan-wmi-telemetry-self-test'
$p16 = Read-Source 'release\p16-target-checkpoint.json' | ConvertFrom-Json
if ($p16.normalManual.executionAuthorized -or $p16.normalManual.controllerPhysicalExecutionAuthorized -or
    $p16.normalManual.physicalGatesOpened -or $p16.normalManual.physicalPassed) {
    throw 'WMI telemetry migration must retain the closed P16 physical gates.'
}
Write-Host 'PASS: HP 8C40 read-only WMI fan telemetry invariants; P16 hardware gates closed.'
