namespace VictusFanControl.Performance;

internal static class PerformanceSourceTransitionCoordinatorSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            PrimeDoesNotDispatch(output);
            DuplicateNotificationIsSuppressed(output);
            ConfirmedChangeDispatchesBothDomains(output);
            CpuFailureDoesNotBlockGpu(output);
            GpuFailureDoesNotRevertCpu(output);
            SelectedDomainMaskSkipsDisabledSink(output);
            QueryFailureDispatchesUnknownOnce(output);
            UnprimedNotificationOnlyPrimes(output);

            output.WriteLine(
                "Performance source transition coordinator self-test: PASS (direct-query authority, duplicate suppression, independent CPU/GPU dispatch, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance source transition coordinator self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void PrimeDoesNotDispatch(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    raw: 1));

        var cpu =
            new FakeCpuSink();

        var gpu =
            new FakeGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        var result =
            coordinator.Prime();

        Require(
            result.Primed &&
            coordinator.LastSource ==
                PerformancePowerSourceKind.Ac &&
            cpu.Calls == 0 &&
            gpu.Calls == 0,
            "prime must be read-only");

        output.WriteLine(
            "PASS source coordinator prime records AC with zero domain dispatch");
    }

    private static void DuplicateNotificationIsSuppressed(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1));

        var cpu =
            new FakeCpuSink();

        var gpu =
            new FakeGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        _ =
            coordinator.Prime();

        var result =
            coordinator.HandleNotificationSignal();

        Require(
            result.DuplicateSuppressed &&
            cpu.Calls == 0 &&
            gpu.Calls == 0,
            "same-source notification must be zero-write");

        output.WriteLine(
            "PASS initial/same-source Windows notification is suppressed after direct query");
    }

    private static void ConfirmedChangeDispatchesBothDomains(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0));

        var cpu =
            new FakeCpuSink();

        var gpu =
            new FakeGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        _ =
            coordinator.Prime();

        var result =
            coordinator.HandleNotificationSignal();

        Require(
            result.Succeeded &&
            result.CpuAttempted &&
            result.GpuAttempted &&
            cpu.Calls == 1 &&
            gpu.Calls == 1 &&
            cpu.LastSource ==
                PerformancePowerSourceKind.Battery &&
            gpu.LastSource ==
                PerformancePowerSourceKind.Battery,
            "confirmed Battery must reach both domains");

        output.WriteLine(
            "PASS confirmed AC->Battery source change independently dispatches CPU and GPU");
    }

    private static void CpuFailureDoesNotBlockGpu(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0));

        var cpu =
            new FakeCpuSink
            {
                ThrowOnCall = true
            };

        var gpu =
            new FakeGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        _ =
            coordinator.Prime();

        var result =
            coordinator.HandleNotificationSignal();

        Require(
            !result.Succeeded &&
            result.CpuException is not null &&
            result.GpuResult?.Succeeded == true &&
            cpu.Calls == 1 &&
            gpu.Calls == 1,
            "CPU exception must not suppress GPU dispatch");

        output.WriteLine(
            "PASS CPU transition failure does not block independent GPU transition attempt");
    }

    private static void GpuFailureDoesNotRevertCpu(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0));

        var cpu =
            new FakeCpuSink();

        var gpu =
            new FakeGpuSink
            {
                ThrowOnCall = true
            };

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        _ =
            coordinator.Prime();

        var result =
            coordinator.HandleNotificationSignal();

        Require(
            !result.Succeeded &&
            result.CpuResult?.Succeeded == true &&
            result.GpuException is not null &&
            cpu.Calls == 1 &&
            gpu.Calls == 1,
            "GPU exception must not revert or suppress CPU dispatch");

        output.WriteLine(
            "PASS GPU transition failure does not revert successful independent CPU dispatch");
    }

    private static void SelectedDomainMaskSkipsDisabledSink(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0));

        var cpu =
            new FakeCpuSink();

        var gpu =
            new FakeGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        _ =
            coordinator.Prime();

        var result =
            coordinator.HandleNotificationSignal(
                dispatchCpu: true,
                dispatchGpu: false);

        Require(
            result.Succeeded &&
            result.CpuAttempted &&
            !result.GpuAttempted &&
            cpu.Calls == 1 &&
            gpu.Calls == 0,
            "selected-domain dispatch must not invoke a disabled sink");

        output.WriteLine(
            "PASS source coordinator dispatch mask invokes only explicitly enabled domains");
    }

    private static void QueryFailureDispatchesUnknownOnce(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                new PerformancePowerSourceObservation(
                    Succeeded: false,
                    Source:
                        PerformancePowerSourceKind.Unknown,
                    RawAcLineStatus: null,
                    BatteryPercent: null,
                    BatteryFlags: null,
                    Status:
                        "SYNTHETIC_QUERY_FAILURE"),
                new PerformancePowerSourceObservation(
                    Succeeded: false,
                    Source:
                        PerformancePowerSourceKind.Unknown,
                    RawAcLineStatus: null,
                    BatteryPercent: null,
                    BatteryFlags: null,
                    Status:
                        "SYNTHETIC_QUERY_FAILURE_2"));

        var cpu =
            new FakeCpuSink();

        var gpu =
            new FakeGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        _ =
            coordinator.Prime();

        var first =
            coordinator.HandleNotificationSignal();

        var second =
            coordinator.HandleNotificationSignal();

        Require(
            !first.Succeeded &&
            first.CpuAttempted &&
            first.GpuAttempted &&
            cpu.LastSource ==
                PerformancePowerSourceKind.Unknown &&
            gpu.LastSource ==
                PerformancePowerSourceKind.Unknown,
            "query failure must dispatch fail-closed Unknown");

        Require(
            second.DuplicateSuppressed &&
            cpu.Calls == 1 &&
            gpu.Calls == 1,
            "repeated Unknown query failure must not retry releases");

        output.WriteLine(
            "PASS source query failure dispatches one fail-closed Unknown release episode");
    }

    private static void UnprimedNotificationOnlyPrimes(
        TextWriter output)
    {
        var reader =
            new FakeReader(
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0));

        var cpu =
            new FakeCpuSink();

        var gpu =
            new FakeGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        var result =
            coordinator.HandleNotificationSignal();

        Require(
            result.Primed &&
            cpu.Calls == 0 &&
            gpu.Calls == 0 &&
            coordinator.LastSource ==
                PerformancePowerSourceKind.Battery,
            "first unprimed signal must establish baseline only");

        output.WriteLine(
            "PASS first unprimed notification cannot create startup CPU/GPU authority");
    }

    private static PerformancePowerSourceObservation Observation(
        PerformancePowerSourceKind source,
        byte raw) =>
        new(
            Succeeded: true,
            Source: source,
            RawAcLineStatus: raw,
            BatteryPercent: 100,
            BatteryFlags: 1,
            Status:
                "FIXTURE");

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeReader :
        IPerformancePowerSourceReader
    {
        private readonly Queue<PerformancePowerSourceObservation>
            _observations;

        internal FakeReader(
            params PerformancePowerSourceObservation[] observations)
        {
            _observations =
                new Queue<PerformancePowerSourceObservation>(
                    observations);
        }

        public PerformancePowerSourceObservation Read()
        {
            if (_observations.Count == 0)
            {
                throw new InvalidOperationException(
                    "No synthetic source observation remains.");
            }

            return _observations.Dequeue();
        }
    }

    private sealed class FakeCpuSink :
        ICpuPowerSourceTransitionSink
    {
        internal int Calls;
        internal PerformancePowerSourceKind? LastSource;
        internal bool ThrowOnCall;

        public CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
            PerformancePowerSourceKind source)
        {
            Calls++;
            LastSource = source;

            if (ThrowOnCall)
            {
                throw new IOException(
                    "synthetic CPU transition failure");
            }

            return new CpuPowerPresetTransitionResult(
                CpuPowerPresetTransitionDisposition.EnabledPresetSwitched,
                source,
                source ==
                    PerformancePowerSourceKind.Ac
                    ? PerformancePresetSlot.Ac
                    : source ==
                        PerformancePowerSourceKind.Battery
                        ? PerformancePresetSlot.Battery
                        : null,
                Succeeded: true,
                Status: "FAKE_CPU_TRANSITION");
        }
    }

    private sealed class FakeGpuSink :
        IGpuClockSourceTransitionSink
    {
        internal int Calls;
        internal PerformancePowerSourceKind? LastSource;
        internal bool ThrowOnCall;

        public GpuClockPresetTransitionResult HandleConfirmedSourceChange(
            PerformancePowerSourceKind source)
        {
            Calls++;
            LastSource = source;

            if (ThrowOnCall)
            {
                throw new IOException(
                    "synthetic GPU transition failure");
            }

            return new GpuClockPresetTransitionResult(
                GpuClockPresetTransitionDisposition.EnabledPresetSwitched,
                source,
                source ==
                    PerformancePowerSourceKind.Ac
                    ? PerformancePresetSlot.Ac
                    : source ==
                        PerformancePowerSourceKind.Battery
                        ? PerformancePresetSlot.Battery
                        : null,
                Succeeded: true,
                SessionState:
                    GpuClockSessionState.ActiveUnverified,
                Status:
                    "FAKE_GPU_TRANSITION");
        }
    }
}
