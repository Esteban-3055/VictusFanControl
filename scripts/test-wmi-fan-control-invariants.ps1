$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Read-Source([string]$Path){Get-Content -LiteralPath (Join-Path $root $Path) -Raw}
function Require-Text([string]$Text,[string]$Needle){if($Text.IndexOf($Needle,[StringComparison]::Ordinal)-lt 0){throw "WMI control invariant missing: $Needle"}}
$backend=Read-Source 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackend.cs'
$reader=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanProofReader.cs'
$telemetry=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanTelemetryReader.cs'
$publication=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanSamplePublication.cs'
$broker=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanSampleBroker.cs'
$brokerTests=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanSampleBrokerSelfTest.cs'
$program=Read-Source 'src\VictusFanControl\Program.cs'
$proofTests=Read-Source 'src\VictusFanControl\Telemetry\HpWmiFanProofReaderSelfTest.cs'
$backendTests=Read-Source 'src\VictusFanControl\Hardware\Hp\Hp8C40FanControlBackendSelfTest.cs'
# Step 5: descriptive history can never become a control/ACK data source.
foreach($source in @($reader,$backend)){
 foreach($forbidden in @('HpWmiFanSampleWindow','HpWmiFanWindowSnapshot','ReadWindowCached(', 'ReadCached(', 'StableCpuRpm','StableGpuRpm')){
  if($source.IndexOf($forbidden,[StringComparison]::Ordinal)-ge 0){throw "WMI command proof cannot consume cache/history: $forbidden"}
 }
}
foreach($needle in @('five historical acquisitions never replace a new raw proof query','full fresh window cannot satisfy failed, invalid, expired or canceled Control reads')){Require-Text $proofTests $needle}
foreach($needle in @('TestWindowCannotReplaceCommandProofAsync(output)','favorable history native failure','one new sample then native failure','two raw confirmations with lagging median','postCommandQueries ==','proofs[0].Contains("samples=2"')){Require-Text $backendTests $needle}
Require-Text $backend 'TachometerAckTimeout: TimeSpan.FromSeconds(8)'
Require-Text $backend 'PollInterval: TimeSpan.FromMilliseconds(250)'
$first=$backend.IndexOf('internal sealed class Hp8C40FanHardware', [StringComparison]::Ordinal)
$last=$backend.IndexOf('internal readonly record struct Hp8C40FanBackendTiming', [StringComparison]::Ordinal)
$hardware=$backend.Substring($first,$last-$first)
if($hardware.IndexOf('ReadFanTachometers(',[StringComparison]::Ordinal)-ge 0){throw 'Production control cannot read direct EC tachometers or fall back.'}
foreach($needle in @('_fans.ReadFreshAsync(cancellationToken)','ReadStableFanSetpoint','ReadFanControlGuard','TachometerResolutionRpm = HpWmiFanTelemetrySample.ResolutionRpm')){Require-Text $hardware $needle}
foreach($forbidden in @('SetFanLevel(', 'RestoreFirmwareAuto(', 'AcpiEcReader', 'BuildReleaseFanLevelRequest(')){
 if($reader.IndexOf($forbidden,[StringComparison]::Ordinal)-ge 0){throw "WMI proof reader must be read-only: $forbidden"}
}
foreach($needle in @('HpWmiFanTelemetryReader.SharedReadAdmission','BuildGetFanLevelRequest()','MaximumWaitMilliseconds = 3000','pending.WaitAsync(remaining, cancellationToken)','_broker.AcquireControlAsync(_maximumWait, cancellationToken)','_broker.RunNative(lease','lease.CancelBeforeNative()')){Require-Text $reader $needle}
Require-Text $telemetry 'SharedReadAdmission => HpWmiFanSampleBroker.Production.Admission'
# Step 3 moves the old reader finally-release into one broker-owned worker.
# Check the equivalent ownership boundary, plus executable race tests, rather
# than requiring the old source spelling in each adapter.
foreach($needle in @('ConditionalWeakTable<SemaphoreSlim, HpWmiFanSampleBroker>','For(new SemaphoreSlim(1, 1))','lease.TransferToNative(this)','finally { lease.ReleaseAfterNative(); }','Interlocked.CompareExchange(ref _state, 2, 0)','Interlocked.CompareExchange(ref _state, 2, 1)')){Require-Text $broker $needle}
foreach($adapter in @($reader,$telemetry)){
 foreach($forbidden in @('_admission.Release(', '_admission.Wait(', '_admission.WaitAsync(', 'Task.Run(')){
  if($adapter.IndexOf($forbidden,[StringComparison]::Ordinal)-ge 0){throw "WMI adapters must delegate native admission/lifetime to the broker: $forbidden"}
 }
}
Require-Text $program 'HpWmiFanSampleBrokerSelfTest.RunAsync(Console.Out)'
foreach($needle in @('lease cannot be reused, cross brokers, or release native through cancellation','Control waits behind Periodic','sustained Control pressure exposes freshness loss','peak == 1')){Require-Text $brokerTests $needle}
foreach($needle in @('HpWmiFanSamplePublication.Capture(_admission)','completed.Publish(sample.Speeds)')){Require-Text $reader $needle}
foreach($needle in @('ConditionalWeakTable<SemaphoreSlim, Recipients>','WeakReference<HpWmiFanTelemetryReader>','reader.CaptureControlSampleSink(sequence)')){Require-Text $publication $needle}
foreach($needle in @('HpWmiFanSamplePublication.Register(_admission, this)','_epoch != epoch','age < 0 || age >= AcceptedMaximumAgeMilliseconds','sequence < _latestOutcomeSequence',
 'AcceptedMaximumAgeMilliseconds = productTolerance ? 10000 : MaximumSampleAgeMilliseconds','AcceptedMaximumAgeMilliseconds = MaximumSampleAgeMilliseconds')){Require-Text $telemetry $needle}
foreach($needle in @('ReadTachometerSnapshotWithinDeadlineAsync','commandCompletedAtMilliseconds','IsFreshTachometerProof','sample.FanQuerySequence > Math.Max(baseline.FanQuerySequence, lastAcceptedSequence)','sample.FanQueryStartedAtMilliseconds >= commandCompletedAtMilliseconds','baselineRpm + baselineResolutionRpm - 1 + MinimumDirectionalRpmDelta','currentRpm + currentResolutionRpm - 1 + MinimumDirectionalRpmDelta','RequiredTachConfirmationSamples = 2','MinimumDirectionalRpmDelta = 150','_hardware.ReadAdmissionStateAsync(cancellationToken)','Initial WMI baseline expired before pre-write ownership/guard acquisition.')){Require-Text $backend $needle}
$p16=Read-Source 'release\p16-target-checkpoint.json'|ConvertFrom-Json
# Migration closures remain historical; only a separately validated P16 generation 6 may open.
$dedicatedP16=($p16.status -ceq 'P16B_NORMAL_MANUAL_AUTHORIZED_AWAITING_SAME_HEAD_CI' -and
 [int]$p16.normalManual.authorization.authorizationGeneration -eq 6 -and
 $p16.normalManual.authorization.openedFromWmiCoordinationClosureHead -ceq 'c2b80dbf3b23bd0e44dc84ba8f7e65d94d701fa6' -and
 [long]$p16.normalManual.authorization.basisCiRunId -eq 37097259714 -and
 $p16.normalManual.hardeningRequired.postAttempt6.wmiCoordinationImplementation.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED' -and
 $p16.normalManual.hardeningRequired.postAttempt6.wmiCoordinationImplementation.ciValidated -and
 $p16.normalManual.controlledWmiPreparation.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED' -and
 $p16.normalManual.controlledWmiPreparation.validation.result -ceq 'SUCCESS' -and
 $p16.normalManual.hardeningRequired.softwareConcurrencyInvestigation.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED' -and
 $p16.normalManual.hardeningRequired.softwareConcurrencyInvestigation.validation.result -ceq 'SUCCESS')
$auditedP16Pass=($p16.status -ceq 'P16B_PHYSICAL_PASS_EVIDENCE_CLOSED_GATE_CLOSED' -and $p16.normalManual.evidenceClosed -and $p16.normalManual.physicalClosure.closed -and $p16.normalManual.physicalClosure.result -ceq 'PASS' -and [int]$p16.normalManual.physicalClosure.attempt -eq 7 -and $p16.normalManual.authorization.authorizationConsumed -and -not $p16.normalManual.executionAuthorized -and -not $p16.normalManual.physicalGatesOpened)
. (Join-Path $PSScriptRoot 'p16c-promotion-boundary.ps1')
if(($p16.promotion.manualExecutionAuthorized -and -not (Test-P16CManualPromotion)) -or $p16.promotion.automaticMayOpen -or ($p16.normalManual.physicalPassed -and -not $auditedP16Pass)){throw 'WMI migration cannot preclaim physical PASS or promote permanent control.'}
if(-not $dedicatedP16 -and ($p16.normalManual.executionAuthorized -or $p16.normalManual.controllerPhysicalExecutionAuthorized -or $p16.normalManual.physicalGatesOpened)){throw 'WMI migration permits only separately validated generation-6 P16 authorization.'}

$lifecycle=$p16.normalManual.hardeningRequired.postAttempt4.lifecycleImplementation
if($lifecycle.status -cne 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED' -or -not $lifecycle.ciValidated -or -not $lifecycle.closure.closed){throw 'WMI control requires formally closed P16 lifecycle session hardening.'}
$cp=Read-Source 'release\fan-wmi-control-checkpoint.json'|ConvertFrom-Json
if($cp.targetProfileId -cne 'HP-8C40-9D0R1LA-F18' -or $cp.hardwareExecution -or $cp.physicalGatesOpened -or $cp.physicalMigrationQualified -or $cp.writesChanged -or $cp.installedM4WatchdogChanged -or $cp.thermalThresholdsChanged -or $cp.legacy88F8Changed -or $cp.query.cachedProofAllowed -or $cp.query.directEcFallback -or $cp.query.nativeCancellationClaimed -or [int]$cp.query.maximumWaitMilliseconds -ne 3000 -or [int]$cp.query.resolutionRpm -ne 100 -or [int]$cp.proof.requiredConsecutiveSamples -ne 2 -or [int]$cp.proof.minimumGuaranteedDirectionalDeltaRpm -ne 150 -or $cp.proof.restoreRequiresRpm){throw 'WMI control checkpoint scope/proof boundaries mismatch.'}
if(($cp.retainedDirectEcControlRegisters -join ',') -cne '34,35,EC,F4'){throw 'WMI control must preserve ownership/raw-guard EC evidence.'}
if($cp.status -ceq 'IMPLEMENTED_CI_PENDING_GATE_CLOSED'){
 if($cp.softwareClosure.closed -or $null -ne $cp.implementationValidation){throw 'WMI control cannot pre-claim CI/closure.'}
}elseif($cp.status -ceq 'SOFTWARE_CLOSED_CI_VALIDATED_GATE_CLOSED'){
 if(-not $cp.softwareClosure.closed -or $cp.softwareClosure.result -cne 'PASS' -or $cp.implementationValidation.result -cne 'SUCCESS' -or $cp.softwareClosure.implementationHead -cne $cp.implementationValidation.head -or $cp.softwareClosure.sourceCiResult -cne 'SUCCESS'){throw 'WMI control closure requires exact successful implementation CI.'}
 if($cp.implementationValidation.head -cne 'd3df9d9b2aeef3fa9364e89e30688e5117360bcc' -or $cp.implementationValidation.tree -cne 'e169413472735adf2723c6622eebaf7e89c402b7' -or [long]$cp.implementationValidation.runId -ne 37090519386 -or [int]$cp.implementationValidation.runNumber -ne 1206 -or -not $cp.implementationValidation.fullWindowsCi -or -not $cp.implementationValidation.windowsPowerShell51Validated -or -not $cp.implementationValidation.deterministicRcPackageValidated){throw 'WMI control implementation CI identity changed.'}
 if($cp.softwareClosure.implementationTree -cne $cp.implementationValidation.tree -or [long]$cp.softwareClosure.sourceCiRunId -ne [long]$cp.implementationValidation.runId -or [int]$cp.softwareClosure.sourceCiRunNumber -ne [int]$cp.implementationValidation.runNumber -or $cp.softwareClosure.hardwareExecution -or -not $cp.softwareClosure.separateSameHeadClosureCiRequired){throw 'WMI control closure cannot substitute source CI or claim hardware qualification.'}
 if([int]$cp.implementationValidation.querySelfTestCases -ne 5 -or [int]$cp.implementationValidation.additionalBackendSelfTestCases -ne 10){throw 'WMI control validation coverage changed.'}
}else{throw 'Unexpected WMI control checkpoint status.'}
Write-Host 'PASS: WMI control RPM proof; EC ownership/guards preserved; physical gates closed.'
