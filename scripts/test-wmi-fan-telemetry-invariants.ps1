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
$safety = Read-Source 'src\VictusFanControl\Safety\SafetyGate.cs'
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
Require-Text $snapshot 'IsFanTelemetryFreshAt(DateTimeOffset now)'
Require-Text $safety '!snapshot.IsFanTelemetryFreshAt(now)'
$p16 = Read-Source 'release\p16-target-checkpoint.json' | ConvertFrom-Json
if ($p16.normalManual.executionAuthorized -or $p16.normalManual.controllerPhysicalExecutionAuthorized -or
    $p16.normalManual.physicalGatesOpened -or $p16.normalManual.physicalPassed) {
    throw 'WMI telemetry migration must retain the closed P16 physical gates.'
}
$checkpoint = Read-Source 'release\fan-wmi-telemetry-checkpoint.json' | ConvertFrom-Json
if ($checkpoint.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or
    $checkpoint.hardwareExecution -or $checkpoint.physicalGatesOpened -or
    $checkpoint.p16QualificationLifecycleLatchCompleted -or $checkpoint.query.directEcFallback -or
    $checkpoint.query.command -cne '0x00020008' -or $checkpoint.query.commandType -cne '0x2D' -or
    [int]$checkpoint.query.resolutionRpm -ne 100 -or [int]$checkpoint.query.maximumSampleAgeMs -ne 3000 -or
    [int]$checkpoint.query.logicalTimeoutMs -ne 5000) {
    throw 'WMI telemetry checkpoint scope/freshness/hardware contract mismatch.'
}
if ($checkpoint.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED') {
    $validation = $checkpoint.implementationValidation
    $closure = $checkpoint.softwareClosure
    if (-not $closure.closed -or $closure.result -cne 'PASS' -or
        $closure.hardwareExecution -or $closure.physicalGatesOpened -or $closure.p16Promoted -or
        $validation.head -cne 'f41f390c682994af08807b28abcfa256b967722f' -or
        $validation.tree -cne '1cfbfcccf9270f747ef4a91ec21f4e7e105df23a' -or
        [long]$validation.runId -ne 37081921838 -or [int]$validation.runNumber -ne 1201 -or
        $validation.result -cne 'SUCCESS' -or [int]$validation.fakeTransportCases -ne 8 -or
        $closure.implementationHead -cne $validation.head -or
        [long]$closure.sourceCiRunId -ne [long]$validation.runId -or
        $closure.sourceCiResult -cne 'SUCCESS') {
        throw 'WMI telemetry software closure lacks exact same-head CI evidence.'
    }
    foreach ($property in $closure.audit.PSObject.Properties) {
        if (-not [bool]$property.Value) { throw "WMI telemetry audit failed: $($property.Name)" }
    }
} elseif ($checkpoint.status -cne 'IMPLEMENTED_CI_PENDING_GATE_CLOSED' -or $checkpoint.softwareClosure.closed) {
    throw 'Unexpected WMI telemetry software checkpoint status.'
}
Write-Host 'PASS: HP 8C40 read-only WMI fan telemetry invariants; P16 hardware gates closed.'
