using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>
/// Composes the independently qualified CPU and GPU Guardian domains.
///
/// The combined lifecycle owns only composition semantics. CPU RAPL ownership,
/// journaling and restore remain inside QualifiedCpuGuardianDomainLifecycle;
/// GPU journal/Reset semantics remain inside QualifiedGpuGuardianDomainLifecycle.
///
/// Initial enable is CPU first, then GPU. If the GPU enable fails, CPU rollback
/// is attempted immediately. Normal release always attempts BOTH domains even
/// if the first one fails, so one cleanup failure cannot suppress the other.
/// </summary>
internal sealed class CombinedGuardianDomainLifecycle :
    IGuardianDomainLifecycle,
    ICpuPowerSourceTransitionSink,
    IGpuClockSourceTransitionSink,
    IDisposable
{
    private readonly IGuardianDomainLifecycle _cpuLifecycle;
    private readonly ICpuPowerSourceTransitionSink _cpuTransitions;
    private readonly IGuardianDomainLifecycle _gpuLifecycle;
    private readonly IGpuClockSourceTransitionSink _gpuTransitions;
    private readonly IDisposable? _cpuDisposable;
    private readonly IDisposable? _gpuDisposable;
    private readonly PerformancePowerSourceKind? _requiredInitialSource;

    private int _enableCalls;
    private int _releaseCalls;
    private bool _lastCpuEnabled;
    private bool _lastGpuEnabled;
    private PerformancePowerSourceKind? _lastInitialSource;
    private string? _lastReleaseReason;
    private bool _disposed;

    internal CombinedGuardianDomainLifecycle(
        IGuardianDomainLifecycle cpuLifecycle,
        ICpuPowerSourceTransitionSink cpuTransitions,
        IGuardianDomainLifecycle gpuLifecycle,
        IGpuClockSourceTransitionSink gpuTransitions,
        PerformancePowerSourceKind? requiredInitialSource = null)
    {
        _cpuLifecycle =
            cpuLifecycle ??
            throw new ArgumentNullException(
                nameof(cpuLifecycle));

        _cpuTransitions =
            cpuTransitions ??
            throw new ArgumentNullException(
                nameof(cpuTransitions));

        _gpuLifecycle =
            gpuLifecycle ??
            throw new ArgumentNullException(
                nameof(gpuLifecycle));

        _gpuTransitions =
            gpuTransitions ??
            throw new ArgumentNullException(
                nameof(gpuTransitions));

        _cpuDisposable =
            cpuLifecycle as IDisposable;

        _gpuDisposable =
            gpuLifecycle as IDisposable;

        _requiredInitialSource =
            requiredInitialSource;
    }

    public GuardianDomainLifecycleSnapshot Snapshot
    {
        get
        {
            var cpu =
                _cpuLifecycle.Snapshot;

            var gpu =
                _gpuLifecycle.Snapshot;

            return new GuardianDomainLifecycleSnapshot(
                EnableCalls:
                    _enableCalls,
                ReleaseCalls:
                    _releaseCalls,
                LastCpuEnabled:
                    _lastCpuEnabled,
                LastGpuEnabled:
                    _lastGpuEnabled,
                LastInitialSource:
                    _lastInitialSource,
                LastReleaseReason:
                    _lastReleaseReason,
                CpuHardwareWriteAttempts:
                    cpu.CpuHardwareWriteAttempts,
                GpuHardwareWriteAttempts:
                    gpu.GpuHardwareWriteAttempts,
                CpuState:
                    cpu.CpuState,
                GpuState:
                    gpu.GpuState,
                CpuStatus:
                    cpu.CpuStatus,
                GpuStatus:
                    gpu.GpuStatus);
        }
    }

    public async ValueTask EnableAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        PerformancePowerSourceKind initialSource,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        _lastCpuEnabled =
            cpuEnabled;

        _lastGpuEnabled =
            gpuEnabled;

        _lastInitialSource =
            initialSource;

        if (!cpuEnabled ||
            !gpuEnabled)
        {
            throw new InvalidOperationException(
                "Combined Guardian qualification requires CPU=true and GPU=true.");
        }

        if (_requiredInitialSource.HasValue &&
            initialSource !=
                _requiredInitialSource.Value)
        {
            throw new InvalidOperationException(
                "Combined Guardian initial source must be " +
                _requiredInitialSource.Value +
                "; observed " +
                initialSource +
                ".");
        }

        await _cpuLifecycle.EnableAsync(
                cpuEnabled: true,
                gpuEnabled: false,
                initialSource,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _gpuLifecycle.EnableAsync(
                    cpuEnabled: false,
                    gpuEnabled: true,
                    initialSource,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception gpuFailure)
        {
            Exception? rollbackFailure =
                null;

            try
            {
                await _cpuLifecycle.ReleaseAsync(
                        cpuEnabled: true,
                        gpuEnabled: false,
                        "COMBINED_ENABLE_GPU_FAILURE_CPU_ROLLBACK",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                rollbackFailure =
                    ex;
            }

            if (rollbackFailure is not null)
            {
                throw new AggregateException(
                    "Combined Guardian GPU enable failed and CPU rollback also failed.",
                    gpuFailure,
                    rollbackFailure);
            }

            throw;
        }

        _enableCalls++;
    }

    public async ValueTask ReleaseAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        string reason,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        _lastCpuEnabled =
            cpuEnabled;

        _lastGpuEnabled =
            gpuEnabled;

        _lastReleaseReason =
            reason;

        if (!cpuEnabled ||
            !gpuEnabled)
        {
            throw new InvalidOperationException(
                "Combined Guardian release requires both qualified domains.");
        }

        Exception? cpuFailure =
            null;

        Exception? gpuFailure =
            null;

        try
        {
            await _cpuLifecycle.ReleaseAsync(
                    cpuEnabled: true,
                    gpuEnabled: false,
                    reason,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            cpuFailure =
                ex;
        }

        try
        {
            await _gpuLifecycle.ReleaseAsync(
                    cpuEnabled: false,
                    gpuEnabled: true,
                    reason,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            gpuFailure =
                ex;
        }

        if (cpuFailure is not null &&
            gpuFailure is not null)
        {
            throw new AggregateException(
                "Combined Guardian CPU and GPU release both failed after both cleanup attempts.",
                cpuFailure,
                gpuFailure);
        }

        if (cpuFailure is not null)
            throw cpuFailure;

        if (gpuFailure is not null)
            throw gpuFailure;

        _releaseCalls++;
    }

    public CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source)
    {
        ThrowIfDisposed();

        return _cpuTransitions.HandleConfirmedSourceChange(
            source);
    }

    GpuClockPresetTransitionResult
        IGpuClockSourceTransitionSink.HandleConfirmedSourceChange(
            PerformancePowerSourceKind source)
    {
        ThrowIfDisposed();

        return _gpuTransitions.HandleConfirmedSourceChange(
            source);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Exception? cpuFailure =
            null;

        Exception? gpuFailure =
            null;

        try
        {
            _cpuDisposable?.Dispose();
        }
        catch (Exception ex)
        {
            cpuFailure =
                ex;
        }

        try
        {
            if (!ReferenceEquals(
                    _gpuDisposable,
                    _cpuDisposable))
            {
                _gpuDisposable?.Dispose();
            }
        }
        catch (Exception ex)
        {
            gpuFailure =
                ex;
        }

        _disposed =
            true;

        if (cpuFailure is not null &&
            gpuFailure is not null)
        {
            throw new AggregateException(
                "Combined Guardian domain disposal failed for both domains.",
                cpuFailure,
                gpuFailure);
        }

        if (cpuFailure is not null)
            throw cpuFailure;

        if (gpuFailure is not null)
            throw gpuFailure;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }
}

internal static class CombinedGuardianDomainLifecycleSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            NormalCombinedLifecycle(
                output);

            GpuEnableFailureRollsBackCpu(
                output);

            ReleaseFailureDoesNotSuppressOtherDomain(
                output);

            InitialSourceConstraintIsFailClosed(
                output);

            output.WriteLine(
                "Combined Guardian domain self-test: PASS (dual enable, rollback, independent cleanup, source routing; fake domains only).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Combined Guardian domain self-test: FAIL - " +
                ex);

            return 1;
        }
    }

    private static void NormalCombinedLifecycle(
        TextWriter output)
    {
        var cpu =
            new FakeCpuDomain();

        var gpu =
            new FakeGpuDomain();

        using var combined =
            new CombinedGuardianDomainLifecycle(
                cpu,
                cpu,
                gpu,
                gpu,
                requiredInitialSource:
                    PerformancePowerSourceKind.Ac);

        combined.EnableAsync(
                cpuEnabled: true,
                gpuEnabled: true,
                PerformancePowerSourceKind.Ac,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var cpuTransition =
            combined.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Battery);

        var gpuTransition =
            ((IGpuClockSourceTransitionSink)combined)
                .HandleConfirmedSourceChange(
                    PerformancePowerSourceKind.Battery);

        combined.ReleaseAsync(
                cpuEnabled: true,
                gpuEnabled: true,
                "SELF_TEST",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var snapshot =
            combined.Snapshot;

        Require(
            cpu.EnableCalls == 1 &&
            gpu.EnableCalls == 1 &&
            cpu.ReleaseCalls == 1 &&
            gpu.ReleaseCalls == 1 &&
            cpuTransition.Succeeded &&
            gpuTransition.Succeeded &&
            cpu.TransitionCalls == 1 &&
            gpu.TransitionCalls == 1 &&
            snapshot.EnableCalls == 1 &&
            snapshot.ReleaseCalls == 1 &&
            snapshot.CpuHardwareWriteAttempts == 4 &&
            snapshot.GpuHardwareWriteAttempts == 4 &&
            snapshot.CpuState == "Disabled" &&
            snapshot.GpuState == "Disabled",
            "normal combined lifecycle");

        output.WriteLine(
            "PASS combined lifecycle forwards CPU/GPU enable, source transition and release exactly once");
    }

    private static void GpuEnableFailureRollsBackCpu(
        TextWriter output)
    {
        var cpu =
            new FakeCpuDomain();

        var gpu =
            new FakeGpuDomain
            {
                ThrowOnEnable =
                    true
            };

        using var combined =
            new CombinedGuardianDomainLifecycle(
                cpu,
                cpu,
                gpu,
                gpu,
                requiredInitialSource:
                    PerformancePowerSourceKind.Ac);

        RequireThrows<InvalidOperationException>(
            () =>
                combined.EnableAsync(
                        cpuEnabled: true,
                        gpuEnabled: true,
                        PerformancePowerSourceKind.Ac,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
            "GPU enable failure must surface");

        Require(
            cpu.EnableCalls == 1 &&
            gpu.EnableCalls == 1 &&
            cpu.ReleaseCalls == 1 &&
            combined.Snapshot.EnableCalls == 0,
            "GPU enable failure rolls CPU back before returning");

        output.WriteLine(
            "PASS combined GPU-enable failure attempts immediate CPU rollback");
    }

    private static void ReleaseFailureDoesNotSuppressOtherDomain(
        TextWriter output)
    {
        var cpu =
            new FakeCpuDomain
            {
                ThrowOnRelease =
                    true
            };

        var gpu =
            new FakeGpuDomain();

        using var combined =
            new CombinedGuardianDomainLifecycle(
                cpu,
                cpu,
                gpu,
                gpu);

        RequireThrows<InvalidOperationException>(
            () =>
                combined.ReleaseAsync(
                        cpuEnabled: true,
                        gpuEnabled: true,
                        "SELF_TEST_RELEASE_FAILURE",
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
            "CPU release failure must surface");

        Require(
            cpu.ReleaseCalls == 1 &&
            gpu.ReleaseCalls == 1,
            "GPU release must still be attempted after CPU release failure");

        output.WriteLine(
            "PASS combined cleanup attempts GPU release even after CPU release failure");
    }

    private static void InitialSourceConstraintIsFailClosed(
        TextWriter output)
    {
        var cpu =
            new FakeCpuDomain();

        var gpu =
            new FakeGpuDomain();

        using var combined =
            new CombinedGuardianDomainLifecycle(
                cpu,
                cpu,
                gpu,
                gpu,
                requiredInitialSource:
                    PerformancePowerSourceKind.Ac);

        RequireThrows<InvalidOperationException>(
            () =>
                combined.EnableAsync(
                        cpuEnabled: true,
                        gpuEnabled: true,
                        PerformancePowerSourceKind.Battery,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
            "combined physical gate must start on AC");

        Require(
            cpu.EnableCalls == 0 &&
            gpu.EnableCalls == 0,
            "wrong initial source is rejected before either domain");

        output.WriteLine(
            "PASS combined initial-source constraint rejects before any domain enable");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                label);
        }
    }

    private static void RequireThrows<T>(
        Action action,
        string label)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(
            label);
    }

    private sealed class FakeCpuDomain :
        IGuardianDomainLifecycle,
        ICpuPowerSourceTransitionSink
    {
        internal int EnableCalls;
        internal int ReleaseCalls;
        internal int TransitionCalls;
        internal bool ThrowOnEnable;
        internal bool ThrowOnRelease;

        public GuardianDomainLifecycleSnapshot Snapshot =>
            new(
                EnableCalls,
                ReleaseCalls,
                true,
                false,
                PerformancePowerSourceKind.Ac,
                "FAKE_CPU",
                CpuHardwareWriteAttempts:
                    4,
                GpuHardwareWriteAttempts:
                    0,
                CpuState:
                    "Disabled",
                GpuState:
                    null,
                CpuStatus:
                    "FAKE_CPU_OK",
                GpuStatus:
                    null);

        public ValueTask EnableAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            PerformancePowerSourceKind initialSource,
            CancellationToken cancellationToken)
        {
            EnableCalls++;

            if (!cpuEnabled ||
                gpuEnabled)
            {
                throw new InvalidOperationException(
                    "fake CPU received wrong domain selection");
            }

            if (ThrowOnEnable)
            {
                throw new InvalidOperationException(
                    "synthetic CPU enable failure");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            string reason,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;

            if (!cpuEnabled ||
                gpuEnabled)
            {
                throw new InvalidOperationException(
                    "fake CPU received wrong release selection");
            }

            if (ThrowOnRelease)
            {
                throw new InvalidOperationException(
                    "synthetic CPU release failure");
            }

            return ValueTask.CompletedTask;
        }

        public CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
            PerformancePowerSourceKind source)
        {
            TransitionCalls++;

            return new CpuPowerPresetTransitionResult(
                CpuPowerPresetTransitionDisposition.EnabledPresetSwitched,
                source,
                source ==
                    PerformancePowerSourceKind.Ac
                    ? PerformancePresetSlot.Ac
                    : PerformancePresetSlot.Battery,
                Succeeded: true,
                Status:
                    "FAKE_CPU_TRANSITION");
        }
    }

    private sealed class FakeGpuDomain :
        IGuardianDomainLifecycle,
        IGpuClockSourceTransitionSink
    {
        internal int EnableCalls;
        internal int ReleaseCalls;
        internal int TransitionCalls;
        internal bool ThrowOnEnable;
        internal bool ThrowOnRelease;

        public GuardianDomainLifecycleSnapshot Snapshot =>
            new(
                EnableCalls,
                ReleaseCalls,
                false,
                true,
                PerformancePowerSourceKind.Ac,
                "FAKE_GPU",
                CpuHardwareWriteAttempts:
                    0,
                GpuHardwareWriteAttempts:
                    4,
                CpuState:
                    null,
                GpuState:
                    "Disabled",
                CpuStatus:
                    null,
                GpuStatus:
                    "FAKE_GPU_OK");

        public ValueTask EnableAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            PerformancePowerSourceKind initialSource,
            CancellationToken cancellationToken)
        {
            EnableCalls++;

            if (cpuEnabled ||
                !gpuEnabled)
            {
                throw new InvalidOperationException(
                    "fake GPU received wrong domain selection");
            }

            if (ThrowOnEnable)
            {
                throw new InvalidOperationException(
                    "synthetic GPU enable failure");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            string reason,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;

            if (cpuEnabled ||
                !gpuEnabled)
            {
                throw new InvalidOperationException(
                    "fake GPU received wrong release selection");
            }

            if (ThrowOnRelease)
            {
                throw new InvalidOperationException(
                    "synthetic GPU release failure");
            }

            return ValueTask.CompletedTask;
        }

        public GpuClockPresetTransitionResult HandleConfirmedSourceChange(
            PerformancePowerSourceKind source)
        {
            TransitionCalls++;

            return new GpuClockPresetTransitionResult(
                GpuClockPresetTransitionDisposition.EnabledPresetSwitched,
                source,
                source ==
                    PerformancePowerSourceKind.Ac
                    ? PerformancePresetSlot.Ac
                    : PerformancePresetSlot.Battery,
                Succeeded: true,
                SessionState:
                    GpuClockSessionState.ActiveUnverified,
                Status:
                    "FAKE_GPU_TRANSITION");
        }
    }
}
