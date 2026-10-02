using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

public static class AdaptiveFanProductionControllerSelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;
        failures += await TestClosedGatesNeverTouchBackendAsync(output);
        failures += await TestManualEqualOnlyAndNoRetransmitAsync(output);
        failures += await TestAutomaticNoRetransmitAndSafetyReleaseAsync(output);
        failures += await TestManualRangeGuardAsync(output);
        failures += await TestManualFreshSafetyRefreshAndRetryAsync(output);
        failures += await TestManualFreshSafetyExhaustionRestoresAsync(output);

        output.WriteLine();
        output.WriteLine(
            failures == 0
                ? "Adaptive production controller self-test: PASS"
                : $"Adaptive production controller self-test: FAIL ({failures} case(s))");
        return failures == 0 ? 0 : 34;
    }

    private static async Task<int> TestClosedGatesNeverTouchBackendAsync(TextWriter output)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);
        var controller = new AdaptiveFanProductionController(
            coordinator, BuildConfig(), false, false);

        var snapshot = BuildSnapshot(DateTimeOffset.UtcNow, 45, 40);
        var safety = BuildSafety(snapshot);

        var automaticMode = await controller.SetModeAsync(
            AdaptiveFanProductionMode.Automatic, CancellationToken.None);
        var automatic = await controller.ProcessAutomaticAsync(
            snapshot, safety, CancellationToken.None);
        var manualMode = await controller.SetModeAsync(
            AdaptiveFanProductionMode.Manual, CancellationToken.None);
        var manual = await controller.ApplyManualAsync(
            30, safety, CancellationToken.None);

        return Report(
            output,
            "closed post-M9 gates cannot touch the backend",
            controller.Mode == AdaptiveFanProductionMode.Firmware &&
            automaticMode.Action == AdaptiveFanProductionActionKind.Blocked &&
            automatic.Action == AdaptiveFanProductionActionKind.Blocked &&
            manualMode.Action == AdaptiveFanProductionActionKind.Blocked &&
            manual.Action == AdaptiveFanProductionActionKind.Blocked &&
            backend.EnterCalls == 0 &&
            backend.ApplyCalls == 0 &&
            backend.RestoreCalls == 0);
    }

    private static async Task<int> TestManualEqualOnlyAndNoRetransmitAsync(TextWriter output)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);
        var controller = new AdaptiveFanProductionController(
            coordinator, BuildConfig(), true, false);

        _ = await controller.SetModeAsync(
            AdaptiveFanProductionMode.Manual, CancellationToken.None);

        var t0 = DateTimeOffset.UtcNow;
        var s1 = BuildSnapshot(t0, 55, 50);
        var first = await controller.ApplyManualAsync(
            30, BuildSafety(s1), CancellationToken.None);

        var s2 = BuildSnapshot(t0 + TimeSpan.FromSeconds(1), 55, 50);
        var duplicate = await controller.ApplyManualAsync(
            30, BuildSafety(s2), CancellationToken.None);

        var s3 = BuildSnapshot(t0 + TimeSpan.FromSeconds(2), 55, 50);
        var changed = await controller.ApplyManualAsync(
            31, BuildSafety(s3), CancellationToken.None);

        var released = await controller.ReleaseToFirmwareAsync(
            "manual self-test complete", CancellationToken.None);

        var equalOnly = backend.Commands.All(
            c => c.CpuLevel == c.GpuLevel && c.CpuLevel is >= 10 and <= 50);

        return Report(
            output,
            "manual path is equal-only and suppresses unchanged retransmission",
            first.Action == AdaptiveFanProductionActionKind.EnterCustomAndApply &&
            duplicate.Action == AdaptiveFanProductionActionKind.HoldCustom &&
            changed.Action == AdaptiveFanProductionActionKind.ApplyChangedLevel &&
            released.Action == AdaptiveFanProductionActionKind.RestoreFirmware &&
            backend.EnterCalls == 1 &&
            backend.ApplyCalls == 2 &&
            backend.RestoreCalls == 1 &&
            equalOnly &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestAutomaticNoRetransmitAndSafetyReleaseAsync(TextWriter output)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);
        var controller = new AdaptiveFanProductionController(
            coordinator, BuildConfig(), false, true);

        _ = await controller.SetModeAsync(
            AdaptiveFanProductionMode.Automatic, CancellationToken.None);

        var t0 = DateTimeOffset.UtcNow;
        var low1 = BuildSnapshot(t0, 40, 35);
        var first = await controller.ProcessAutomaticAsync(
            low1, BuildSafety(low1), CancellationToken.None);

        var low2 = BuildSnapshot(t0 + TimeSpan.FromSeconds(1), 40, 35);
        var hold = await controller.ProcessAutomaticAsync(
            low2, BuildSafety(low2), CancellationToken.None);

        var high = BuildSnapshot(t0 + TimeSpan.FromSeconds(2), 90, 85);
        var raised = await controller.ProcessAutomaticAsync(
            high, BuildSafety(high), CancellationToken.None);

        var emergency = BuildSnapshot(t0 + TimeSpan.FromSeconds(3), 70, 87);
        var released = await controller.ProcessAutomaticAsync(
            emergency, BuildSafety(emergency), CancellationToken.None);

        return Report(
            output,
            "automatic path suppresses unchanged WMI intent and releases on SafetyGate loss",
            first.Action == AdaptiveFanProductionActionKind.EnterCustomAndApply &&
            hold.Action == AdaptiveFanProductionActionKind.HoldCustom &&
            raised.Action == AdaptiveFanProductionActionKind.ApplyChangedLevel &&
            released.Action == AdaptiveFanProductionActionKind.RestoreFirmware &&
            backend.EnterCalls == 1 &&
            backend.ApplyCalls == 2 &&
            backend.RestoreCalls == 1 &&
            backend.Commands.All(c => c.CpuLevel == c.GpuLevel) &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestManualRangeGuardAsync(TextWriter output)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);
        var controller = new AdaptiveFanProductionController(
            coordinator, BuildConfig(), true, false);

        _ = await controller.SetModeAsync(
            AdaptiveFanProductionMode.Manual, CancellationToken.None);

        var rejected = false;
        try
        {
            var snapshot = BuildSnapshot(DateTimeOffset.UtcNow, 50, 45);
            _ = await controller.ApplyManualAsync(
                0, BuildSafety(snapshot), CancellationToken.None);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        return Report(
            output,
            "manual level 0 is rejected before backend access",
            rejected &&
            backend.EnterCalls == 0 &&
            backend.ApplyCalls == 0 &&
            backend.RestoreCalls == 0);
    }

    private static async Task<int> TestManualFreshSafetyRefreshAndRetryAsync(TextWriter output)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);
        var controller = new AdaptiveFanProductionController(
            coordinator, BuildConfig(), true, false);

        _ = await controller.SetModeAsync(
            AdaptiveFanProductionMode.Manual, CancellationToken.None);

        var t0 = DateTimeOffset.UtcNow;
        var initial = BuildSafety(BuildSnapshot(t0, 50, 45));
        var duringAdmission = BuildSafety(BuildSnapshot(t0 + TimeSpan.FromSeconds(1), 50, 45));
        var firstRefresh = BuildSafety(BuildSnapshot(t0 + TimeSpan.FromSeconds(2), 50, 45));
        var supersedingRefresh = BuildSafety(BuildSnapshot(t0 + TimeSpan.FromSeconds(3), 50, 45));
        var finalRefresh = BuildSafety(BuildSnapshot(t0 + TimeSpan.FromSeconds(4), 50, 45));

        var supervisorTasks = new List<Task<bool>>();
        backend.AfterEnter = () =>
        {
            supervisorTasks.Add(
                coordinator.EnforceSafetyAsync(
                    duringAdmission,
                    "synthetic newer safety during read-only Manual admission",
                    CancellationToken.None).AsTask());
        };

        var refreshCalls = 0;
        SafetyGateResult? RefreshSafety()
        {
            refreshCalls++;
            if (refreshCalls == 1)
            {
                supervisorTasks.Add(
                    coordinator.EnforceSafetyAsync(
                        supersedingRefresh,
                        "synthetic newer safety between Manual refresh and Apply",
                        CancellationToken.None).AsTask());
                return firstRefresh;
            }

            return finalRefresh;
        }

        var result = await controller.ApplyManualAsync(
            30,
            initial,
            RefreshSafety,
            CancellationToken.None);

        if (supervisorTasks.Count > 0)
        {
            await Task.WhenAll(supervisorTasks);
        }

        var authorityBeforeCleanup = coordinator.Authority;
        var restoreCallsBeforeCleanup = backend.RestoreCalls;

        await controller.ReleaseToFirmwareAsync(
            "fresh-Safety Manual retry self-test cleanup",
            CancellationToken.None);

        return Report(
            output,
            "manual path refreshes SafetyGate after admission and retries a superseded command without duplicate writes",
            result.Action == AdaptiveFanProductionActionKind.EnterCustomAndApply &&
            result.EqualFanLevel == 30 &&
            refreshCalls == 2 &&
            backend.EnterCalls == 1 &&
            backend.ApplyCalls == 1 &&
            backend.Commands.Count == 1 &&
            backend.Commands[0].CpuLevel == 30 &&
            backend.Commands[0].GpuLevel == 30 &&
            restoreCallsBeforeCleanup == 0 &&
            authorityBeforeCleanup == FanAuthority.Custom &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestManualFreshSafetyExhaustionRestoresAsync(TextWriter output)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);
        var controller = new AdaptiveFanProductionController(
            coordinator, BuildConfig(), true, false);

        _ = await controller.SetModeAsync(
            AdaptiveFanProductionMode.Manual, CancellationToken.None);

        var t0 = DateTimeOffset.UtcNow;
        var initial = BuildSafety(BuildSnapshot(t0, 50, 45));
        var duringAdmission = BuildSafety(BuildSnapshot(t0 + TimeSpan.FromSeconds(1), 50, 45));
        var staleRefresh = BuildSafety(BuildSnapshot(t0 + TimeSpan.FromSeconds(2), 50, 45));
        var newerRefresh = BuildSafety(BuildSnapshot(t0 + TimeSpan.FromSeconds(3), 50, 45));

        var supervisorTasks = new List<Task<bool>>();
        backend.AfterEnter = () =>
        {
            supervisorTasks.Add(
                coordinator.EnforceSafetyAsync(
                    duringAdmission,
                    "synthetic newer safety during exhausted Manual admission",
                    CancellationToken.None).AsTask());
        };

        var refreshCalls = 0;
        var superseded = false;
        SafetyGateResult? RefreshSafety()
        {
            refreshCalls++;
            if (!superseded)
            {
                superseded = true;
                supervisorTasks.Add(
                    coordinator.EnforceSafetyAsync(
                        newerRefresh,
                        "synthetic permanently newer Manual safety",
                        CancellationToken.None).AsTask());
            }

            return staleRefresh;
        }

        var result = await controller.ApplyManualAsync(
            30,
            initial,
            RefreshSafety,
            CancellationToken.None);

        if (supervisorTasks.Count > 0)
        {
            await Task.WhenAll(supervisorTasks);
        }

        return Report(
            output,
            "manual fresh-Safety retry exhaustion restores Firmware without issuing a fan command",
            result.Action == AdaptiveFanProductionActionKind.RestoreFirmware &&
            refreshCalls == 4 &&
            backend.EnterCalls == 1 &&
            backend.ApplyCalls == 0 &&
            backend.RestoreCalls == 1 &&
            backend.Commands.Count == 0 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static AdaptiveFanPolicyConfig BuildConfig()
    {
        static IReadOnlyList<AdaptiveFanCurvePoint> Curve(double lo, double hi) =>
        [
            new AdaptiveFanCurvePoint(lo, 10),
            new AdaptiveFanCurvePoint(hi, 50)
        ];

        return new AdaptiveFanPolicyConfig(
            10, 50, 4, 2, 3, 1, TimeSpan.FromSeconds(3),
            Curve(40, 90), Curve(35, 85), Curve(0, 120),
            Curve(0, 140), Curve(0, 100), Curve(0, 100));
    }

    private static SafetyGateResult BuildSafety(TelemetrySnapshot snapshot)
    {
        var hardware = new HardwareIdentity(
            Hp8C40TargetProfile.BoardManufacturer,
            Hp8C40TargetProfile.BoardProduct,
            Hp8C40TargetProfile.BoardVersion,
            Hp8C40TargetProfile.SystemManufacturer,
            Hp8C40TargetProfile.SystemProductName,
            $"{Hp8C40TargetProfile.SystemSkuPrefix}#AKH",
            Hp8C40TargetProfile.ValidatedBiosVersion);

        return SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            snapshot.Timestamp,
            fanWritePathPresent: true);
    }

    private static TelemetrySnapshot BuildSnapshot(
        DateTimeOffset timestamp,
        double cpuC,
        double gpuC)
    {
        var cores = Enumerable.Range(
                0, Hp8C40TargetProfile.Instance.ExpectedPhysicalCoreCount)
            .Select(index => new CpuCoreTemperatureSample(
                index, index, index < 6 ? "Performance" : "Efficiency", cpuC))
            .ToArray();

        return new TelemetrySnapshot(
            timestamp,
            "Intel Core i7-13700H",
            cpuC,
            40,
            50,
            Hp8C40TargetProfile.ExpectedGpuName,
            gpuC,
            60,
            70,
            3000,
            3000)
        {
            CpuCoreTemperatures = cores,
            CpuExpectedPhysicalCoreCount =
                Hp8C40TargetProfile.Instance.ExpectedPhysicalCoreCount
        };
    }

    private static int Report(TextWriter output, string name, bool pass)
    {
        output.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
        return pass ? 0 : 1;
    }

    private sealed class RecordingBackend : IFanControlBackend
    {
        public string Name => "adaptive-production-self-test";
        public bool CanWrite => true;
        public FanBackendCapabilities Capabilities =>
            new(Hp8C40TargetProfile.BoardProduct, 10, 50, false);

        public int EnterCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public bool Active { get; private set; }
        public Action? AfterEnter { get; set; }
        public List<FanCommand> Commands { get; } = [];

        public ValueTask ProbeControlDependencyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new FanBackendStatus(
                Name, CanWrite, Active, true, true, "synthetic"));
        }

        public ValueTask EnterCustomModeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCalls++;
            Active = true;
            AfterEnter?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplyAsync(FanCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            Commands.Add(command);
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreFirmwareAutoAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCalls++;
            Active = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
