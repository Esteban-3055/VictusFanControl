using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// P15B qualification-only controller for one production Manual 30/30
/// transaction followed by the complete watchdog-backed firmware restore.
/// The user-facing Manual and Automatic gates remain closed.
/// </summary>
public static class Hp8C40P15BManual30QualificationTest
{
    // HARD VERSIONED BARRIER. A later, separate authorization commit may set
    // this true only after the P15B preparation contract has passed full CI.
    public static readonly bool PhysicalExecutionAuthorized = true;

    public const string RequiredToken = "8C40-P15B-MANUAL30";
    public const int QualificationLevel = 30;
    public const int RequiredConsecutivePreWriteSamples = 3;
    public const int MaximumPreWriteSamples = 10;
    public const int SupervisionSamples = 3;
    public const int SampleIntervalMilliseconds = 1000;
    public const double CpuPhysicalAbortC = 90.0;
    public const double GpuPhysicalAbortC = 82.0;
    public const double MaximumCpuPackagePowerW = 60.0;
    public const double MaximumGpuPowerW = 75.0;

    private const byte MinimumBatteryPercent = 20;
    private static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumInterSampleGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ParentContinueTimeout = TimeSpan.FromSeconds(20);

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string readyPath,
        string continuePath,
        string resultPath,
        CancellationToken cancellationToken)
    {
        // Keep before Administrator, SMBIOS, PawnIO, watchdog IPC, WMI or EC.
        if (!PhysicalExecutionAuthorized)
        {
            Console.Error.WriteLine(
                "P15B PHYSICAL REFUSED: qualification controller authorization is closed.");
            return 240;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var result = "FAIL_CLOSED";
        string? failureReason = null;
        var exitCode = 241;
        var applyManualCalls = 0;
        var readyPublished = false;
        var continueObserved = false;
        var manualModeSelected = false;
        var productionRouteConstructed = false;
        var productionRestoreCompleted = false;
        var finalFirmwareOwned = false;
        var cleanupRestoreAttempted = false;
        var cleanupRestoreSucceeded = false;
        var preWriteSamples = new List<SampleEvidence>();
        var supervisionSamples = new List<SampleEvidence>();
        var finalFirmwareSamples = new List<EcEvidence>();
        var events = new List<EventEvidence>();
        var controllerIdentity = GetCurrentProcessIdentity();
        FanFirmwareRestoreEvidence? restoreEvidence = null;

        FanControlCoordinator? coordinator = null;

        try
        {
            if (!IsAdministrator())
            {
                throw new InvalidOperationException(
                    "P15B requires an elevated Administrator process.");
            }

            var hardware = HardwareIdentityReader.ReadCurrent();
            if (!Hp8C40TargetProfile.Matches(hardware, out var targetReason))
            {
                throw new InvalidOperationException(
                    $"P15B exact-target refusal: {targetReason}");
            }

            var conflict = FindKnownConflictingControllerProcess();
            if (conflict is not null)
            {
                throw new InvalidOperationException(
                    $"P15B refused while '{conflict}' is running.");
            }

            EnsurePowerStatus(SystemPowerStatusReader.Read());

            var ecProbe = new Hp8C40EcControlStateProbe(modulesDirectory);
            var initial = await ReadExpectedEcConfirmedAsync(
                    ecProbe,
                    byte.MaxValue,
                    byte.MaxValue,
                    "initial firmware-owned baseline",
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(
                initial,
                byte.MaxValue,
                byte.MaxValue,
                "initial firmware-owned baseline");

            using var telemetry = new HardwareTelemetryReader(modulesDirectory);
            if (!telemetry.BackendsInitialized ||
                !string.Equals(
                    telemetry.TargetProfile?.Id,
                    Hp8C40TargetProfile.Instance.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "P15B exact-target telemetry backends are not fully initialized.");
            }

            var thermalConfirmation = new Hp8C40ThermalEmergencyConfirmation();
            _ = telemetry.ReadSnapshot();
            telemetry.ResetHealthWindow();
            await Task.Delay(
                    TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);

            SafetyGateResult? admittedSafety = null;
            DateTimeOffset? previousTimestamp = null;
            var healthyStreak = 0;

            for (var index = 1; index <= MaximumPreWriteSamples; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePowerStatus(SystemPowerStatusReader.Read());

                var snapshot = telemetry.ReadSnapshot();
                EnsureImmediatePhysicalLimits(snapshot, $"pre-write sample {index}");

                SafetyGateResult safety;
                try
                {
                    safety = EvaluateEffectiveSafety(
                        hardware,
                        snapshot,
                        previousTimestamp,
                        thermalConfirmation,
                        fanWritePathPresent: true);
                }
                catch (InvalidOperationException ex)
                {
                    healthyStreak = 0;
                    Console.WriteLine(
                        $"P15B_PREWRITE {index}/{MaximumPreWriteSamples} waiting: {ex.Message}");
                    previousTimestamp = snapshot.Timestamp;

                    if (index < MaximumPreWriteSamples)
                    {
                        await Task.Delay(
                                TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw;
                }

                previousTimestamp = snapshot.Timestamp;
                var boundedLoad = IsBoundedLoad(snapshot);
                var accepted =
                    safety.PreconditionsReady &&
                    safety.CustomControlPermitted &&
                    !safety.ThermalEmergency &&
                    boundedLoad;

                healthyStreak = accepted ? healthyStreak + 1 : 0;
                preWriteSamples.Add(
                    ToEvidence(index, snapshot, safety, boundedLoad, healthyStreak));

                Console.WriteLine(
                    $"P15B_PREWRITE {index}/{MaximumPreWriteSamples} accepted={accepted} " +
                    $"streak={healthyStreak}/{RequiredConsecutivePreWriteSamples} " +
                    $"CPU={snapshot.CpuControlTemperatureC:0.0}C {snapshot.CpuPackagePowerW:0.0}W " +
                    $"GPU={snapshot.GpuTemperatureC:0.0}C {snapshot.GpuPowerW:0.0}W");

                if (healthyStreak >= RequiredConsecutivePreWriteSamples)
                {
                    admittedSafety = safety;
                    break;
                }

                if (index < MaximumPreWriteSamples)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (admittedSafety is null)
            {
                throw new InvalidOperationException(
                    "P15B could not establish three consecutive fresh, complete, SafetyGate-permitted bounded-load snapshots.");
            }

            var preConstruction = await ReadExpectedEcConfirmedAsync(
                    ecProbe,
                    byte.MaxValue,
                    byte.MaxValue,
                    "before production construction",
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(
                preConstruction,
                byte.MaxValue,
                byte.MaxValue,
                "before production construction");

            IFanControlWatchdogLeaseClient? lease =
                Hp8C40ProductionWatchdogGate.CreateLeaseIfAuthorized(hardware);

            if (lease is null)
            {
                throw new InvalidOperationException(
                    "P15B production watchdog gate did not return the promoted M4 lease client.");
            }

            HpFanBackendSelection selection;
            try
            {
                selection = HpFanControlBackendFactory.Create(
                    modulesDirectory,
                    hardware,
                    lease);

                // Successful factory construction transfers lease ownership to
                // the selected backend.
                lease = null;
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (!string.Equals(
                    selection.TargetProfile?.Id,
                    Hp8C40TargetProfile.Instance.Id,
                    StringComparison.Ordinal) ||
                selection.Backend is not Hp8C40FanControlBackend)
            {
                await selection.Backend.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    "P15B factory did not select the exact production HP 8C40 watchdog-backed backend.");
            }

            coordinator = new FanControlCoordinator(selection.Backend);
            productionRouteConstructed = true;
            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "PRODUCTION_PATH_CONSTRUCTED",
                "Hp8C40ProductionWatchdogGate.CreateLeaseIfAuthorized -> HpFanControlBackendFactory.Create -> Hp8C40FanControlBackend."));

            var manual = new AdaptiveFanProductionController(
                coordinator,
                BuildManualAdapterConfig(),
                manualExecutionAuthorized: true,
                automaticExecutionAuthorized: false);

            var mode = await manual.SetModeAsync(
                    AdaptiveFanProductionMode.Manual,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!mode.ExecutionAuthorized ||
                mode.Mode != AdaptiveFanProductionMode.Manual ||
                mode.Authority != FanAuthority.Firmware)
            {
                throw new InvalidOperationException(
                    "P15B could not select qualification-only Manual mode while retaining Firmware authority.");
            }

            manualModeSelected = true;
            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "MANUAL_MODE_SELECTED",
                "Qualification-only AdaptiveFanProductionController Manual mode selected; no fan command issued yet."));

            var beforeApply = await ReadExpectedEcConfirmedAsync(
                    ecProbe,
                    byte.MaxValue,
                    byte.MaxValue,
                    "after Manual mode selection / before ApplyManualAsync",
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(
                beforeApply,
                byte.MaxValue,
                byte.MaxValue,
                "after Manual mode selection / before ApplyManualAsync");

            applyManualCalls++;
            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "APPLY_MANUAL_30_BEGIN",
                "Exactly one AdaptiveFanProductionController.ApplyManualAsync(30) begins."));

            var applied = await manual.ApplyManualAsync(
                    QualificationLevel,
                    admittedSafety,
                    cancellationToken)
                .ConfigureAwait(false);

            if (applyManualCalls != 1 ||
                !applied.ExecutionAuthorized ||
                applied.Mode != AdaptiveFanProductionMode.Manual ||
                applied.Action != AdaptiveFanProductionActionKind.EnterCustomAndApply ||
                applied.EqualFanLevel != QualificationLevel ||
                applied.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "P15B Manual adapter did not complete exactly one first 30/30 Custom application.");
            }

            var owned = await ReadExpectedEcConfirmedAsync(
                    ecProbe,
                    QualificationLevel,
                    QualificationLevel,
                    "post-Manual ownership",
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(
                owned,
                QualificationLevel,
                QualificationLevel,
                "post-Manual ownership");

            if (owned.CpuRpm == 0 || owned.GpuRpm == 0)
            {
                throw new InvalidOperationException(
                    "P15B post-Manual dual-tach evidence is zero.");
            }

            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "APPLY_MANUAL_30_COMPLETE",
                "Production Manual adapter completed PREPARE -> WRITE_INTENT -> one real 30/30 -> EC+dual-tach ACK -> COMMIT."));

            await WriteJsonAsync(
                    readyPath,
                    new
                    {
                        schemaVersion = 1,
                        gate = "P15B",
                        targetProfileId = Hp8C40TargetProfile.Instance.Id,
                        processId = controllerIdentity.ProcessId,
                        processStartUtcTicks = controllerIdentity.ProcessStartUtcTicks,
                        timestampUtc = DateTimeOffset.UtcNow,
                        mode = manual.Mode.ToString(),
                        authority = coordinator.Authority.ToString(),
                        cpuSetpoint = owned.CpuSetpoint,
                        gpuSetpoint = owned.GpuSetpoint,
                        cpuRpm = owned.CpuRpm,
                        gpuRpm = owned.GpuRpm,
                        applyManualCalls,
                        manualExecutionAuthorized = manual.ManualExecutionAuthorized,
                        automaticExecutionAuthorized = manual.AutomaticExecutionAuthorized,
                        constructionRoute =
                            "Hp8C40ProductionWatchdogGate.CreateLeaseIfAuthorized -> HpFanControlBackendFactory.Create -> AdaptiveFanProductionController"
                    })
                .ConfigureAwait(false);

            readyPublished = true;
            await WaitForParentContinueAsync(
                    continuePath,
                    cancellationToken)
                .ConfigureAwait(false);
            continueObserved = true;

            previousTimestamp = null;
            for (var index = 1; index <= SupervisionSamples; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePowerStatus(SystemPowerStatusReader.Read());

                var snapshot = telemetry.ReadSnapshot();
                EnsureImmediatePhysicalLimits(snapshot, $"supervision sample {index}");

                var safety = EvaluateEffectiveSafety(
                    hardware,
                    snapshot,
                    previousTimestamp,
                    thermalConfirmation,
                    fanWritePathPresent: true);
                previousTimestamp = snapshot.Timestamp;

                if (!IsBoundedLoad(snapshot))
                {
                    throw new InvalidOperationException(
                        "P15B supervision left the bounded-load envelope; refusing to hold 30/30.");
                }

                var retained = await coordinator.EnforceSafetyAsync(
                        safety,
                        $"P15B Manual 30/30 supervision {index}/{SupervisionSamples}",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!retained || coordinator.Authority != FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        "P15B supervision returned authority to firmware before the planned strong restore.");
                }

                var ec = await ReadExpectedEcConfirmedAsync(
                        ecProbe,
                        QualificationLevel,
                        QualificationLevel,
                        $"supervision sample {index}",
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureExpectedEc(
                    ec,
                    QualificationLevel,
                    QualificationLevel,
                    $"supervision sample {index}");

                supervisionSamples.Add(
                    ToEvidence(index, snapshot, safety, true, index));

                if (index < SupervisionSamples)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "STRONG_RESTORE_BEGIN",
                "Manual one-shot supervision complete; ReleaseToFirmwareAsync begins."));

            var release = await manual.ReleaseToFirmwareAsync(
                    "P15B one-shot Manual 30/30 complete; strong restore to HP firmware",
                    CancellationToken.None)
                .ConfigureAwait(false);

            restoreEvidence = coordinator.LastRestoreEvidence;

            if (release.Action != AdaptiveFanProductionActionKind.RestoreFirmware ||
                release.Mode != AdaptiveFanProductionMode.Firmware ||
                release.Authority != FanAuthority.Firmware ||
                !restoreEvidence.HasValue ||
                !restoreEvidence.Value.LocalFirmwareAckVerified ||
                !restoreEvidence.Value.WatchdogLeaseRequired ||
                !restoreEvidence.Value.WatchdogReleaseVerified)
            {
                throw new InvalidOperationException(
                    "P15B production strong restore did not prove local FF/FF acknowledgement plus watchdog RELEASE.");
            }

            productionRestoreCompleted = true;

            finalFirmwareSamples = await ReadStableFirmwareOwnedAsync(
                    ecProbe,
                    cancellationToken)
                .ConfigureAwait(false);
            finalFirmwareOwned = true;

            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "STRONG_RESTORE_COMPLETE",
                "Production restore path returned Firmware authority with local FF/FF acknowledgement, watchdog RELEASE and two independent final FF/FF samples."));

            result = "PASS_CONTROLLER_STRONG_RESTORE";
            exitCode = 0;
        }
        catch (Exception ex)
        {
            failureReason = ex.ToString();
            Console.Error.WriteLine($"P15B FAIL_CLOSED: {ex.Message}");
        }
        finally
        {
            if (coordinator is not null)
            {
                if (coordinator.Authority != FanAuthority.Firmware)
                {
                    cleanupRestoreAttempted = true;
                    try
                    {
                        await coordinator.RestoreFirmwareAsync(
                                "P15B controller fail-closed cleanup",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        cleanupRestoreSucceeded =
                            coordinator.Authority == FanAuthority.Firmware;
                    }
                    catch (Exception cleanupFailure)
                    {
                        failureReason =
                            (failureReason ?? "P15B failure") +
                            Environment.NewLine +
                            "Cleanup restore failure: " +
                            cleanupFailure;
                    }
                }

                try
                {
                    await coordinator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeFailure)
                {
                    failureReason =
                        (failureReason ?? "P15B failure") +
                        Environment.NewLine +
                        "Coordinator dispose failure: " +
                        disposeFailure;

                    if (exitCode == 0)
                    {
                        exitCode = 242;
                        result = "FAIL_CLOSED";
                    }
                }
            }

            var evidence = new
            {
                schemaVersion = 1,
                gate = "P15B",
                result,
                failureReason,
                startedUtc,
                completedUtc = DateTimeOffset.UtcNow,
                targetProfileId = Hp8C40TargetProfile.Instance.Id,
                processId = controllerIdentity.ProcessId,
                processStartUtcTicks = controllerIdentity.ProcessStartUtcTicks,
                physicalExecutionAuthorized = PhysicalExecutionAuthorized,
                qualificationLevel = QualificationLevel,
                applyManualCalls,
                readyPublished,
                continueObserved,
                manualModeSelected,
                manualExecutionAuthorized = true,
                automaticExecutionAuthorized = false,
                productionRouteConstructed,
                productionConstructionAuthorized =
                    Hp8C40ProductionWatchdogGate.ProductionConstructionAuthorized,
                m9cQualificationConstructionAuthorized =
                    Hp8C40ProductionWatchdogGate.M9CPhysicalQualificationConstructionAuthorized,
                m9dQualificationConstructionAuthorized =
                    Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationConstructionAuthorized,
                productionRestoreCompleted,
                finalFirmwareOwned,
                restoreEvidence,
                cleanupRestoreAttempted,
                cleanupRestoreSucceeded,
                preWriteSamples,
                supervisionSamples,
                finalFirmwareSamples,
                events
            };

            try
            {
                await WriteJsonAsync(resultPath, evidence).ConfigureAwait(false);
            }
            catch (Exception resultFailure)
            {
                Console.Error.WriteLine(
                    $"P15B could not write durable result evidence: {resultFailure}");

                if (exitCode == 0)
                {
                    exitCode = 243;
                }
            }
        }

        return exitCode;
    }

    private static AdaptiveFanPolicyConfig BuildManualAdapterConfig()
    {
        static IReadOnlyList<AdaptiveFanCurvePoint> Curve(double low, double high) =>
        [
            new AdaptiveFanCurvePoint(low, 10),
            new AdaptiveFanCurvePoint(high, 50)
        ];

        // Manual mode consumes only the validated 10..50 envelope. The curves
        // remain valid but are never evaluated by P15B.
        return new AdaptiveFanPolicyConfig(
            10,
            50,
            4,
            2,
            3,
            1,
            TimeSpan.FromSeconds(3),
            Curve(40, 90),
            Curve(35, 85),
            Curve(0, 120),
            Curve(0, 140),
            Curve(0, 100),
            Curve(0, 100));
    }

    private static bool IsBoundedLoad(TelemetrySnapshot snapshot) =>
        snapshot.CpuPackagePowerW.HasValue &&
        snapshot.GpuPowerW.HasValue &&
        snapshot.CpuPackagePowerW.Value <= MaximumCpuPackagePowerW &&
        snapshot.GpuPowerW.Value <= MaximumGpuPowerW;

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
                $"P15B stale telemetry: {age.TotalSeconds:0.000}s.");
        }

        if (previousTimestamp.HasValue)
        {
            var gap = snapshot.Timestamp - previousTimestamp.Value;
            if (gap < TimeSpan.Zero || gap > MaximumInterSampleGap)
            {
                throw new InvalidOperationException(
                    $"P15B invalid inter-sample gap: {gap.TotalSeconds:0.000}s.");
            }
        }

        if (!snapshot.IsComplete ||
            !snapshot.CpuCoreTelemetryComplete ||
            !snapshot.GpuTemperatureC.HasValue ||
            snapshot.GpuTemperatureC.Value <= 0)
        {
            throw new InvalidOperationException(
                "P15B requires complete package/core/power/load/GPU/tach telemetry.");
        }

        var raw = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            now,
            fanWritePathPresent);

        return confirmation.Apply(hardware, snapshot, raw);
    }

    private static void EnsureImmediatePhysicalLimits(
        TelemetrySnapshot snapshot,
        string context)
    {
        if (snapshot.CpuControlTemperatureC.HasValue &&
            snapshot.CpuControlTemperatureC.Value >= CpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"P15B {context} CPU physical abort: " +
                $"{snapshot.CpuControlTemperatureC.Value:0.0} C >= {CpuPhysicalAbortC:0} C.");
        }

        if (snapshot.GpuTemperatureC.HasValue &&
            snapshot.GpuTemperatureC.Value >= GpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"P15B {context} GPU physical abort: " +
                $"{snapshot.GpuTemperatureC.Value:0.0} C >= {GpuPhysicalAbortC:0} C.");
        }
    }

    private static void EnsurePowerStatus(SystemPowerStatusSample status)
    {
        if (!status.AcOnline ||
            !status.BatteryPresent ||
            status.BatteryPercent > 100 ||
            status.BatteryPercent < MinimumBatteryPercent)
        {
            throw new InvalidOperationException(
                $"P15B AC/battery sanity gate refused: {status}");
        }
    }

    private static async Task<Hp8C40EcControlState> ReadExpectedEcConfirmedAsync(
        Hp8C40EcControlStateProbe probe,
        int expectedCpu,
        int expectedGpu,
        string context,
        CancellationToken cancellationToken)
    {
        const int maxReads = 3;
        const int requiredConsecutiveUnexpected = 2;

        var previous = probe.ReadControlEvidence();
        if (MatchesExpected(previous, expectedCpu, expectedGpu))
        {
            return previous;
        }

        var consecutiveUnexpected = 1;

        for (var read = 2; read <= maxReads; read++)
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(25),
                    cancellationToken)
                .ConfigureAwait(false);

            var next = probe.ReadControlEvidence();
            if (MatchesExpected(next, expectedCpu, expectedGpu))
            {
                return next;
            }

            if (SameUnexpected(previous, next))
            {
                consecutiveUnexpected++;
                if (consecutiveUnexpected >= requiredConsecutiveUnexpected)
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

    private static async Task<List<EcEvidence>> ReadStableFirmwareOwnedAsync(
        Hp8C40EcControlStateProbe probe,
        CancellationToken cancellationToken)
    {
        const int maximumReads = 6;
        const int requiredConsecutive = 2;
        var samples = new List<EcEvidence>();
        var consecutive = 0;

        for (var read = 1; read <= maximumReads; read++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = probe.ReadControlEvidence();

            samples.Add(new EcEvidence(
                read,
                DateTimeOffset.UtcNow,
                state.CpuSetpoint,
                state.GpuSetpoint,
                state.MaxFan,
                state.FanSwitch,
                state.CpuRpm,
                state.GpuRpm));

            if (MatchesExpected(state, byte.MaxValue, byte.MaxValue))
            {
                consecutive++;
                if (consecutive >= requiredConsecutive)
                {
                    return samples;
                }
            }
            else
            {
                consecutive = 0;
            }

            if (read < maximumReads)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "P15B strong restore did not produce two consecutive independent FF/FF control-evidence samples within six reads.");
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
        left.CpuSetpoint == right.CpuSetpoint &&
        left.GpuSetpoint == right.GpuSetpoint &&
        left.MaxFan == right.MaxFan &&
        left.FanSwitch == right.FanSwitch;

    private static void EnsureExpectedEc(
        Hp8C40EcControlState state,
        int expectedCpu,
        int expectedGpu,
        string context)
    {
        if (!MatchesExpected(state, expectedCpu, expectedGpu))
        {
            throw new InvalidOperationException(
                $"P15B EC evidence refused during {context}: " +
                $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}; " +
                $"guards=0x{state.MaxFan:X2}/0x{state.FanSwitch:X2}; " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}; " +
                $"expected={expectedCpu}/{expectedGpu} guards=00/00.");
        }
    }

    private static async Task WaitForParentContinueAsync(
        string continuePath,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < ParentContinueTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(continuePath))
            {
                var value =
                    (await File.ReadAllTextAsync(
                            continuePath,
                            cancellationToken)
                        .ConfigureAwait(false))
                    .Trim();

                if (!string.Equals(
                        value,
                        "P15B-CONTINUE",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "P15B parent continue marker is invalid.");
                }

                return;
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "P15B timed out waiting for parent OWNED journal/service verification.");
    }

    private static SampleEvidence ToEvidence(
        int index,
        TelemetrySnapshot snapshot,
        SafetyGateResult safety,
        bool boundedLoad,
        int consecutiveAccepted) =>
        new(
            index,
            snapshot.Timestamp,
            boundedLoad,
            consecutiveAccepted,
            snapshot.CpuControlTemperatureC,
            snapshot.CpuPackagePowerW,
            snapshot.CpuLoadPercent,
            snapshot.GpuTemperatureC,
            snapshot.GpuPowerW,
            snapshot.GpuLoadPercent,
            snapshot.CpuFanRpm,
            snapshot.GpuFanRpm,
            safety.PreconditionsReady,
            safety.CustomControlPermitted,
            safety.ThermalEmergency,
            safety.Reasons.Count == 0
                ? "none"
                : string.Join(" | ", safety.Reasons));

    private static async Task WriteJsonAsync(
        string path,
        object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "P15B evidence path has no parent directory."));

        var json = JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
        using var process = Process.GetCurrentProcess();
        return (
            process.Id,
            process.StartTime.ToUniversalTime().Ticks);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? FindKnownConflictingControllerProcess()
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
                processes = Process.GetProcessesByName(name);
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
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }

    private sealed record SampleEvidence(
        int Index,
        DateTimeOffset TimestampUtc,
        bool BoundedLoad,
        int ConsecutiveAccepted,
        double? CpuEffectiveC,
        double? CpuPackagePowerW,
        double? CpuLoadPercent,
        double? GpuTemperatureC,
        double? GpuPowerW,
        double? GpuLoadPercent,
        double? CpuFanRpm,
        double? GpuFanRpm,
        bool PreconditionsReady,
        bool CustomControlPermitted,
        bool ThermalEmergency,
        string SafetyReasons);

    private sealed record EcEvidence(
        int Index,
        DateTimeOffset TimestampUtc,
        byte CpuSetpoint,
        byte GpuSetpoint,
        byte MaxFan,
        byte FanSwitch,
        ushort CpuRpm,
        ushort GpuRpm);

    private sealed record EventEvidence(
        DateTimeOffset TimestampUtc,
        string Event,
        string Detail);
}
