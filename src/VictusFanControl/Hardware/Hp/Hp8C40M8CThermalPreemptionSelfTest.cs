using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Safety;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Hardware-free M8C qualification for thermal preemption semantics.
///
/// The test uses the real SafetyGate, the real exact-target HP 8C40 temporal
/// confirmation layer and the real FanControlCoordinator. Only the backend is
/// synthetic, so no WMI, EC, watchdog service or fan write can occur.
/// </summary>
public static class Hp8C40M8CThermalPreemptionSelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;

        failures += await TestCpuFiveUniqueSamplesAsync(output);
        failures += await TestGpuImmediateAsync(output);
        failures += await TestCpuHardImmediateAsync(output);
        failures += await TestThermalPreemptionCancelsInFlightApplyAsync(output);

        output.WriteLine();
        output.WriteLine(
            failures == 0
                ? "HP 8C40 M8C synthetic thermal-preemption self-test: PASS"
                : $"HP 8C40 M8C synthetic thermal-preemption self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 221;
    }

    private static async Task<int> TestCpuFiveUniqueSamplesAsync(
        TextWriter output)
    {
        var backend = new QualificationBackend();
        await using var coordinator =
            new FanControlCoordinator(backend);

        var hardware = BuildHardware();
        var confirmation =
            new Hp8C40ThermalEmergencyConfirmation();
        var start = DateTimeOffset.UtcNow;

        var ready = BuildReadySafety(
            hardware,
            confirmation,
            start);

        var entered = await coordinator.TryEnterCustomAsync(
            ready,
            CancellationToken.None);

        if (!entered)
        {
            return Report(
                output,
                "CPU 95 C x5 preempts exactly on fifth unique sample",
                false);
        }

        await coordinator.ApplyAsync(
            new FanCommand(
                50,
                50,
                "M8C synthetic 50/50 ownership model"),
            ready,
            CancellationToken.None);

        var firstFourStayedCustom = true;
        var rawThresholdSeen = true;
        var explicitSyntheticMarker = true;

        for (var ordinal = 1; ordinal <= 4; ordinal++)
        {
            var frame =
                Hp8C40M8CThermalQualificationInjection
                    .CpuConfirmedThreshold(
                        start + TimeSpan.FromSeconds(ordinal),
                        ordinal);

            var evaluation =
                Hp8C40M8CThermalQualificationInjection.Evaluate(
                    hardware,
                    frame,
                    confirmation);

            rawThresholdSeen &=
                evaluation.Raw.ThermalEmergency &&
                !evaluation.Raw.CustomControlPermitted;

            explicitSyntheticMarker &=
                string.Equals(
                    evaluation.EvidenceKind,
                    Hp8C40M8CThermalQualificationInjection.EvidenceMarker,
                    StringComparison.Ordinal);

            var retained =
                await coordinator.EnforceSafetyAsync(
                    evaluation.Effective,
                    $"M8C synthetic CPU threshold {ordinal}/5",
                    CancellationToken.None);

            firstFourStayedCustom &=
                retained &&
                !evaluation.Effective.ThermalEmergency &&
                evaluation.Effective.CustomControlPermitted &&
                coordinator.Authority == FanAuthority.Custom &&
                backend.RestoreCalls == 0;
        }

        var fifthFrame =
            Hp8C40M8CThermalQualificationInjection
                .CpuConfirmedThreshold(
                    start + TimeSpan.FromSeconds(5),
                    ordinal: 5);

        var fifth =
            Hp8C40M8CThermalQualificationInjection.Evaluate(
                hardware,
                fifthFrame,
                confirmation);

        var fifthRetained =
            await coordinator.EnforceSafetyAsync(
                fifth.Effective,
                "M8C synthetic CPU threshold 5/5",
                CancellationToken.None);

        var passed =
            firstFourStayedCustom &&
            rawThresholdSeen &&
            explicitSyntheticMarker &&
            fifth.Raw.ThermalEmergency &&
            fifth.Effective.ThermalEmergency &&
            !fifth.Effective.CustomControlPermitted &&
            !fifthRetained &&
            confirmation.CurrentCpuConsecutiveHighSamples ==
                Hp8C40ThermalEmergencyConfirmation.RequiredConsecutiveCpuSamples &&
            backend.RestoreCalls == 1 &&
            !backend.Active &&
            coordinator.Authority == FanAuthority.Firmware;

        return Report(
            output,
            "CPU 95 C x5 preempts exactly on fifth unique sample",
            passed);
    }

    private static async Task<int> TestGpuImmediateAsync(
        TextWriter output)
    {
        var backend = new QualificationBackend();
        await using var coordinator =
            new FanControlCoordinator(backend);

        var hardware = BuildHardware();
        var confirmation =
            new Hp8C40ThermalEmergencyConfirmation();
        var start = DateTimeOffset.UtcNow;

        var ready =
            BuildReadySafety(
                hardware,
                confirmation,
                start);

        var entered =
            await coordinator.TryEnterCustomAsync(
                ready,
                CancellationToken.None);

        if (entered)
        {
            await coordinator.ApplyAsync(
                new FanCommand(
                    50,
                    50,
                    "M8C synthetic GPU case ownership model"),
                ready,
                CancellationToken.None);
        }

        var frame =
            Hp8C40M8CThermalQualificationInjection
                .GpuImmediateThreshold(
                    start + TimeSpan.FromSeconds(1));

        var evaluation =
            Hp8C40M8CThermalQualificationInjection.Evaluate(
                hardware,
                frame,
                confirmation);

        var retained =
            entered &&
            await coordinator.EnforceSafetyAsync(
                evaluation.Effective,
                "M8C synthetic GPU 87 C immediate threshold",
                CancellationToken.None);

        return Report(
            output,
            "GPU 87 C preempts immediately",
            entered &&
            evaluation.Raw.ThermalEmergency &&
            evaluation.Effective.ThermalEmergency &&
            !evaluation.Effective.CustomControlPermitted &&
            !retained &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestCpuHardImmediateAsync(
        TextWriter output)
    {
        var backend = new QualificationBackend();
        await using var coordinator =
            new FanControlCoordinator(backend);

        var hardware = BuildHardware();
        var confirmation =
            new Hp8C40ThermalEmergencyConfirmation();
        var start = DateTimeOffset.UtcNow;

        var ready =
            BuildReadySafety(
                hardware,
                confirmation,
                start);

        var entered =
            await coordinator.TryEnterCustomAsync(
                ready,
                CancellationToken.None);

        if (entered)
        {
            await coordinator.ApplyAsync(
                new FanCommand(
                    50,
                    50,
                    "M8C synthetic hard-CPU case ownership model"),
                ready,
                CancellationToken.None);
        }

        var frame =
            Hp8C40M8CThermalQualificationInjection
                .CpuHardImmediateThreshold(
                    start + TimeSpan.FromSeconds(1));

        var evaluation =
            Hp8C40M8CThermalQualificationInjection.Evaluate(
                hardware,
                frame,
                confirmation);

        var retained =
            entered &&
            await coordinator.EnforceSafetyAsync(
                evaluation.Effective,
                "M8C synthetic CPU 99 C immediate hard threshold",
                CancellationToken.None);

        return Report(
            output,
            "CPU 99 C preempts immediately",
            entered &&
            evaluation.Raw.ThermalEmergency &&
            evaluation.Effective.ThermalEmergency &&
            !evaluation.Effective.CustomControlPermitted &&
            !retained &&
            confirmation.CurrentCpuConsecutiveHighSamples == 0 &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestThermalPreemptionCancelsInFlightApplyAsync(
        TextWriter output)
    {
        var backend = new QualificationBackend();
        await using var coordinator =
            new FanControlCoordinator(backend);

        var hardware = BuildHardware();
        var confirmation =
            new Hp8C40ThermalEmergencyConfirmation();
        var start = DateTimeOffset.UtcNow;

        var ready =
            BuildReadySafety(
                hardware,
                confirmation,
                start);

        var entered =
            await coordinator.TryEnterCustomAsync(
                ready,
                CancellationToken.None);

        if (!entered)
        {
            return Report(
                output,
                "thermal handoff cancels in-flight ApplyAsync",
                false);
        }

        await coordinator.ApplyAsync(
            new FanCommand(
                50,
                50,
                "M8C synthetic initial ownership"),
            ready,
            CancellationToken.None);

        backend.BlockFutureApplyUntilCancelled = true;
        backend.ApplyStarted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight =
            coordinator.ApplyAsync(
                new FanCommand(
                    50,
                    50,
                    "M8C synthetic in-flight command race"),
                ready,
                CancellationToken.None)
            .AsTask();

        await backend.ApplyStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));

        var frame =
            Hp8C40M8CThermalQualificationInjection
                .GpuImmediateThreshold(
                    start + TimeSpan.FromSeconds(2));

        var evaluation =
            Hp8C40M8CThermalQualificationInjection.Evaluate(
                hardware,
                frame,
                confirmation);

        var enforcement =
            coordinator.EnforceSafetyAsync(
                evaluation.Effective,
                "M8C synthetic thermal preemption versus in-flight ApplyAsync",
                CancellationToken.None)
            .AsTask();

        var cancelled = false;
        try
        {
            await inFlight;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await enforcement;

        return Report(
            output,
            "thermal handoff cancels in-flight ApplyAsync",
            evaluation.Effective.ThermalEmergency &&
            cancelled &&
            backend.RestoreCalls == 1 &&
            !backend.Active &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static SafetyGateResult BuildReadySafety(
        HardwareIdentity hardware,
        Hp8C40ThermalEmergencyConfirmation confirmation,
        DateTimeOffset timestamp)
    {
        var frame =
            Hp8C40M8CThermalQualificationInjection
                .CpuConfirmedThreshold(
                    timestamp,
                    ordinal: 1);

        var safeSnapshot =
            frame.Snapshot with
            {
                CpuTemperatureC = 70.0,
                CpuCoreTemperatures =
                    frame.Snapshot.CpuCoreTemperatures
                        .Select((core, index) =>
                            core with
                            {
                                TemperatureC =
                                    index == 0
                                        ? 75.0
                                        : 70.0
                            })
                        .ToArray()
            };

        var raw = SafetyGate.Evaluate(
            hardware,
            Runtime.SystemState.Healthy,
            safeSnapshot,
            timestamp,
            fanWritePathPresent: true);

        return confirmation.Apply(
            hardware,
            safeSnapshot,
            raw);
    }

    private static HardwareIdentity BuildHardware() =>
        new(
            "HP",
            "8C40",
            "63.43",
            "HP",
            "Victus by HP Gaming Laptop 15-fa1xxx",
            "9D0R1LA#AKH",
            Hp8C40TargetProfile.ValidatedBiosVersion);

    private static int Report(
        TextWriter output,
        string name,
        bool pass)
    {
        output.WriteLine(
            $"{(pass ? "PASS" : "FAIL")}  {name}");

        return pass ? 0 : 1;
    }

    private sealed class QualificationBackend : IFanControlBackend
    {
        public string Name => "HP 8C40 M8C synthetic backend";

        public bool CanWrite => true;

        public FanBackendCapabilities Capabilities =>
            new(
                Hp8C40TargetProfile.BoardProduct,
                Hp8C40TargetProfile.MinimumPhysicallyQualifiedFanLevel,
                Hp8C40TargetProfile.MaximumPhysicallyQualifiedFanLevel,
                SupportsIndependentLevels: false);

        public int EnterCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public bool Active { get; private set; }

        public bool BlockFutureApplyUntilCancelled { get; set; }

        public TaskCompletionSource<bool> ApplyStarted { get; set; } =
            new(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ProbeControlDependencyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<FanBackendStatus> GetStatusAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCalls++;

            return ValueTask.FromResult(
                new FanBackendStatus(
                    Name,
                    CanWrite: true,
                    CustomModeActive: Active,
                    OwnershipValid: Active,
                    FeedbackHealthy: Active,
                    Detail: "synthetic M8C backend"));
        }

        public ValueTask EnterCustomModeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCalls++;
            Active = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask ApplyAsync(
            FanCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            ApplyStarted.TrySetResult(true);

            if (BlockFutureApplyUntilCancelled)
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
        }

        public ValueTask RestoreFirmwareAutoAsync(
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            Active = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
