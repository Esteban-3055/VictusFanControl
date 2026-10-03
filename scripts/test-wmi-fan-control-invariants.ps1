$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Read-Source([string]$Path){Get-Content -LiteralPath (Join-Path $root $Path) -Raw}
function Require-Text([string]$Text,[string]$Needle){if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw "WMI control invariant missing: $Needle"}}
$backend=Read-Source 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$reader=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanProofReader.cs'
$telemetry=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanTelemetryReader.cs'
$first=$backend.IndexOf('internal sealed class Hp8C40FanHardware', [StringComparison]::Ordinal)
$last=$backend.IndexOf('internal readonly record struct Hp8C40FanBackendTiming', [StringComparison]::Ordinal)
$hardware=$backend.Substring($first,$last-$first)
if($hardware.IndexOf('ReadFanTachometers(',[StringComparison]::Ordinal)-ge 0){throw 'Production control cannot read direct EC tachometers or fall back.'}
foreach($needle in @('_fans.ReadFreshAsync(cancellationToken)','ReadStableFanSetpoint','ReadFanControlGuard','TachometerResolutionRpm = HpWmiFanTelemetrySample.ResolutionRpm')){Require-Text $hardware $needle}
foreach($forbidden in @('SetFanLevel(', 'RestoreFirmwareAuto(', 'AcpiEcReader', 'BuildReleaseFanLevelRequest(')){
 if($reader.IndexOf($forbidden,[StringComparison]::Ordinal)-ge 0){throw "WMI proof reader must be read-only: $forbidden"}
}
foreach($needle in @('HpWmiFanTelemetryReader.SharedReadAdmission','BuildGetFanLevelRequest()','MaximumWaitMilliseconds = 3000','pending.WaitAsync(remaining, cancellationToken)','finally { _admission.Release(); }')){Require-Text $reader $needle}
Require-Text $telemetry 'SharedReadAdmission => ProductionAdmission'
foreach($needle in @('ReadTachometerSnapshotWithinDeadlineAsync','commandCompletedAtMilliseconds','IsFreshTachometerProof','sample.FanQuerySequence > Math.Max(baseline.FanQuerySequence, lastAcceptedSequence)','sample.FanQueryStartedAtMilliseconds >= commandCompletedAtMilliseconds','baselineRpm + baselineResolutionRpm - 1 + MinimumDirectionalRpmDelta','currentRpm + currentResolutionRpm - 1 + MinimumDirectionalRpmDelta','RequiredTachConfirmationSamples = 2','MinimumDirectionalRpmDelta = 150','_hardware.ReadAdmissionStateAsync(cancellationToken)','Initial WMI baseline expired before pre-write ownership/guard acquisition.')){Require-Text $backend $needle}
$p16=Read-Source 'release\p16-target-checkpoint.json'|ConvertFrom-Json
if($p16.normalManual.executionAuthorized -or $p16.normalManual.controllerPhysicalExecutionAuthorized -or $p16.normalManual.physicalGatesOpened -or $p16.promotion.manualExecutionAuthorized -or $p16.promotion.automaticMayOpen){throw 'WMI control implementation cannot open physical/permanent gates.'}
$lifecycle=$p16.normalManual.hardeningRequired.postAttempt4.lifecycleImplementation
if($lifecycle.status -cne 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED' -or -not $lifecycle.ciValidated -or -not $lifecycle.closure.closed){throw 'WMI control requires formally closed P16 lifecycle session hardening.'}
$cp=Read-Source 'release\fan-wmi-control-checkpoint.json'|ConvertFrom-Json
if($cp.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or $cp.hardwareExecution -or $cp.physicalGatesOpened -or $cp.physicalMigrationQualified -or $cp.writesChanged -or $cp.installedM4WatchdogChanged -or $cp.thermalThresholdsChanged -or $cp.legacy88F8Changed -or $cp.query.cachedProofAllowed -or $cp.query.directEcFallback -or $cp.query.nativeCancellationClaimed -or [int]$cp.query.maximumWaitMilliseconds -ne 3000 -or [int]$cp.query.resolutionRpm -ne 100 -or [int]$cp.proof.requiredConsecutiveSamples -ne 2 -or [int]$cp.proof.minimumGuaranteedDirectionalDeltaRpm -ne 150 -or $cp.proof.restoreRequiresRpm){throw 'WMI control checkpoint scope/proof boundaries mismatch.'}
if(($cp.retainedDirectEcControlRegisters -join ',') -cne '34,35,EC,F4'){throw 'WMI control must preserve ownership/raw-guard EC evidence.'}
if($cp.status -ceq 'IMPLEMENTED_CI_PENDING_GATE_CLOSED'){
 if($cp.softwareClosure.closed -or $null -ne $cp.implementationValidation){throw 'WMI control cannot pre-claim CI/closure.'}
}elseif($cp.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED'){
 if(-not $cp.softwareClosure.closed -or $cp.softwareClosure.result -cne 'PASS' -or $cp.implementationValidation.result -cne 'SUCCESS' -or $cp.softwareClosure.implementationHead -cne $cp.implementationValidation.head -or $cp.softwareClosure.sourceCiResult -cne 'SUCCESS'){throw 'WMI control closure requires exact successful implementation CI.'}
}else{throw 'Unexpected WMI control checkpoint status.'}
Write-Host 'PASS: WMI control RPM proof; EC ownership/guards preserved; physical gates closed.'
