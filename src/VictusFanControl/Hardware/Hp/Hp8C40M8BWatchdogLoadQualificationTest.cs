using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// M8B qualification-only watchdog-backed 50/50 load gate for the exact HP 8C40 target.
///
/// M8B requires a real representative gaming/3D workload before Custom admission,
/// performs exactly one 50/50 ApplyAsync call, then supervises ownership/feedback
/// under load without retransmitting the fan command. Production factory construction
/// and automatic/adaptive policy remain blocked.
/// </summary>
public static class Hp8C40M8BWatchdogLoadQualificationTest
{
    public const string RequiredToken = "8C40-M8B-LOAD50";
    public const int QualificationLevel = 50;

    public const int MaximumPreWriteSamples = 10;
    public const int RequiredConsecutivePreWriteRepresentativeSamples = 3;
    public const int SupervisionSamples = 30;
    public const int MinimumRepresentativeSupervisionSamples = 22;
    public const int MinimumConsecutiveRepresentativeSupervisionSamples = 10;
    public const int SampleIntervalMilliseconds = 1000;

    public const double CpuHardAbortC =
        Hp8C40ThermalEmergencyConfirmation.CpuHardEmergencyC;

    public const double GpuPhysicalAbortC = 82.0;

    private const byte MinimumBatteryPercent = 20;

    private static readonly TimeSpan MaximumTelemetryAge =
        TimeSpan.FromSeconds(3);

    private static readonly TimeSpan MaximumInterSampleGap =
        TimeSpan.FromSeconds(3);

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string readyPath,
        string resultPath,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var result = "FAIL_CLOSED";
        string? failureReason = null;
        var exitCode = 211;

        var controllerPid = Environment.ProcessId;
        long controllerStartUtcTicks;

        using (var current = Process.GetCurrentProcess())
        {
            controllerStartUtcTicks =
                current.StartTime.ToUniversalTime().Ticks;
        }

        var samples = new List<SampleEvidence>(
            SupervisionSamples);

        var preWriteSamples =
            new List<PreWriteEvidence>(
                MaximumPreWriteSamples);

        var representativeFlags =
            new List<bool>(
                SupervisionSamples);

        var events =
            new List<EventEvidence>();

        var applyCalls = 0;
        var customWasOwned = false;
        var normalRestoreCompleted = false;
        var finalFirmwareOwned = false;
        var readyPublished = false;

        FanControlCoordinator? coordinator = null;
        Hp8C40EcControlStateProbe? ecProbe = null;

        try
        {
            if (!IsAdministrator())
            {
                throw new InvalidOperationException(
                    "M8B requires an elevated Administrator process.");
            }

            var hardware =
                HardwareIdentityReader.ReadCurrent();

            if (!Hp8C40TargetProfile.Matches(
                    hardware,
                    out var targetReason))
            {
                throw new InvalidOperationException(
                    $"M8B exact-target refusal: {targetReason}");
            }

            var conflict =
                FindKnownConflictingControllerProcess();

            if (conflict is not null)
            {
                throw new InvalidOperationException(
                    $"M8B refused while '{conflict}' is running.");
            }

            EnsurePowerStatus(
                SystemPowerStatusReader.Read());

            ecProbe =
                new Hp8C40EcControlStateProbe(
                    modulesDirectory);

            var initial =
                await ReadExpectedEcEvidenceConfirmedAsync(
                        ecProbe,
                        expectedCpu: byte.MaxValue,
                        expectedGpu: byte.MaxValue,
                        context: "initial firmware-owned baseline",
                        cancellationToken)
                    .ConfigureAwait(false);

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "INITIAL_FIRMWARE_OWNED",
                    Format(initial.State)));

            EnsureExpectedEc(
                initial.State,
                byte.MaxValue,
                byte.MaxValue,
                "initial firmware-owned baseline");

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
                    "M8B exact-target telemetry backends are not fully initialized.");
            }

            var thermalConfirmation =
                new Hp8C40ThermalEmergencyConfirmation();

            // Prime differential CPU load/power counters. This sample is not
            // part of representative-load qualification, but hard physical
            // limits are still enforced.
            var warmup =
                telemetry.ReadSnapshot();

            EnsureImmediatePhysicalLimits(
                warmup,
                "warm-up");

            telemetry.ResetHealthWindow();

            await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        SampleIntervalMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset? previousPreWriteTimestamp = null;
            var preWriteConsecutiveRepresentative = 0;
            TelemetrySnapshot? lastPreWriteSnapshot = null;
            SafetyGateResult? lastPreWriteSafety = null;

            for (var sampleIndex = 1;
                 sampleIndex <= MaximumPreWriteSamples;
                 sampleIndex++)
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
                        previousPreWriteTimestamp,
                        thermalConfirmation,
                        fanWritePathPresent: true);

                previousPreWriteTimestamp =
                    snapshot.Timestamp;

                EnsureImmediatePhysicalLimits(
                    snapshot,
                    $"pre-write sample {sampleIndex}");

                if (!safety.PreconditionsReady ||
                    !safety.CustomControlPermitted ||
                    safety.ThermalEmergency)
                {
                    throw new InvalidOperationException(
                        "M8B pre-write safety refused Custom: " +
                        string.Join(" | ", safety.Reasons));
                }

                var representative =
                    IsRepresentativeLoad(
                        snapshot);

                preWriteConsecutiveRepresentative =
                    representative
                        ? preWriteConsecutiveRepresentative + 1
                        : 0;

                preWriteSamples.Add(
                    new PreWriteEvidence(
                        sampleIndex,
                        snapshot.Timestamp,
                        representative,
                        preWriteConsecutiveRepresentative,
                        snapshot.CpuControlTemperatureC!.Value,
                        snapshot.CpuPackagePowerW!.Value,
                        snapshot.CpuLoadPercent!.Value,
                        snapshot.GpuTemperatureC!.Value,
                        snapshot.GpuPowerW!.Value,
                        snapshot.GpuLoadPercent!.Value,
                        thermalConfirmation.CurrentCpuConsecutiveHighSamples));

                Console.WriteLine(
                    $"M8B_PREWRITE {sampleIndex}/{MaximumPreWriteSamples} " +
                    $"representative={representative} " +
                    $"streak={preWriteConsecutiveRepresentative}/{RequiredConsecutivePreWriteRepresentativeSamples} " +
                    $"CPU={snapshot.CpuControlTemperatureC:0.0}C " +
                    $"{snapshot.CpuPackagePowerW:0.0}W " +
                    $"{snapshot.CpuLoadPercent:0.0}% " +
                    $"CPU95={thermalConfirmation.CurrentCpuConsecutiveHighSamples}/" +
                    $"{Hp8C40ThermalEmergencyConfirmation.RequiredConsecutiveCpuSamples} " +
                    $"GPU={snapshot.GpuTemperatureC:0.0}C " +
                    $"{snapshot.GpuPowerW:0.0}W " +
                    $"{snapshot.GpuLoadPercent:0.0}%");

                lastPreWriteSnapshot = snapshot;
                lastPreWriteSafety = safety;

                if (preWriteConsecutiveRepresentative >=
                    RequiredConsecutivePreWriteRepresentativeSamples)
                {
                    break;
                }

                if (sampleIndex <
                    MaximumPreWriteSamples)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(
                                SampleIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (preWriteConsecutiveRepresentative <
                    RequiredConsecutivePreWriteRepresentativeSamples ||
                lastPreWriteSnapshot is null ||
                lastPreWriteSafety is null)
            {
                throw new InvalidOperationException(
                    "M8B representative load was not established before the write boundary.");
            }

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "REPRESENTATIVE_LOAD_ADMITTED",
                    $"{preWriteConsecutiveRepresentative} consecutive representative samples"));

            var preWriteEc =
                await ReadExpectedEcEvidenceConfirmedAsync(
                        ecProbe,
                        byte.MaxValue,
                        byte.MaxValue,
                        "immediately before Custom admission",
                        cancellationToken)
                    .ConfigureAwait(false);

            EnsureExpectedEc(
                preWriteEc.State,
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
                        "HP 8C40 M8B qualification-only watchdog-backed representative-load path.",
                    watchdogLease:
                        lease);

            coordinator =
                new FanControlCoordinator(
                    backend);

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "CUSTOM_ADMISSION_BEGIN",
                    "Requesting watchdog-backed Custom authority; no fan write yet."));

            var admitted =
                await coordinator.TryEnterCustomAsync(
                        lastPreWriteSafety,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!admitted ||
                coordinator.Authority !=
                    FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "M8B coordinator did not acquire Custom authority.");
            }

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "CUSTOM_ADMITTED_PREPARED",
                    "Watchdog PREPARE acknowledged; EC still firmware-owned."));

            var afterPrepare =
                await ReadExpectedEcEvidenceConfirmedAsync(
                        ecProbe,
                        byte.MaxValue,
                        byte.MaxValue,
                        "after PREPARE / before WRITE_INTENT",
                        cancellationToken)
                    .ConfigureAwait(false);

            EnsureExpectedEc(
                afterPrepare.State,
                byte.MaxValue,
                byte.MaxValue,
                "after PREPARE / before WRITE_INTENT");

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "APPLY_50_BEGIN",
                    "Exactly one ApplyAsync(50/50) begins."));

            applyCalls++;

            await coordinator.ApplyAsync(
                    new FanCommand(
                        QualificationLevel,
                        QualificationLevel,
                        "HP 8C40 M8B representative-load watchdog qualification"),
                    lastPreWriteSafety,
                    cancellationToken)
                .ConfigureAwait(false);

            customWasOwned = true;

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "APPLY_50_COMPLETE",
                    "WRITE_INTENT -> one real 50/50 command -> hardware ACK -> COMMIT completed."));

            var owned =
                await ReadExpectedEcEvidenceConfirmedAsync(
                        ecProbe,
                        QualificationLevel,
                        QualificationLevel,
                        "post-Commit ownership",
                        cancellationToken)
                    .ConfigureAwait(false);

            EnsureExpectedEc(
                owned.State,
                QualificationLevel,
                QualificationLevel,
                "post-Commit ownership");

            if (owned.State.CpuRpm == 0 ||
                owned.State.GpuRpm == 0)
            {
                throw new InvalidOperationException(
                    "M8B post-Commit dual tach evidence is zero.");
            }

            await WriteJsonAsync(
                    readyPath,
                    new
                    {
                        schemaVersion = 1,
                        gate = "M8B",
                        targetProfileId =
                            Hp8C40TargetProfile.Instance.Id,
                        processId = controllerPid,
                        processStartUtcTicks =
                            controllerStartUtcTicks,
                        timestampUtc =
                            DateTimeOffset.UtcNow,
                        authority = "Custom",
                        cpuSetpoint =
                            owned.State.CpuSetpoint,
                        gpuSetpoint =
                            owned.State.GpuSetpoint,
                        cpuRpm =
                            owned.State.CpuRpm,
                        gpuRpm =
                            owned.State.GpuRpm,
                        maxFan =
                            owned.State.MaxFan,
                        fanSwitch =
                            owned.State.FanSwitch,
                        ack =
                            "backend-ec+tachs+watchdog-owned",
                        applyCalls
                    })
                .ConfigureAwait(false);

            readyPublished = true;

            Console.WriteLine(
                $"M8B_READY controllerPid={controllerPid} " +
                $"startTicks={controllerStartUtcTicks} " +
                $"EC={owned.State.CpuSetpoint}/{owned.State.GpuSetpoint} " +
                $"RPM={owned.State.CpuRpm}/{owned.State.GpuRpm}");

            // PREPARE -> WRITE_INTENT -> WMI -> hardware ACK -> COMMIT ->
            // READY is an intentional synchronous control transaction, not a
            // telemetry/lifecycle sampling interval. Do not compare the first
            // post-Commit supervision timestamp against the last pre-write
            // sample. The first supervision frame must still be individually
            // fresh/complete and every later supervision gap remains bounded.
            DateTimeOffset? previousSupervisionTimestamp = null;

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "SUPERVISION_TELEMETRY_EPOCH_BEGIN",
                    "Post-Commit supervision starts a new telemetry continuity epoch; freshness remains enforced and only the intentional control transaction gap is excluded."));

            for (var sampleIndex = 1;
                 sampleIndex <= SupervisionSamples;
                 sampleIndex++)
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
                        previousSupervisionTimestamp,
                        thermalConfirmation,
                        fanWritePathPresent: true);

                previousSupervisionTimestamp =
                    snapshot.Timestamp;

                // Qualification boundary: GPU >=82 C is intentionally more
                // conservative than the production 87 C raw GPU threshold.
                if (snapshot.GpuTemperatureC >=
                    GpuPhysicalAbortC)
                {
                    await coordinator.RestoreFirmwareAsync(
                            "M8B GPU physical abort",
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    customWasOwned = false;

                    throw new InvalidOperationException(
                        $"M8B physical GPU abort: " +
                        $"{snapshot.GpuTemperatureC:0.0} C >= " +
                        $"{GpuPhysicalAbortC:0} C.");
                }

                var retained =
                    await coordinator.EnforceSafetyAsync(
                            safety,
                            $"M8B representative-load supervision {sampleIndex}/{SupervisionSamples}",
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!retained ||
                    coordinator.Authority !=
                        FanAuthority.Custom)
                {
                    customWasOwned = false;

                    throw new InvalidOperationException(
                        "M8B safety/ownership supervision returned authority to firmware: " +
                        string.Join(" | ", safety.Reasons));
                }

                var ec =
                    await ReadExpectedEcEvidenceConfirmedAsync(
                            ecProbe,
                            QualificationLevel,
                            QualificationLevel,
                            $"supervision sample {sampleIndex}",
                            cancellationToken)
                        .ConfigureAwait(false);

                EnsureExpectedEc(
                    ec.State,
                    QualificationLevel,
                    QualificationLevel,
                    $"supervision sample {sampleIndex}");

                var representative =
                    IsRepresentativeLoad(
                        snapshot);

                representativeFlags.Add(
                    representative);

                var evidence =
                    new SampleEvidence(
                        sampleIndex,
                        snapshot.Timestamp,
                        representative,
                        snapshot.CpuControlTemperatureC!.Value,
                        snapshot.CpuTemperatureC!.Value,
                        snapshot.CpuCoreMaxTemperatureC!.Value,
                        snapshot.CpuPackagePowerW!.Value,
                        snapshot.CpuLoadPercent!.Value,
                        snapshot.GpuTemperatureC!.Value,
                        snapshot.GpuPowerW!.Value,
                        snapshot.GpuLoadPercent!.Value,
                        snapshot.CpuFanRpm!.Value,
                        snapshot.GpuFanRpm!.Value,
                        thermalConfirmation.CurrentCpuConsecutiveHighSamples,
                        safety.ThermalEmergency,
                        ec.RecoveredTransient,
                        ec.Reads);

                samples.Add(
                    evidence);

                Console.WriteLine(
                    $"M8B_SAMPLE {sampleIndex}/{SupervisionSamples} " +
                    $"representative={representative} " +
                    $"CPU={evidence.CpuEffectiveC:0.0}C " +
                    $"{evidence.CpuPowerW:0.0}W " +
                    $"{evidence.CpuLoadPercent:0.0}% " +
                    $"CPU95={evidence.CpuHighStreak}/" +
                    $"{Hp8C40ThermalEmergencyConfirmation.RequiredConsecutiveCpuSamples} " +
                    $"GPU={evidence.GpuTemperatureC:0.0}C " +
                    $"{evidence.GpuPowerW:0.0}W " +
                    $"{evidence.GpuLoadPercent:0.0}% " +
                    $"EC={ec.State.CpuSetpoint}/{ec.State.GpuSetpoint} " +
                    $"RPM={ec.State.CpuRpm}/{ec.State.GpuRpm}");

                if (sampleIndex <
                    SupervisionSamples)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(
                                SampleIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var window =
                EvaluateRepresentativeWindow(
                    representativeFlags);

            Console.WriteLine(
                $"M8B_WINDOW representative=" +
                $"{window.RepresentativeSamples}/{SupervisionSamples} " +
                $"maxConsecutive={window.MaximumConsecutiveRepresentative} " +
                $"required={MinimumRepresentativeSupervisionSamples}/{SupervisionSamples} " +
                $"and >= {MinimumConsecutiveRepresentativeSupervisionSamples} consecutive.");

            if (!window.Passed)
            {
                throw new InvalidOperationException(
                    "M8B representative-load supervision window was not established: " +
                    $"{window.RepresentativeSamples}/{SupervisionSamples}, " +
                    $"maximum consecutive {window.MaximumConsecutiveRepresentative}.");
            }

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "NORMAL_RESTORE_BEGIN",
                    "M8B supervision complete; returning authority to firmware."));

            await coordinator.RestoreFirmwareAsync(
                    "M8B normal watchdog-backed load release",
                    CancellationToken.None)
                .ConfigureAwait(false);

            customWasOwned = false;

            var final =
                await WaitForFirmwareOwnedAsync(
                        ecProbe,
                        TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

            finalFirmwareOwned =
                final.CpuSetpoint == byte.MaxValue &&
                final.GpuSetpoint == byte.MaxValue;

            var restoreEvidence =
                coordinator.LastRestoreEvidence;

            if (restoreEvidence is null)
            {
                throw new InvalidOperationException(
                    "M8B coordinator/backend did not expose restore evidence.");
            }

            var verified =
                restoreEvidence.Value;

            if (coordinator.Authority !=
                    FanAuthority.Firmware ||
                !verified.LocalFirmwareAckVerified ||
                !verified.WatchdogLeaseRequired ||
                !verified.WatchdogReleaseVerified ||
                !finalFirmwareOwned)
            {
                throw new InvalidOperationException(
                    "M8B normal restore did not prove local FF/FF plus watchdog Release.");
            }

            normalRestoreCompleted = true;

            events.Add(
                new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "NORMAL_RESTORE_COMPLETE",
                    "Local FF/FF verified and watchdog Release acknowledged."));

            result = "PASS";
            exitCode = 0;

            Console.WriteLine(
                "PASS: HP 8C40 M8B watchdog-backed 50/50 representative-load gate completed.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            failureReason =
                "M8B cancelled by operator.";

            exitCode = 130;

            Console.Error.WriteLine(
                failureReason);
        }
        catch (Exception ex)
        {
            failureReason =
                $"{ex.GetType().Name}: {ex.Message}";

            exitCode = 211;

            Console.Error.WriteLine(
                $"M8B FAIL_CLOSED: {failureReason}");
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
                                "M8B finally fallback",
                                CancellationToken.None)
                            .ConfigureAwait(false);

                        customWasOwned = false;
                    }
                    catch (Exception restoreFailure)
                    {
                        Console.Error.WriteLine(
                            $"M8B finally restore failed: " +
                            $"{restoreFailure.GetType().Name}: " +
                            $"{restoreFailure.Message}");
                    }
                }

                await coordinator.DisposeAsync()
                    .ConfigureAwait(false);
            }

            if (ecProbe is not null)
            {
                try
                {
                    var finalProbe =
                        await ReadExpectedEcEvidenceConfirmedAsync(
                                ecProbe,
                                byte.MaxValue,
                                byte.MaxValue,
                                "final controller-side firmware-owned probe",
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    finalFirmwareOwned =
                        finalProbe.State.CpuSetpoint ==
                            byte.MaxValue &&
                        finalProbe.State.GpuSetpoint ==
                            byte.MaxValue;
                }
                catch
                {
                    // Parent harness owns the independent final closure proof.
                }
            }

            var window =
                EvaluateRepresentativeWindow(
                    representativeFlags);

            var evidenceObject =
                new
                {
                    schemaVersion = 1,
                    gate = "M8B",
                    result,
                    failureReason,
                    startedUtc,
                    endedUtc = DateTimeOffset.UtcNow,
                    targetProfileId =
                        Hp8C40TargetProfile.Instance.Id,
                    controller =
                        new
                        {
                            processId = controllerPid,
                            processStartUtcTicks =
                                controllerStartUtcTicks
                        },
                    writeCapable = true,
                    qualificationLevel =
                        $"{QualificationLevel}/{QualificationLevel}",
                    applyCalls,
                    readyPublished,
                    customWasOwnedAtFinally =
                        customWasOwned,
                    normalRestoreCompleted,
                    finalFirmwareOwned,
                    criteria =
                        new
                        {
                            maximumPreWriteSamples =
                                MaximumPreWriteSamples,
                            requiredConsecutivePreWriteRepresentativeSamples =
                                RequiredConsecutivePreWriteRepresentativeSamples,
                            supervisionSamples =
                                SupervisionSamples,
                            minimumRepresentativeSupervisionSamples =
                                MinimumRepresentativeSupervisionSamples,
                            minimumConsecutiveRepresentativeSupervisionSamples =
                                MinimumConsecutiveRepresentativeSupervisionSamples,
                            sampleIntervalMilliseconds =
                                SampleIntervalMilliseconds,
                            gpu =
                                "load >=35% AND power >=20 W",
                            cpu =
                                "load >=5% OR package power >=15 W",
                            cpuThermalConfirmation =
                                "5 unique consecutive fresh samples >=95 C",
                            cpuHardAbortC =
                                CpuHardAbortC,
                            gpuPhysicalAbortC =
                                GpuPhysicalAbortC
                        },
                    window =
                        new
                        {
                            representativeSamples =
                                window.RepresentativeSamples,
                            maximumConsecutiveRepresentative =
                                window.MaximumConsecutiveRepresentative,
                            passed =
                                window.Passed
                        },
                    thermal =
                        new
                        {
                            cpuMaximumEffectiveC =
                                samples.Count == 0
                                    ? (double?)null
                                    : samples.Max(
                                        sample =>
                                            sample.CpuEffectiveC),
                            cpuMaximumHighStreak =
                                samples.Count == 0
                                    ? 0
                                    : samples.Max(
                                        sample =>
                                            sample.CpuHighStreak),
                            gpuMaximumC =
                                samples.Count == 0
                                    ? (double?)null
                                    : samples.Max(
                                        sample =>
                                            sample.GpuTemperatureC)
                        },
                    preWriteSamples,
                    samples,
                    events
                };

            try
            {
                await WriteJsonAsync(
                        resultPath,
                        evidenceObject)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"M8B evidence: {Path.GetFullPath(resultPath)}");
            }
            catch (Exception evidenceFailure)
            {
                Console.Error.WriteLine(
                    $"M8B evidence write failed: " +
                    $"{evidenceFailure.GetType().Name}: " +
                    $"{evidenceFailure.Message}");

                if (exitCode == 0)
                {
                    exitCode = 212;
                }
            }
        }

        return exitCode;
    }

    internal static WindowEvaluation
        EvaluateRepresentativeWindow(
            IReadOnlyList<bool> representativeSamples)
    {
        var representativeCount = 0;
        var currentConsecutive = 0;
        var maximumConsecutive = 0;

        foreach (var representative in
                 representativeSamples)
        {
            if (representative)
            {
                representativeCount++;
                currentConsecutive++;
                maximumConsecutive =
                    Math.Max(
                        maximumConsecutive,
                        currentConsecutive);
            }
            else
            {
                currentConsecutive = 0;
            }
        }

        var passed =
            representativeSamples.Count ==
                SupervisionSamples &&
            representativeCount >=
                MinimumRepresentativeSupervisionSamples &&
            maximumConsecutive >=
                MinimumConsecutiveRepresentativeSupervisionSamples;

        return new WindowEvaluation(
            representativeCount,
            maximumConsecutive,
            passed);
    }

    private static bool IsRepresentativeLoad(
        TelemetrySnapshot snapshot) =>
        Hp8C40M8RepresentativeLoadQualificationTest
            .IsRepresentativeLoadValues(
                snapshot.GpuLoadPercent!.Value,
                snapshot.GpuPowerW!.Value,
                snapshot.CpuLoadPercent!.Value,
                snapshot.CpuPackagePowerW!.Value);

    private static SafetyGateResult
        EvaluateEffectiveSafety(
            HardwareIdentity hardware,
            TelemetrySnapshot snapshot,
            DateTimeOffset? previousTimestamp,
            Hp8C40ThermalEmergencyConfirmation
                thermalConfirmation,
            bool fanWritePathPresent)
    {
        var now = DateTimeOffset.UtcNow;

        var age =
            now - snapshot.Timestamp;

        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age >
            MaximumTelemetryAge)
        {
            throw new InvalidOperationException(
                $"M8B stale telemetry: " +
                $"{age.TotalSeconds:0.000}s.");
        }

        if (previousTimestamp.HasValue)
        {
            var gap =
                snapshot.Timestamp -
                previousTimestamp.Value;

            if (gap < TimeSpan.Zero ||
                gap >
                    MaximumInterSampleGap)
            {
                throw new InvalidOperationException(
                    $"M8B invalid inter-sample gap: " +
                    $"{gap.TotalSeconds:0.000}s.");
            }
        }

        if (!snapshot.IsComplete ||
            !snapshot.CpuCoreTelemetryComplete ||
            !snapshot.GpuTemperatureC.HasValue ||
            snapshot.GpuTemperatureC.Value <= 0)
        {
            throw new InvalidOperationException(
                "M8B requires complete package/core/power/load/GPU/tach telemetry.");
        }

        var raw =
            SafetyGate.Evaluate(
                hardware,
                SystemState.Healthy,
                snapshot,
                now,
                fanWritePathPresent);

        return thermalConfirmation.Apply(
            hardware,
            snapshot,
            raw);
    }

    private static void EnsureImmediatePhysicalLimits(
        TelemetrySnapshot snapshot,
        string context)
    {
        var cpu =
            snapshot.CpuControlTemperatureC;

        if (cpu.HasValue &&
            cpu.Value >=
                CpuHardAbortC)
        {
            throw new InvalidOperationException(
                $"M8B {context} CPU hard abort: " +
                $"{cpu.Value:0.0} C >= " +
                $"{CpuHardAbortC:0} C.");
        }

        if (snapshot.GpuTemperatureC.HasValue &&
            snapshot.GpuTemperatureC.Value >=
                GpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"M8B {context} GPU physical abort: " +
                $"{snapshot.GpuTemperatureC.Value:0.0} C >= " +
                $"{GpuPhysicalAbortC:0} C.");
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
                $"M8B AC/battery sanity gate refused: {status}");
        }
    }

    private static async Task<ConfirmedEcEvidence>
        ReadExpectedEcEvidenceConfirmedAsync(
            Hp8C40EcControlStateProbe probe,
            int expectedCpu,
            int expectedGpu,
            string context,
            CancellationToken cancellationToken)
    {
        const int maxReads = 3;
        const int requiredConsecutiveUnexpected = 2;
        const int delayMs = 25;

        var first =
            probe.ReadControlEvidence();

        if (MatchesExpected(
                first,
                expectedCpu,
                expectedGpu))
        {
            return new ConfirmedEcEvidence(
                first,
                false,
                1);
        }

        var previous =
            first;

        var consecutiveUnexpected = 1;

        for (var read = 2;
             read <= maxReads;
             read++)
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        delayMs),
                    cancellationToken)
                .ConfigureAwait(false);

            var next =
                probe.ReadControlEvidence();

            if (MatchesExpected(
                    next,
                    expectedCpu,
                    expectedGpu))
            {
                Console.WriteLine(
                    $"M8B EC transient during {context} " +
                    $"recovered on read {read}/{maxReads}.");

                return new ConfirmedEcEvidence(
                    next,
                    true,
                    read);
            }

            if (SameUnexpected(
                    previous,
                    next))
            {
                consecutiveUnexpected++;

                if (consecutiveUnexpected >=
                    requiredConsecutiveUnexpected)
                {
                    return new ConfirmedEcEvidence(
                        next,
                        false,
                        read);
                }
            }
            else
            {
                consecutiveUnexpected = 1;
            }

            previous = next;
        }

        return new ConfirmedEcEvidence(
            previous,
            false,
            maxReads);
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
                $"M8B EC evidence refused during {context}: " +
                $"{Format(state)}; expected=" +
                $"{expectedCpu}/{expectedGpu} guards=00/00.");
        }
    }

    private static async Task<Hp8C40EcControlState>
        WaitForFirmwareOwnedAsync(
            Hp8C40EcControlStateProbe probe,
            TimeSpan timeout)
    {
        var started =
            Stopwatch.GetTimestamp();

        Hp8C40EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) <
               timeout)
        {
            last =
                probe.ReadControlEvidence();

            if (last.CpuSetpoint ==
                    byte.MaxValue &&
                last.GpuSetpoint ==
                    byte.MaxValue)
            {
                return last;
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"M8B did not independently observe FF/FF; " +
            $"last={Format(last)}");
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
                "M8B evidence path has no parent directory."));

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

    private static string?
        FindKnownConflictingControllerProcess()
    {
        foreach (var name in new[]
                 {
                     "OmenMon",
                     "OmenMon-Reborn",
                     "VictusFanControl.App"
                 })
        {
            Process[] processes;

            try
            {
                processes =
                    Process.GetProcessesByName(
                        name);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return name;
                }
            }
            finally
            {
                foreach (var process in
                         processes)
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }

    private static string Format(
        Hp8C40EcControlState? state) =>
        state is null
            ? "n/a"
            : $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}; " +
              $"MaxFan=0x{state.MaxFan:X2}; " +
              $"FanSwitch=0x{state.FanSwitch:X2}; " +
              $"RPM={state.CpuRpm}/{state.GpuRpm}";

    internal sealed record WindowEvaluation(
        int RepresentativeSamples,
        int MaximumConsecutiveRepresentative,
        bool Passed);

    private sealed record ConfirmedEcEvidence(
        Hp8C40EcControlState State,
        bool RecoveredTransient,
        int Reads);

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
        double GpuLoadPercent,
        int CpuHighStreak);

    private sealed record SampleEvidence(
        int Index,
        DateTimeOffset TimestampUtc,
        bool Representative,
        double CpuEffectiveC,
        double CpuPackageC,
        double CpuCoreMaxC,
        double CpuPowerW,
        double CpuLoadPercent,
        double GpuTemperatureC,
        double GpuPowerW,
        double GpuLoadPercent,
        double CpuFanRpm,
        double GpuFanRpm,
        int CpuHighStreak,
        bool ThermalEmergency,
        bool EcRecoveredTransient,
        int EcReads);

    private sealed record EventEvidence(
        DateTimeOffset TimestampUtc,
        string Event,
        string Detail);
}
