using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

public enum Hp8C40M8CPhysicalCase
{
    CpuConfirmed95,
    GpuImmediate87
}

/// <summary>
/// Prepared-but-blocked M8C physical thermal-preemption controller.
///
/// The complete real-hardware sequence is compiled so it can be reviewed and
/// tested statically before M8B closes, but PhysicalExecutionAuthorized remains
/// false until a later explicit commit made from real M8B PASS evidence.
/// </summary>
public static class Hp8C40M8CPhysicalThermalPreemptionQualificationTest
{
    public static readonly bool PhysicalExecutionAuthorized = false;
    public const string RequiredToken = "8C40-M8C-THERMAL50";
    public const int QualificationLevel = 50;

    private const int MaximumPreWriteSamples = 10;
    private const int RequiredConsecutiveRepresentativeSamples = 3;
    private const byte MinimumBatteryPercent = 20;

    private static readonly TimeSpan SampleInterval =
        TimeSpan.FromSeconds(1);

    private static readonly TimeSpan MaximumTelemetryAge =
        TimeSpan.FromSeconds(3);

    private static readonly TimeSpan MaximumInterSampleGap =
        TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ParentContinueTimeout =
        TimeSpan.FromSeconds(30);

    public static bool TryParseCase(
        string value,
        out Hp8C40M8CPhysicalCase @case)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "cpu":
            case "cpu95":
                @case =
                    Hp8C40M8CPhysicalCase.CpuConfirmed95;
                return true;

            case "gpu":
            case "gpu87":
                @case =
                    Hp8C40M8CPhysicalCase.GpuImmediate87;
                return true;

            default:
                @case = default;
                return false;
        }
    }

    public static async Task<int> RunAsync(
        string modulesDirectory,
        Hp8C40M8CPhysicalCase @case,
        string readyPath,
        string continuePath,
        string resultPath,
        CancellationToken cancellationToken)
    {
        // This authorization test MUST remain before every hardware identity,
        // PawnIO, watchdog, WMI or EC construction. It is the compile-time
        // execution barrier while M8B physical retry is still pending.
        if (!PhysicalExecutionAuthorized)
        {
            Console.Error.WriteLine(
                "M8C PHYSICAL REFUSED: code is prepared but physical execution " +
                "is not authorized until M8B has a recorded physical PASS.");
            return 222;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var result = "FAIL_CLOSED";
        string? failureReason = null;
        var exitCode = 223;

        var events = new List<EventEvidence>();
        var synthetic = new List<SyntheticEvidence>();
        var preWrite = new List<PreWriteEvidence>();

        var applyCalls = 0;
        var customWasOwned = false;
        var finalFirmwareOwned = false;
        var normalThermalHandoffCompleted = false;
        var readyPublished = false;
        var continueObserved = false;

        FanControlCoordinator? coordinator = null;
        Hp8C40EcControlStateProbe? ecProbe = null;

        try
        {
            if (!IsAdministrator())
            {
                throw new InvalidOperationException(
                    "M8C requires an elevated Administrator process.");
            }

            var hardware =
                HardwareIdentityReader.ReadCurrent();

            if (!Hp8C40TargetProfile.Matches(
                    hardware,
                    out var targetReason))
            {
                throw new InvalidOperationException(
                    $"M8C exact-target refusal: {targetReason}");
            }

            EnsurePowerStatus(
                SystemPowerStatusReader.Read());

            ecProbe =
                new Hp8C40EcControlStateProbe(
                    modulesDirectory);

            var initial =
                await ReadExpectedEcConfirmedAsync(
                        ecProbe,
                        byte.MaxValue,
                        byte.MaxValue,
                        "initial firmware ownership",
                        cancellationToken)
                    .ConfigureAwait(false);

            EnsureExpectedEc(
                initial,
                byte.MaxValue,
                byte.MaxValue,
                "initial firmware ownership");

            using var telemetry =
                new HardwareTelemetryReader(
                    modulesDirectory);

            if (!telemetry.BackendsInitialized ||
                !string.Equals(
                    telemetry.TargetProfile?.Id,
                    Hp8C40TargetProfile.Instance.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "M8C exact-target telemetry backends are not fully initialized.");
            }

            var confirmation =
                new Hp8C40ThermalEmergencyConfirmation();

            var warmup =
                telemetry.ReadSnapshot();

            EnsureImmediatePhysicalLimits(
                warmup,
                "warm-up");

            telemetry.ResetHealthWindow();

            await Task.Delay(
                    SampleInterval,
                    cancellationToken)
                .ConfigureAwait(false);

            TelemetrySnapshot? admittedSnapshot = null;
            SafetyGateResult? admittedSafety = null;
            DateTimeOffset? previousTimestamp = null;
            var representativeStreak = 0;

            for (var index = 1;
                 index <= MaximumPreWriteSamples;
                 index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePowerStatus(
                    SystemPowerStatusReader.Read());

                var snapshot =
                    telemetry.ReadSnapshot();

                var safety =
                    EvaluateEffectiveSafety(
                        hardware,
                        snapshot,
                        previousTimestamp,
                        confirmation,
                        fanWritePathPresent: true);

                previousTimestamp =
                    snapshot.Timestamp;

                EnsureImmediatePhysicalLimits(
                    snapshot,
                    $"pre-write sample {index}");

                if (!safety.PreconditionsReady ||
                    !safety.CustomControlPermitted ||
                    safety.ThermalEmergency)
                {
                    throw new InvalidOperationException(
                        "M8C real pre-write SafetyGate refused Custom: " +
                        string.Join(" | ", safety.Reasons));
                }

                var representative =
                    IsRepresentativeLoad(snapshot);

                representativeStreak =
                    representative
                        ? representativeStreak + 1
                        : 0;

                preWrite.Add(
                    new PreWriteEvidence(
                        index,
                        snapshot.Timestamp,
                        representative,
                        representativeStreak,
                        snapshot.CpuControlTemperatureC!.Value,
                        snapshot.CpuPackagePowerW!.Value,
                        snapshot.CpuLoadPercent!.Value,
                        snapshot.GpuTemperatureC!.Value,
                        snapshot.GpuPowerW!.Value,
                        snapshot.GpuLoadPercent!.Value));

                if (representativeStreak >=
                    RequiredConsecutiveRepresentativeSamples)
                {
                    admittedSnapshot = snapshot;
                    admittedSafety = safety;
                    break;
                }

                if (index < MaximumPreWriteSamples)
                {
                    await Task.Delay(
                            SampleInterval,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (admittedSnapshot is null ||
                admittedSafety is null)
            {
                throw new InvalidOperationException(
                    "M8C representative load was not established before Custom admission.");
            }

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "REPRESENTATIVE_LOAD_ADMITTED",
                    $"{representativeStreak} consecutive real representative samples"));

            var preCustom =
                await ReadExpectedEcConfirmedAsync(
                        ecProbe,
                        byte.MaxValue,
                        byte.MaxValue,
                        "immediately before Custom admission",
                        cancellationToken)
                    .ConfigureAwait(false);

            EnsureExpectedEc(
                preCustom,
                byte.MaxValue,
                byte.MaxValue,
                "immediately before Custom admission");

            var lease =
                new NamedPipeFanControlWatchdogLeaseClient(
                    Hp8C40TargetProfile.Instance.Id,
                    FanControlWatchdogLeaseContract.Hp8C40M4PipeName);

            var realHardware =
                new Hp8C40FanHardware(
                    modulesDirectory);

            var backend =
                new Hp8C40FanControlBackend(
                    realHardware,
                    targetSupported: true,
                    supportDetail:
                        "HP 8C40 M8C qualification-only real 50/50 + synthetic thermal-preemption path.",
                    watchdogLease: lease);

            coordinator =
                new FanControlCoordinator(
                    backend);

            if (!await coordinator.TryEnterCustomAsync(
                    admittedSafety,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "M8C coordinator did not acquire watchdog-backed Custom authority.");
            }

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "CUSTOM_ADMITTED_PREPARED",
                    "Watchdog PREPARE acknowledged; no fan-level write yet."));

            applyCalls++;

            await coordinator.ApplyAsync(
                    new FanCommand(
                        QualificationLevel,
                        QualificationLevel,
                        "HP 8C40 M8C physical thermal-preemption qualification"),
                    admittedSafety,
                    cancellationToken)
                .ConfigureAwait(false);

            customWasOwned = true;

            if (applyCalls != 1)
            {
                throw new InvalidOperationException(
                    "M8C must issue exactly one 50/50 ApplyAsync call.");
            }

            var owned =
                await ReadExpectedEcConfirmedAsync(
                        ecProbe,
                        QualificationLevel,
                        QualificationLevel,
                        "post-Commit real ownership",
                        cancellationToken)
                    .ConfigureAwait(false);

            EnsureExpectedEc(
                owned,
                QualificationLevel,
                QualificationLevel,
                "post-Commit real ownership");

            if (owned.CpuRpm == 0 ||
                owned.GpuRpm == 0)
            {
                throw new InvalidOperationException(
                    "M8C post-Commit real dual-tach evidence is zero.");
            }

            // Start a new real-telemetry epoch after the synchronous control
            // transaction, as hardened by the M8B attempt-1 finding.
            previousTimestamp = null;

            await Task.Delay(
                    SampleInterval,
                    cancellationToken)
                .ConfigureAwait(false);

            TelemetrySnapshot? preInjectionSnapshot = null;

            for (var attempt = 1;
                 attempt <= MaximumPreWriteSamples;
                 attempt++)
            {
                EnsurePowerStatus(
                    SystemPowerStatusReader.Read());

                var snapshot =
                    telemetry.ReadSnapshot();

                EnsureImmediatePhysicalLimits(
                    snapshot,
                    $"pre-injection sample {attempt}");

                var safety =
                    EvaluateEffectiveSafety(
                        hardware,
                        snapshot,
                        previousTimestamp,
                        confirmation,
                        fanWritePathPresent: true);

                previousTimestamp =
                    snapshot.Timestamp;

                var representative =
                    IsRepresentativeLoad(snapshot);

                var belowCpuCandidate =
                    snapshot.CpuControlTemperatureC!.Value <
                    SafetyGate.CpuEmergencyC;

                if (representative &&
                    belowCpuCandidate &&
                    !safety.ThermalEmergency &&
                    safety.CustomControlPermitted)
                {
                    var retained =
                        await coordinator.EnforceSafetyAsync(
                                safety,
                                "M8C real pre-injection supervision",
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (!retained ||
                        coordinator.Authority !=
                            FanAuthority.Custom)
                    {
                        throw new InvalidOperationException(
                            "M8C lost Custom authority before synthetic injection.");
                    }

                    preInjectionSnapshot = snapshot;
                    break;
                }

                if (attempt < MaximumPreWriteSamples)
                {
                    await Task.Delay(
                            SampleInterval,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (preInjectionSnapshot is null)
            {
                throw new InvalidOperationException(
                    "M8C could not establish a representative real sample below the CPU temporal threshold before injection.");
            }

            var controllerIdentity =
                GetCurrentProcessIdentity();

            await WriteJsonAsync(
                    readyPath,
                    new
                    {
                        schemaVersion = 1,
                        gate = "M8C",
                        caseName = @case.ToString(),
                        targetProfileId =
                            Hp8C40TargetProfile.Instance.Id,
                        processId =
                            controllerIdentity.ProcessId,
                        processStartUtcTicks =
                            controllerIdentity.ProcessStartUtcTicks,
                        timestampUtc =
                            DateTimeOffset.UtcNow,
                        authority =
                            coordinator.Authority.ToString(),
                        cpuSetpoint =
                            owned.CpuSetpoint,
                        gpuSetpoint =
                            owned.GpuSetpoint,
                        cpuRpm =
                            owned.CpuRpm,
                        gpuRpm =
                            owned.GpuRpm,
                        ack =
                            "real-backend-ec+tachs+watchdog-owned;synthetic-not-yet-injected",
                        applyCalls
                    })
                .ConfigureAwait(false);

            readyPublished = true;

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "M8C_READY_BEFORE_INJECTION",
                    "Real watchdog-backed 50/50 is OWNED; waiting for parent journal/failsafe verification."));

            await WaitForParentContinueAsync(
                    continuePath,
                    cancellationToken)
                .ConfigureAwait(false);

            continueObserved = true;

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "PARENT_CONTINUE_ACK",
                    "Parent authorized only the already-prepared synthetic threshold subcycle."));

            switch (@case)
            {
                case Hp8C40M8CPhysicalCase.CpuConfirmed95:
                    await ExecuteCpuSyntheticCaseAsync(
                            hardware,
                            confirmation,
                            coordinator,
                            synthetic,
                            events,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Hp8C40M8CPhysicalCase.GpuImmediate87:
                    await ExecuteGpuSyntheticCaseAsync(
                            hardware,
                            confirmation,
                            coordinator,
                            synthetic,
                            events,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported M8C physical case '{@case}'.");
            }

            if (coordinator.Authority !=
                FanAuthority.Firmware)
            {
                throw new InvalidOperationException(
                    "M8C synthetic thermal handoff did not end in firmware authority.");
            }

            customWasOwned = false;
            normalThermalHandoffCompleted = true;

            var final =
                await ReadExpectedEcConfirmedAsync(
                        ecProbe,
                        byte.MaxValue,
                        byte.MaxValue,
                        "post-preemption local restore",
                        CancellationToken.None)
                    .ConfigureAwait(false);

            EnsureExpectedEc(
                final,
                byte.MaxValue,
                byte.MaxValue,
                "post-preemption local restore");

            finalFirmwareOwned = true;
            result = "PASS_CONTROLLER_LOCAL_CLOSURE";
            exitCode = 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            failureReason =
                "OperationCanceledException: operator/lifecycle cancellation";
            Console.Error.WriteLine(
                $"M8C FAIL_CLOSED: {failureReason}");
        }
        catch (Exception ex)
        {
            failureReason =
                $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(
                $"M8C FAIL_CLOSED: {failureReason}");
        }
        finally
        {
            if (coordinator is not null)
            {
                if (coordinator.Authority ==
                    FanAuthority.Custom)
                {
                    try
                    {
                        await coordinator.RestoreFirmwareAsync(
                                "M8C finally fallback",
                                CancellationToken.None)
                            .ConfigureAwait(false);

                        customWasOwned = false;
                    }
                    catch (Exception restoreFailure)
                    {
                        Console.Error.WriteLine(
                            "M8C finally restore failed: " +
                            $"{restoreFailure.GetType().Name}: " +
                            restoreFailure.Message);
                    }
                }

                await coordinator.DisposeAsync()
                    .ConfigureAwait(false);
            }

            if (ecProbe is not null)
            {
                try
                {
                    var final =
                        await ReadExpectedEcConfirmedAsync(
                                ecProbe,
                                byte.MaxValue,
                                byte.MaxValue,
                                "final controller-side firmware proof",
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    finalFirmwareOwned =
                        final.CpuSetpoint == byte.MaxValue &&
                        final.GpuSetpoint == byte.MaxValue;
                }
                catch
                {
                    // The parent physical harness owns the independent final
                    // closure proof and must fail if it cannot prove it.
                }
            }

            try
            {
                await WriteJsonAsync(
                        resultPath,
                        new
                        {
                            schemaVersion = 1,
                            gate = "M8C",
                            caseName = @case.ToString(),
                            result,
                            failureReason,
                            startedUtc,
                            endedUtc =
                                DateTimeOffset.UtcNow,
                            targetProfileId =
                                Hp8C40TargetProfile.Instance.Id,
                            physicalExecutionAuthorized =
                                PhysicalExecutionAuthorized,
                            syntheticEvidenceMarker =
                                Hp8C40M8CThermalQualificationInjection.EvidenceMarker,
                            applyCalls,
                            readyPublished,
                            continueObserved,
                            customWasOwnedAtFinally =
                                customWasOwned,
                            normalThermalHandoffCompleted,
                            finalFirmwareOwned,
                            preWrite,
                            synthetic,
                            events
                        })
                    .ConfigureAwait(false);
            }
            catch (Exception evidenceFailure)
            {
                Console.Error.WriteLine(
                    "M8C evidence write failed: " +
                    $"{evidenceFailure.GetType().Name}: " +
                    evidenceFailure.Message);

                if (exitCode == 0)
                {
                    exitCode = 224;
                }
            }
        }

        return exitCode;
    }

    private static async Task ExecuteCpuSyntheticCaseAsync(
        HardwareIdentity hardware,
        Hp8C40ThermalEmergencyConfirmation confirmation,
        FanControlCoordinator coordinator,
        List<SyntheticEvidence> evidence,
        List<EventEvidence> events,
        CancellationToken cancellationToken)
    {
        var syntheticEpoch =
            DateTimeOffset.UtcNow;

        for (var ordinal = 1;
             ordinal <=
             Hp8C40ThermalEmergencyConfirmation.RequiredConsecutiveCpuSamples;
             ordinal++)
        {
            // The temporal confirmation contract advances only on unique
            // telemetry timestamps. Derive them deterministically instead of
            // relying on OS clock resolution during a tight synthetic loop.
            var frame =
                Hp8C40M8CThermalQualificationInjection
                    .CpuConfirmedThreshold(
                        syntheticEpoch +
                        TimeSpan.FromMilliseconds(ordinal),
                        ordinal);

            var evaluation =
                Hp8C40M8CThermalQualificationInjection.Evaluate(
                    hardware,
                    frame,
                    confirmation);

            var retained =
                await coordinator.EnforceSafetyAsync(
                        evaluation.Effective,
                        $"M8C synthetic CPU 95 C {ordinal}/5",
                        CancellationToken.None)
                    .ConfigureAwait(false);

            evidence.Add(
                ToEvidence(
                    evaluation,
                    retained,
                    coordinator.Authority));

            if (ordinal <
                Hp8C40ThermalEmergencyConfirmation.RequiredConsecutiveCpuSamples)
            {
                if (!retained ||
                    evaluation.Effective.ThermalEmergency ||
                    !evaluation.Effective.CustomControlPermitted ||
                    coordinator.Authority !=
                        FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        $"M8C CPU synthetic case preempted too early at {ordinal}/5.");
                }
            }
            else
            {
                if (retained ||
                    !evaluation.Effective.ThermalEmergency ||
                    evaluation.Effective.CustomControlPermitted ||
                    coordinator.Authority !=
                        FanAuthority.Firmware)
                {
                    throw new InvalidOperationException(
                        "M8C CPU synthetic case did not preempt exactly on 5/5.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        events.Add(
            new EventEvidence(
                DateTimeOffset.UtcNow,
                "CPU_SYNTHETIC_PREEMPTION_COMPLETE",
                "Five unique 95 C frames; firmware handoff occurred on 5/5."));
    }

    private static async Task ExecuteGpuSyntheticCaseAsync(
        HardwareIdentity hardware,
        Hp8C40ThermalEmergencyConfirmation confirmation,
        FanControlCoordinator coordinator,
        List<SyntheticEvidence> evidence,
        List<EventEvidence> events,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var frame =
            Hp8C40M8CThermalQualificationInjection
                .GpuImmediateThreshold(
                    DateTimeOffset.UtcNow);

        var evaluation =
            Hp8C40M8CThermalQualificationInjection.Evaluate(
                hardware,
                frame,
                confirmation);

        var retained =
            await coordinator.EnforceSafetyAsync(
                    evaluation.Effective,
                    "M8C synthetic GPU 87 C immediate",
                    CancellationToken.None)
                .ConfigureAwait(false);

        evidence.Add(
            ToEvidence(
                evaluation,
                retained,
                coordinator.Authority));

        if (retained ||
            !evaluation.Effective.ThermalEmergency ||
            evaluation.Effective.CustomControlPermitted ||
            coordinator.Authority !=
                FanAuthority.Firmware)
        {
            throw new InvalidOperationException(
                "M8C GPU synthetic case did not preempt immediately.");
        }

        events.Add(
            new EventEvidence(
                DateTimeOffset.UtcNow,
                "GPU_SYNTHETIC_PREEMPTION_COMPLETE",
                "One 87 C GPU frame forced immediate firmware handoff."));
    }

    private static SyntheticEvidence ToEvidence(
        Hp8C40M8CSyntheticEvaluation evaluation,
        bool retained,
        FanAuthority authority) =>
        new(
            evaluation.Case,
            evaluation.Ordinal,
            evaluation.Timestamp,
            evaluation.Raw.ThermalEmergency,
            evaluation.Raw.CustomControlPermitted,
            evaluation.Effective.ThermalEmergency,
            evaluation.Effective.CustomControlPermitted,
            retained,
            authority.ToString(),
            evaluation.EvidenceKind);

    private static SafetyGateResult EvaluateEffectiveSafety(
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot,
        DateTimeOffset? previousTimestamp,
        Hp8C40ThermalEmergencyConfirmation confirmation,
        bool fanWritePathPresent)
    {
        var now = DateTimeOffset.UtcNow;
        var age = now - snapshot.Timestamp;

        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age > MaximumTelemetryAge)
        {
            throw new InvalidOperationException(
                $"M8C stale telemetry: {age.TotalSeconds:0.000}s.");
        }

        if (previousTimestamp.HasValue)
        {
            var gap =
                snapshot.Timestamp -
                previousTimestamp.Value;

            if (gap < TimeSpan.Zero ||
                gap > MaximumInterSampleGap)
            {
                throw new InvalidOperationException(
                    $"M8C invalid inter-sample gap: {gap.TotalSeconds:0.000}s.");
            }
        }

        if (!snapshot.IsComplete ||
            !snapshot.CpuCoreTelemetryComplete ||
            !snapshot.GpuTemperatureC.HasValue ||
            snapshot.GpuTemperatureC.Value <= 0)
        {
            throw new InvalidOperationException(
                "M8C requires complete package/core/power/load/GPU/tach telemetry.");
        }

        var raw =
            SafetyGate.Evaluate(
                hardware,
                SystemState.Healthy,
                snapshot,
                now,
                fanWritePathPresent);

        return confirmation.Apply(
            hardware,
            snapshot,
            raw);
    }

    private static bool IsRepresentativeLoad(
        TelemetrySnapshot snapshot) =>
        Hp8C40M8RepresentativeLoadQualificationTest
            .IsRepresentativeLoadValues(
                snapshot.GpuLoadPercent!.Value,
                snapshot.GpuPowerW!.Value,
                snapshot.CpuLoadPercent!.Value,
                snapshot.CpuPackagePowerW!.Value);

    private static void EnsureImmediatePhysicalLimits(
        TelemetrySnapshot snapshot,
        string context)
    {
        var cpu =
            snapshot.CpuControlTemperatureC;

        if (cpu.HasValue &&
            cpu.Value >=
            Hp8C40ThermalEmergencyConfirmation.CpuHardEmergencyC)
        {
            throw new InvalidOperationException(
                $"M8C {context} CPU hard abort: {cpu.Value:0.0} C.");
        }

        if (snapshot.GpuTemperatureC.HasValue &&
            snapshot.GpuTemperatureC.Value >=
            Hp8C40M8BWatchdogLoadQualificationTest.GpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"M8C {context} GPU physical abort: " +
                $"{snapshot.GpuTemperatureC.Value:0.0} C.");
        }
    }

    private static void EnsurePowerStatus(
        SystemPowerStatusSample status)
    {
        if (!status.AcOnline ||
            !status.BatteryPresent ||
            status.BatteryPercent > 100 ||
            status.BatteryPercent <
                MinimumBatteryPercent)
        {
            throw new InvalidOperationException(
                $"M8C AC/battery sanity gate refused: {status}");
        }
    }

    private static async Task<Hp8C40EcControlState>
        ReadExpectedEcConfirmedAsync(
            Hp8C40EcControlStateProbe probe,
            int expectedCpu,
            int expectedGpu,
            string context,
            CancellationToken cancellationToken)
    {
        const int maximumReads = 3;
        const int requiredConsecutiveUnexpected = 2;

        var previous =
            probe.ReadControlEvidence();

        if (MatchesExpected(
                previous,
                expectedCpu,
                expectedGpu))
        {
            return previous;
        }

        var consecutiveUnexpected = 1;

        for (var read = 2;
             read <= maximumReads;
             read++)
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(25),
                    cancellationToken)
                .ConfigureAwait(false);

            var next =
                probe.ReadControlEvidence();

            if (MatchesExpected(
                    next,
                    expectedCpu,
                    expectedGpu))
            {
                return next;
            }

            if (SameUnexpected(
                    previous,
                    next))
            {
                consecutiveUnexpected++;

                if (consecutiveUnexpected >=
                    requiredConsecutiveUnexpected)
                {
                    return next;
                }
            }
            else
            {
                consecutiveUnexpected = 1;
            }

            previous = next;
        }

        return previous;
    }

    private static bool MatchesExpected(
        Hp8C40EcControlState state,
        int expectedCpu,
        int expectedGpu) =>
        state.CpuSetpoint == expectedCpu &&
        state.GpuSetpoint == expectedGpu &&
        state.MaxFan == 0 &&
        state.FanSwitch == 0 &&
        state.CpuRpm <= 10_000 &&
        state.GpuRpm <= 10_000;

    private static bool SameUnexpected(
        Hp8C40EcControlState left,
        Hp8C40EcControlState right) =>
        left.CpuSetpoint ==
            right.CpuSetpoint &&
        left.GpuSetpoint ==
            right.GpuSetpoint &&
        left.MaxFan ==
            right.MaxFan &&
        left.FanSwitch ==
            right.FanSwitch;

    private static void EnsureExpectedEc(
        Hp8C40EcControlState state,
        int expectedCpu,
        int expectedGpu,
        string context)
    {
        if (!MatchesExpected(
                state,
                expectedCpu,
                expectedGpu))
        {
            throw new InvalidOperationException(
                $"M8C EC evidence refused during {context}: " +
                $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}; " +
                $"guards={state.MaxFan:X2}/{state.FanSwitch:X2}; " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}; expected=" +
                $"{expectedCpu}/{expectedGpu} guards=00/00.");
        }
    }

    private static async Task WaitForParentContinueAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var started =
            Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) <
               ParentContinueTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                var value =
                    (await File.ReadAllTextAsync(
                            path,
                            cancellationToken)
                        .ConfigureAwait(false))
                    .Trim();

                if (string.Equals(
                        value,
                        "M8C-CONTINUE",
                        StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "M8C parent continue marker had unexpected content.");
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "M8C parent did not authorize synthetic injection within 30 s.");
    }

    private static async Task WriteJsonAsync(
        string path,
        object value)
    {
        var fullPath =
            Path.GetFullPath(path);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "M8C evidence path has no parent directory."));

        var json =
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy =
                        JsonNamingPolicy.CamelCase
                });

        await File.WriteAllTextAsync(
                fullPath,
                json,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static (int ProcessId, long ProcessStartUtcTicks)
        GetCurrentProcessIdentity()
    {
        using var current =
            Process.GetCurrentProcess();

        return (
            current.Id,
            current.StartTime.ToUniversalTime().Ticks);
    }

    private static bool IsAdministrator()
    {
        using var identity =
            WindowsIdentity.GetCurrent();

        var principal =
            new WindowsPrincipal(
                identity);

        return principal.IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private sealed record PreWriteEvidence(
        int Index,
        DateTimeOffset TimestampUtc,
        bool Representative,
        int ConsecutiveRepresentative,
        double CpuEffectiveC,
        double CpuPowerW,
        double CpuLoadPercent,
        double GpuTemperatureC,
        double GpuPowerW,
        double GpuLoadPercent);

    private sealed record SyntheticEvidence(
        string Case,
        int Ordinal,
        DateTimeOffset TimestampUtc,
        bool RawThermalEmergency,
        bool RawCustomPermitted,
        bool EffectiveThermalEmergency,
        bool EffectiveCustomPermitted,
        bool CoordinatorRetainedCustom,
        string AuthorityAfter,
        string EvidenceKind);

    private sealed record EventEvidence(
        DateTimeOffset TimestampUtc,
        string Event,
        string Detail);
}
