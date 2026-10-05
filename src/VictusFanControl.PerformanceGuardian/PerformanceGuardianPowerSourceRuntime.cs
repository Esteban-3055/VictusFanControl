using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal readonly record struct GuardianPowerSourceRuntimeSnapshot(
    bool Active,
    bool CpuEnabled,
    bool GpuEnabled,
    int StartCalls,
    int StopCalls,
    int ListenerRegistrations,
    int NotificationSignals,
    int ReconciliationSignals,
    int DuplicateSignals,
    int CpuDispatchAttempts,
    int GpuDispatchAttempts,
    PerformancePowerSourceKind LastSource,
    string? LastStatus,
    string? LastStopReason,
    string? Failure);

internal interface IGuardianPowerSourceRuntime :
    IDisposable
{
    GuardianPowerSourceRuntimeSnapshot Snapshot { get; }

    PerformanceSourceDispatchResult Prime(
        bool cpuEnabled,
        bool gpuEnabled);

    void ActivateListener();

    void Start(
        bool cpuEnabled,
        bool gpuEnabled);

    void Stop(
        string reason);
}

internal interface IGuardianPowerSourceNotificationListenerFactory
{
    IDisposable Create(
        Action onSignal);
}

/// <summary>
/// Session-scoped AC/Battery notification runtime used by the detached
/// PerformanceGuardian.
///
/// It intentionally owns no CPU RAPL or GPU NVML command transport. The only
/// outputs are the narrow CPU/GPU source-transition sinks supplied by the
/// caller. Step 6E wires recording sinks; a later gate may replace them.
///
/// Ordering is deliberate:
/// explicit session enable -> direct GetSystemPowerStatus prime -> listener
/// registration -> notification -> fresh query -> dedup -> selected sinks.
/// </summary>
internal sealed class GuardianPerformancePowerSourceRuntime :
    IGuardianPowerSourceRuntime
{
    private readonly object _sync =
        new();

    private readonly IPerformancePowerSourceReader _reader;
    private readonly ICpuPowerSourceTransitionSink _cpu;
    private readonly IGpuClockSourceTransitionSink _gpu;
    private readonly IGuardianPowerSourceNotificationListenerFactory
        _listenerFactory;

    private PerformanceSourceTransitionCoordinator? _coordinator;
    private IDisposable? _listener;

    private bool _primed;
    private bool _active;
    private bool _cpuEnabled;
    private bool _gpuEnabled;
    private int _startCalls;
    private int _stopCalls;
    private int _listenerRegistrations;
    private int _notificationSignals;
    private int _reconciliationSignals;
    private int _duplicateSignals;
    private int _cpuDispatchAttempts;
    private int _gpuDispatchAttempts;
    private PerformancePowerSourceKind _lastSource =
        PerformancePowerSourceKind.Unknown;
    private string? _lastStatus;
    private string? _lastStopReason;
    private string? _failure;
    private bool _disposed;

    internal GuardianPerformancePowerSourceRuntime(
        IPerformancePowerSourceReader reader,
        ICpuPowerSourceTransitionSink cpu,
        IGpuClockSourceTransitionSink gpu,
        IGuardianPowerSourceNotificationListenerFactory listenerFactory)
    {
        _reader =
            reader ??
            throw new ArgumentNullException(
                nameof(reader));

        _cpu =
            cpu ??
            throw new ArgumentNullException(
                nameof(cpu));

        _gpu =
            gpu ??
            throw new ArgumentNullException(
                nameof(gpu));

        _listenerFactory =
            listenerFactory ??
            throw new ArgumentNullException(
                nameof(listenerFactory));
    }

    public GuardianPowerSourceRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return SnapshotLocked();
            }
        }
    }

    public PerformanceSourceDispatchResult Prime(
        bool cpuEnabled,
        bool gpuEnabled)
    {
        ThrowIfDisposed();

        if (!cpuEnabled &&
            !gpuEnabled)
        {
            throw new InvalidOperationException(
                "Performance Guardian source runtime requires at least one explicitly enabled domain.");
        }

        lock (_sync)
        {
            if (_primed ||
                _active ||
                _listener is not null ||
                _coordinator is not null)
            {
                throw new InvalidOperationException(
                    "Performance Guardian source runtime is already primed or active.");
            }

            _startCalls++;

            var coordinator =
                new PerformanceSourceTransitionCoordinator(
                    _reader,
                    _cpu,
                    _gpu);

            // Direct query always precedes both initial hardware Apply and
            // RegisterPowerSettingNotification.
            var prime =
                coordinator.Prime();

            _coordinator =
                coordinator;

            _cpuEnabled =
                cpuEnabled;

            _gpuEnabled =
                gpuEnabled;

            _lastSource =
                prime.Observation.Source;

            _lastStatus =
                prime.Status;

            _failure =
                prime.Observation.Succeeded
                    ? null
                    : prime.Observation.Status;

            _primed =
                true;

            return prime;
        }
    }

    public void ActivateListener()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (!_primed ||
                _coordinator is null)
            {
                throw new InvalidOperationException(
                    "Performance Guardian source runtime must be primed before listener activation.");
            }

            if (_active ||
                _listener is not null)
            {
                throw new InvalidOperationException(
                    "Performance Guardian source listener is already active.");
            }

            // Domain Apply has already completed before this point. Mark active
            // before registration so an immediate Windows notification cannot
            // be lost between registration and return from the listener factory.
            _active =
                true;

            try
            {
                _listener =
                    _listenerFactory.Create(
                        HandleNotificationSignal);

                _listenerRegistrations++;

                // Close the prime -> initial domain Apply -> listener-register
                // race with one fresh direct query while the callback fence is
                // still held. If the source changed before registration, this
                // performs the missed transition exactly once. A native event
                // queued by registration then observes the new last source and
                // is suppressed as a duplicate.
                _reconciliationSignals++;

                ProcessSourceSignalLocked();
            }
            catch (Exception ex)
            {
                _active =
                    false;

                _failure =
                    ex.ToString();

                _lastStatus =
                    "PERFORMANCE_GUARDIAN_SOURCE_LISTENER_START_FAILED";

                throw;
            }
        }
    }

    public void Start(
        bool cpuEnabled,
        bool gpuEnabled)
    {
        _ =
            Prime(
                cpuEnabled,
                gpuEnabled);

        ActivateListener();
    }

    public void Stop(
        string reason)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(
                reason))
        {
            throw new ArgumentException(
                "Performance Guardian source runtime stop requires a reason.",
                nameof(reason));
        }

        IDisposable? listener;

        lock (_sync)
        {
            if (!_primed &&
                !_active &&
                _listener is null &&
                _coordinator is null)
            {
                return;
            }

            _stopCalls++;

            // Fence notification dispatch before unregistering the window.
            _active =
                false;

            _primed =
                false;

            listener =
                _listener;

            _listener =
                null;

            _coordinator =
                null;

            _cpuEnabled =
                false;

            _gpuEnabled =
                false;

            _lastStopReason =
                reason;

            _lastStatus =
                "PERFORMANCE_GUARDIAN_SOURCE_RUNTIME_STOPPED";
        }

        listener?.Dispose();
    }

    private void HandleNotificationSignal()
    {
        lock (_sync)
        {
            if (!_active ||
                _coordinator is null)
            {
                return;
            }

            _notificationSignals++;

            ProcessSourceSignalLocked();
        }
    }

    private void ProcessSourceSignalLocked()
    {
        if (!_active ||
            _coordinator is null)
        {
            return;
        }

        try
        {
            var result =
                _coordinator.HandleNotificationSignal(
                    _cpuEnabled,
                    _gpuEnabled);

            _lastSource =
                result.Observation.Source;

            _lastStatus =
                result.Status;

            if (result.DuplicateSuppressed)
                _duplicateSignals++;

            if (result.CpuAttempted)
                _cpuDispatchAttempts++;

            if (result.GpuAttempted)
                _gpuDispatchAttempts++;

            if (result.CpuException is not null ||
                result.GpuException is not null)
            {
                _failure =
                    result.CpuException ??
                    result.GpuException;
            }
            else if (result.CpuAttempted &&
                     result.CpuResult.HasValue &&
                     !result.CpuResult.Value.Succeeded)
            {
                _failure =
                    "CPU_SOURCE_TRANSITION_FAILED: " +
                    result.CpuResult.Value.Status;
            }
            else if (result.GpuAttempted &&
                     result.GpuResult.HasValue &&
                     !result.GpuResult.Value.Succeeded)
            {
                _failure =
                    "GPU_SOURCE_TRANSITION_FAILED: " +
                    result.GpuResult.Value.Status;
            }
        }
        catch (Exception ex)
        {
            // A source signal must never unwind through WndProc. Keep the
            // runtime active so a later notification can still be observed,
            // but retain explicit diagnostic evidence.
            _failure =
                ex.ToString();

            _lastStatus =
                "PERFORMANCE_GUARDIAN_SOURCE_SIGNAL_FAILED";
        }
    }

    private GuardianPowerSourceRuntimeSnapshot SnapshotLocked() =>
        new(
            Active:
                _active,
            CpuEnabled:
                _cpuEnabled,
            GpuEnabled:
                _gpuEnabled,
            StartCalls:
                _startCalls,
            StopCalls:
                _stopCalls,
            ListenerRegistrations:
                _listenerRegistrations,
            NotificationSignals:
                _notificationSignals,
            ReconciliationSignals:
                _reconciliationSignals,
            DuplicateSignals:
                _duplicateSignals,
            CpuDispatchAttempts:
                _cpuDispatchAttempts,
            GpuDispatchAttempts:
                _gpuDispatchAttempts,
            LastSource:
                _lastSource,
            LastStatus:
                _lastStatus,
            LastStopReason:
                _lastStopReason,
            Failure:
                _failure);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_active ||
            _listener is not null ||
            _coordinator is not null)
        {
            Stop(
                "RUNTIME_DISPOSE");
        }

        _disposed =
            true;
    }
}

internal sealed class RecordingGuardianCpuSourceTransitionSink :
    ICpuPowerSourceTransitionSink
{
    internal int Calls { get; private set; }

    internal PerformancePowerSourceKind? LastSource { get; private set; }

    internal bool ThrowOnCall { get; set; }

    public CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source)
    {
        Calls++;

        LastSource =
            source;

        if (ThrowOnCall)
        {
            throw new IOException(
                "synthetic Guardian CPU recording transition failure");
        }

        return new CpuPowerPresetTransitionResult(
            CpuPowerPresetTransitionDisposition.EnabledPresetSwitched,
            source,
            Slot(source),
            Succeeded: true,
            Status:
                "GUARDIAN_RECORDING_CPU_SOURCE_TRANSITION__ZERO_HARDWARE_IO");
    }

    private static PerformancePresetSlot? Slot(
        PerformancePowerSourceKind source) =>
        source switch
        {
            PerformancePowerSourceKind.Ac =>
                PerformancePresetSlot.Ac,

            PerformancePowerSourceKind.Battery =>
                PerformancePresetSlot.Battery,

            _ => null
        };
}

internal sealed class RecordingGuardianGpuSourceTransitionSink :
    IGpuClockSourceTransitionSink
{
    internal int Calls { get; private set; }

    internal PerformancePowerSourceKind? LastSource { get; private set; }

    internal bool ThrowOnCall { get; set; }

    public GpuClockPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source)
    {
        Calls++;

        LastSource =
            source;

        if (ThrowOnCall)
        {
            throw new IOException(
                "synthetic Guardian GPU recording transition failure");
        }

        return new GpuClockPresetTransitionResult(
            GpuClockPresetTransitionDisposition.EnabledPresetSwitched,
            source,
            Slot(source),
            Succeeded: true,
            SessionState:
                GpuClockSessionState.ActiveUnverified,
            Status:
                "GUARDIAN_RECORDING_GPU_SOURCE_TRANSITION__ZERO_HARDWARE_IO");
    }

    private static PerformancePresetSlot? Slot(
        PerformancePowerSourceKind source) =>
        source switch
        {
            PerformancePowerSourceKind.Ac =>
                PerformancePresetSlot.Ac,

            PerformancePowerSourceKind.Battery =>
                PerformancePresetSlot.Battery,

            _ => null
        };
}

/// <summary>
/// Real Windows GUID_ACDC_POWER_SOURCE listener for PerformanceGuardian.
///
/// POWERBROADCAST_SETTING.Data is intentionally ignored. The callback is only a
/// trigger; GuardianPerformancePowerSourceRuntime performs a fresh direct query
/// through PerformanceSourceTransitionCoordinator.
/// </summary>
internal sealed class WindowsGuardianPowerSourceNotificationListenerFactory :
    IGuardianPowerSourceNotificationListenerFactory
{
    public IDisposable Create(
        Action onSignal) =>
        new WindowsGuardianPowerSourceNotificationListener(
            onSignal);
}

internal sealed class WindowsGuardianPowerSourceNotificationListener :
    IDisposable
{
    private const int StartupTimeoutMilliseconds =
        5000;

    private const int StopJoinTimeoutMilliseconds =
        5000;

    private readonly Action _onSignal;
    private readonly ManualResetEventSlim _ready =
        new(initialState: false);

    private readonly Thread _thread;

    private Exception? _startupFailure;
    private IntPtr _windowHandle;
    private bool _disposed;

    internal WindowsGuardianPowerSourceNotificationListener(
        Action onSignal)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Performance Guardian power-source notification listener requires Windows.");
        }

        _onSignal =
            onSignal ??
            throw new ArgumentNullException(
                nameof(onSignal));

        _thread =
            new Thread(
                ThreadMain)
            {
                IsBackground =
                    true,

                Name =
                    "VictusFanControl.PerformanceGuardian.PowerSource"
            };

        _thread.SetApartmentState(
            ApartmentState.STA);

        _thread.Start();

        if (!_ready.Wait(
                StartupTimeoutMilliseconds))
        {
            Dispose();

            throw new TimeoutException(
                "Performance Guardian power-source notification listener did not become ready.");
        }

        if (_startupFailure is not null)
        {
            var failure =
                _startupFailure;

            Dispose();

            throw new InvalidOperationException(
                "Performance Guardian power-source notification listener failed during startup.",
                failure);
        }
    }

    private void ThreadMain()
    {
        try
        {
            using var context =
                new ApplicationContext();

            using var window =
                new PowerNotificationWindow(
                    _onSignal,
                    context);

            _windowHandle =
                window.Handle;

            _ready.Set();

            Application.Run(
                context);
        }
        catch (Exception ex)
        {
            _startupFailure =
                ex;

            _ready.Set();
        }
        finally
        {
            _windowHandle =
                IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed =
            true;

        var handle =
            _windowHandle;

        if (handle !=
            IntPtr.Zero)
        {
            _ =
                NativeMethods.PostMessage(
                    handle,
                    PowerNotificationWindow.WmGuardianStop,
                    IntPtr.Zero,
                    IntPtr.Zero);
        }

        if (_thread.IsAlive &&
            !_thread.Join(
                StopJoinTimeoutMilliseconds))
        {
            throw new TimeoutException(
                "Performance Guardian power-source listener thread did not stop.");
        }

        _ready.Dispose();
    }

    private sealed class PowerNotificationWindow :
        NativeWindow,
        IDisposable
    {
        internal const int WmGuardianStop =
            0x8001;

        private const int WmPowerBroadcast =
            0x0218;

        private const int PbtPowerSettingChange =
            0x8013;

        private const uint DeviceNotifyWindowHandle =
            0;

        private static readonly Guid GuidAcDcPowerSource =
            new(
                "5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

        private readonly Action _onSignal;
        private readonly ApplicationContext _context;

        private IntPtr _notificationHandle;

        internal PowerNotificationWindow(
            Action onSignal,
            ApplicationContext context)
        {
            _onSignal =
                onSignal;

            _context =
                context;

            CreateHandle(
                new CreateParams
                {
                    Caption =
                        "VictusFanControl.PerformanceGuardian.PowerNotificationWindow"
                });

            var setting =
                GuidAcDcPowerSource;

            _notificationHandle =
                NativeMethods.RegisterPowerSettingNotification(
                    Handle,
                    ref setting,
                    DeviceNotifyWindowHandle);

            if (_notificationHandle ==
                IntPtr.Zero)
            {
                var error =
                    Marshal.GetLastWin32Error();

                DestroyHandle();

                throw new Win32Exception(
                    error,
                    "RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE) failed.");
            }
        }

        protected override void WndProc(
            ref Message message)
        {
            if (message.Msg ==
                WmGuardianStop)
            {
                _context.ExitThread();

                return;
            }

            if (message.Msg ==
                    WmPowerBroadcast &&
                message.WParam.ToInt32() ==
                    PbtPowerSettingChange &&
                message.LParam !=
                    IntPtr.Zero)
            {
                var settingGuid =
                    Marshal.PtrToStructure<Guid>(
                        message.LParam);

                if (settingGuid ==
                    GuidAcDcPowerSource)
                {
                    // Deliberately ignore POWERBROADCAST_SETTING.Data.
                    _onSignal();
                }
            }

            base.WndProc(
                ref message);
        }

        public void Dispose()
        {
            if (_notificationHandle !=
                IntPtr.Zero)
            {
                _ =
                    NativeMethods.UnregisterPowerSettingNotification(
                        _notificationHandle);

                _notificationHandle =
                    IntPtr.Zero;
            }

            if (Handle !=
                IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport(
            "user32.dll",
            SetLastError = true)]
        internal static extern IntPtr RegisterPowerSettingNotification(
            IntPtr recipient,
            ref Guid powerSettingGuid,
            uint flags);

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterPowerSettingNotification(
            IntPtr handle);

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr hWnd,
            int message,
            IntPtr wParam,
            IntPtr lParam);
    }
}

internal static class PerformanceGuardianPowerSourceRuntimeSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            AcBatteryAcAndDuplicate(output);
            UnknownQueryFailureIsBounded(output);
            CpuFailureDoesNotBlockGpu(output);
            GpuFailureDoesNotRevertCpu(output);
            SelectedDomainsAreHonored(output);

            output.WriteLine(
                "Performance Guardian source runtime self-test: PASS (prime-before-listener, AC/Battery transitions, duplicate suppression, Unknown, independent domain failures, zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance Guardian source runtime self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void AcBatteryAcAndDuplicate(
        TextWriter output)
    {
        var reader =
            new QueuePowerSourceReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1));

        var cpu =
            new RecordingGuardianCpuSourceTransitionSink();

        var gpu =
            new RecordingGuardianGpuSourceTransitionSink();

        var factory =
            new ManualListenerFactory();

        using var runtime =
            new GuardianPerformancePowerSourceRuntime(
                reader,
                cpu,
                gpu,
                factory);

        var before =
            runtime.Snapshot;

        Require(
            !before.Active &&
            before.ListenerRegistrations == 0 &&
            cpu.Calls == 0 &&
            gpu.Calls == 0,
            "startup without explicit session must own no listener or dispatch authority");

        runtime.Start(
            cpuEnabled: true,
            gpuEnabled: true);

        var primed =
            runtime.Snapshot;

        Require(
            primed.Active &&
            primed.ListenerRegistrations == 1 &&
            primed.LastSource ==
                PerformancePowerSourceKind.Ac &&
            cpu.Calls == 0 &&
            gpu.Calls == 0,
            "explicit Start must prime AC before listener and perform zero startup dispatch");

        factory.Signal();

        var duplicate =
            runtime.Snapshot;

        Require(
            duplicate.DuplicateSignals == 2 &&
            duplicate.CpuDispatchAttempts == 0 &&
            duplicate.GpuDispatchAttempts == 0 &&
            cpu.Calls == 0 &&
            gpu.Calls == 0,
            "initial same-source notification must remain a zero-dispatch duplicate");

        factory.Signal();

        Require(
            cpu.Calls == 1 &&
            gpu.Calls == 1 &&
            cpu.LastSource ==
                PerformancePowerSourceKind.Battery &&
            gpu.LastSource ==
                PerformancePowerSourceKind.Battery,
            "AC to Battery must dispatch exactly once to both recording domains");

        factory.Signal();

        Require(
            cpu.Calls == 2 &&
            gpu.Calls == 2 &&
            cpu.LastSource ==
                PerformancePowerSourceKind.Ac &&
            gpu.LastSource ==
                PerformancePowerSourceKind.Ac,
            "Battery to AC must dispatch exactly once to both recording domains");

        runtime.Stop(
            "SELF_TEST");

        var stopped =
            runtime.Snapshot;

        Require(
            !stopped.Active &&
            stopped.StopCalls == 1 &&
            factory.DisposeCalls == 1,
            "runtime Stop must unregister its listener exactly once");

        output.WriteLine(
            "PASS Guardian source runtime: explicit enable prime -> same-source duplicate -> AC/Battery/AC recording dispatch -> listener stop");
    }

    private static void UnknownQueryFailureIsBounded(
        TextWriter output)
    {
        var reader =
            new QueuePowerSourceReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                QueryFailure(
                    "synthetic query failure 1"),
                QueryFailure(
                    "synthetic query failure 2"));

        var cpu =
            new RecordingGuardianCpuSourceTransitionSink();

        var gpu =
            new RecordingGuardianGpuSourceTransitionSink();

        var factory =
            new ManualListenerFactory();

        using var runtime =
            new GuardianPerformancePowerSourceRuntime(
                reader,
                cpu,
                gpu,
                factory);

        runtime.Start(
            cpuEnabled: true,
            gpuEnabled: true);

        factory.Signal();
        factory.Signal();

        var snapshot =
            runtime.Snapshot;

        Require(
            cpu.Calls == 1 &&
            gpu.Calls == 1 &&
            cpu.LastSource ==
                PerformancePowerSourceKind.Unknown &&
            gpu.LastSource ==
                PerformancePowerSourceKind.Unknown &&
            snapshot.DuplicateSignals == 2,
            "query failure must dispatch Unknown once and suppress repeated Unknown");

        output.WriteLine(
            "PASS Guardian source runtime: query failure dispatches one fail-closed Unknown episode");
    }

    private static void CpuFailureDoesNotBlockGpu(
        TextWriter output)
    {
        var reader =
            new QueuePowerSourceReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0));

        var cpu =
            new RecordingGuardianCpuSourceTransitionSink
            {
                ThrowOnCall =
                    true
            };

        var gpu =
            new RecordingGuardianGpuSourceTransitionSink();

        var factory =
            new ManualListenerFactory();

        using var runtime =
            new GuardianPerformancePowerSourceRuntime(
                reader,
                cpu,
                gpu,
                factory);

        runtime.Start(
            cpuEnabled: true,
            gpuEnabled: true);

        factory.Signal();

        var snapshot =
            runtime.Snapshot;

        Require(
            cpu.Calls == 1 &&
            gpu.Calls == 1 &&
            snapshot.CpuDispatchAttempts == 1 &&
            snapshot.GpuDispatchAttempts == 1 &&
            snapshot.Failure is not null,
            "CPU recording failure must not block GPU recording dispatch");

        output.WriteLine(
            "PASS Guardian source runtime: CPU recording failure does not block GPU");
    }

    private static void GpuFailureDoesNotRevertCpu(
        TextWriter output)
    {
        var reader =
            new QueuePowerSourceReader(
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1));

        var cpu =
            new RecordingGuardianCpuSourceTransitionSink();

        var gpu =
            new RecordingGuardianGpuSourceTransitionSink
            {
                ThrowOnCall =
                    true
            };

        var factory =
            new ManualListenerFactory();

        using var runtime =
            new GuardianPerformancePowerSourceRuntime(
                reader,
                cpu,
                gpu,
                factory);

        runtime.Start(
            cpuEnabled: true,
            gpuEnabled: true);

        factory.Signal();

        Require(
            cpu.Calls == 1 &&
            gpu.Calls == 1 &&
            cpu.LastSource ==
                PerformancePowerSourceKind.Ac &&
            runtime.Snapshot.Failure is not null,
            "GPU recording failure must not revert successful CPU recording dispatch");

        output.WriteLine(
            "PASS Guardian source runtime: GPU recording failure does not revert CPU");
    }

    private static void SelectedDomainsAreHonored(
        TextWriter output)
    {
        var reader =
            new QueuePowerSourceReader(
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Ac,
                    1),
                Observation(
                    PerformancePowerSourceKind.Battery,
                    0));

        var cpu =
            new RecordingGuardianCpuSourceTransitionSink();

        var gpu =
            new RecordingGuardianGpuSourceTransitionSink();

        var factory =
            new ManualListenerFactory();

        using var runtime =
            new GuardianPerformancePowerSourceRuntime(
                reader,
                cpu,
                gpu,
                factory);

        runtime.Start(
            cpuEnabled: true,
            gpuEnabled: false);

        factory.Signal();

        var snapshot =
            runtime.Snapshot;

        Require(
            cpu.Calls == 1 &&
            gpu.Calls == 0 &&
            snapshot.CpuDispatchAttempts == 1 &&
            snapshot.GpuDispatchAttempts == 0,
            "explicit CPU-only session must not invoke GPU source sink");

        output.WriteLine(
            "PASS Guardian source runtime: explicit domain selection is preserved across source dispatch");
    }

    private static PerformancePowerSourceObservation Observation(
        PerformancePowerSourceKind source,
        byte raw) =>
        new(
            Succeeded: true,
            Source:
                source,
            RawAcLineStatus:
                raw,
            BatteryPercent:
                50,
            BatteryFlags:
                1,
            Status:
                "SYNTHETIC_CONFIRMED_SOURCE");

    private static PerformancePowerSourceObservation QueryFailure(
        string reason) =>
        new(
            Succeeded: false,
            Source:
                PerformancePowerSourceKind.Unknown,
            RawAcLineStatus: null,
            BatteryPercent: null,
            BatteryFlags: null,
            Status:
                reason);

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(
                label);
    }

    private sealed class QueuePowerSourceReader :
        IPerformancePowerSourceReader
    {
        private readonly Queue<PerformancePowerSourceObservation>
            _observations;

        internal QueuePowerSourceReader(
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
                    "No synthetic Guardian source observation remains.");
            }

            return _observations.Dequeue();
        }
    }

    private sealed class ManualListenerFactory :
        IGuardianPowerSourceNotificationListenerFactory
    {
        private ManualListener? _listener;

        internal int DisposeCalls { get; private set; }

        public IDisposable Create(
            Action onSignal)
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException(
                    "Synthetic listener already exists.");
            }

            _listener =
                new ManualListener(
                    onSignal,
                    () =>
                    {
                        DisposeCalls++;
                        _listener =
                            null;
                    });

            return _listener;
        }

        internal void Signal()
        {
            (_listener ??
             throw new InvalidOperationException(
                 "Synthetic listener is not registered."))
                .Signal();
        }

        private sealed class ManualListener :
            IDisposable
        {
            private readonly Action _onSignal;
            private readonly Action _onDispose;
            private bool _disposed;

            internal ManualListener(
                Action onSignal,
                Action onDispose)
            {
                _onSignal =
                    onSignal;

                _onDispose =
                    onDispose;
            }

            internal void Signal()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(ManualListener));
                }

                _onSignal();
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed =
                    true;

                _onDispose();
            }
        }
    }
}
