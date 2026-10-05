using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal enum GuardianModernStandbySignalKind
{
    SessionDisplayOff = 1,
    SessionDisplayOn = 2,
    Suspend = 3,
    ResumeAutomatic = 4,
    ResumeSuspend = 5,
    ResumeCritical = 6
}

internal readonly record struct GuardianModernStandbySignal(
    GuardianModernStandbySignalKind Kind,
    DateTimeOffset CapturedAtUtc);

internal readonly record struct GuardianModernStandbyLifecycleSnapshot(
    bool Active,
    bool Suspended,
    bool PrimaryDisplayReleaseCompleted,
    int ListenerRegistrations,
    int DisplayOffSignals,
    int DisplayOnSignals,
    int SuspendSignals,
    int ResumeAutomaticSignals,
    int ResumeSuspendSignals,
    int ResumeCriticalSignals,
    int MaintenanceResumeSignalsSuppressed,
    int SuspendReleaseAttempts,
    int SuspendReleaseSuccesses,
    int SuspendFallbackReleaseAttempts,
    int ResumeReacquireAttempts,
    int ResumeReacquireSuccesses,
    string? LastBoundary,
    string? LastStatus,
    string? Failure);

internal readonly record struct GuardianModernStandbyEvidence(
    string Boundary,
    DateTimeOffset CapturedAtUtc,
    GuardianModernStandbyLifecycleSnapshot Lifecycle,
    GuardianDomainLifecycleSnapshot Domains,
    GuardianPowerSourceRuntimeSnapshot Source);

internal interface IGuardianModernStandbyNotificationListenerFactory
{
    IDisposable Create(
        Action<GuardianModernStandbySignal> onSignal);
}

/// <summary>
/// Display-aware Modern Standby wrapper for the already-qualified detached
/// Performance Guardian source/domain stack.
///
/// Exact-target policy follows the physically qualified HP 8C40 M6 behavior:
/// SESSION_DISPLAY_STATUS Off is the proactive release boundary;
/// PBT_APMSUSPEND is a safety-only fallback; resume broadcasts while the
/// session display remains Off never reacquire CPU/GPU authority; only
/// SESSION_DISPLAY_STATUS On can re-prime the current AC/DC source and
/// re-enable the selected domains.
///
/// The wrapper does not change CPU/GPU ownership semantics. Suspend release
/// uses the same normal domain ReleaseAsync path as client disable, therefore
/// CPU performs its conditional owned-field restore and GPU performs a Reset
/// only while the live session is still known.
/// </summary>
internal sealed class GuardianModernStandbyLifecycleRuntime :
    IGuardianPowerSourceRuntime
{
    private readonly object _sync =
        new();

    private readonly IGuardianPowerSourceRuntime _inner;
    private readonly IGuardianDomainLifecycle _domains;
    private readonly IGuardianModernStandbyNotificationListenerFactory
        _listenerFactory;
    private readonly Action<GuardianModernStandbyEvidence>? _evidenceSink;

    private IDisposable? _standbyListener;
    private bool _primed;
    private bool _active;
    private bool _suspended;
    private bool _primaryDisplayReleaseCompleted;
    private bool _suspendReleaseSucceeded;
    private bool _cpuEnabled;
    private bool _gpuEnabled;
    private int _listenerRegistrations;
    private int _displayOffSignals;
    private int _displayOnSignals;
    private int _suspendSignals;
    private int _resumeAutomaticSignals;
    private int _resumeSuspendSignals;
    private int _resumeCriticalSignals;
    private int _maintenanceResumeSignalsSuppressed;
    private int _suspendReleaseAttempts;
    private int _suspendReleaseSuccesses;
    private int _suspendFallbackReleaseAttempts;
    private int _resumeReacquireAttempts;
    private int _resumeReacquireSuccesses;
    private string? _lastBoundary;
    private string? _lastStatus;
    private string? _failure;
    private bool _disposed;

    internal GuardianModernStandbyLifecycleRuntime(
        IGuardianPowerSourceRuntime inner,
        IGuardianDomainLifecycle domains,
        IGuardianModernStandbyNotificationListenerFactory listenerFactory,
        Action<GuardianModernStandbyEvidence>? evidenceSink = null)
    {
        _inner =
            inner ??
            throw new ArgumentNullException(
                nameof(inner));

        _domains =
            domains ??
            throw new ArgumentNullException(
                nameof(domains));

        _listenerFactory =
            listenerFactory ??
            throw new ArgumentNullException(
                nameof(listenerFactory));

        _evidenceSink =
            evidenceSink;
    }

    public GuardianPowerSourceRuntimeSnapshot Snapshot =>
        _inner.Snapshot;

    internal GuardianModernStandbyLifecycleSnapshot StandbySnapshot
    {
        get
        {
            lock (_sync)
            {
                return StandbySnapshotLocked();
            }
        }
    }

    public PerformanceSourceDispatchResult Prime(
        bool cpuEnabled,
        bool gpuEnabled)
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (_primed ||
                _active ||
                _standbyListener is not null)
            {
                throw new InvalidOperationException(
                    "Modern Standby Guardian wrapper is already primed/active.");
            }

            var prime =
                _inner.Prime(
                    cpuEnabled,
                    gpuEnabled);

            _cpuEnabled =
                cpuEnabled;

            _gpuEnabled =
                gpuEnabled;

            _primed =
                true;

            _suspended =
                false;

            _primaryDisplayReleaseCompleted =
                false;

            _suspendReleaseSucceeded =
                false;

            _lastStatus =
                "PERFORMANCE_GUARDIAN_STANDBY_PRIMED";

            _failure =
                null;

            return prime;
        }
    }

    public void ActivateListener()
    {
        ThrowIfDisposed();

        IDisposable? created =
            null;

        Exception? failure =
            null;

        lock (_sync)
        {
            if (!_primed ||
                _active ||
                _standbyListener is not null)
            {
                throw new InvalidOperationException(
                    "Modern Standby Guardian wrapper must be primed exactly once before activation.");
            }

            // Hold the wrapper fence while the native listener becomes ready.
            // Immediate Windows notifications block on this same fence until
            // both listeners are fully active.
            _active =
                true;

            try
            {
                created =
                    _listenerFactory.Create(
                        HandleSignal);

                _standbyListener =
                    created;

                _listenerRegistrations++;

                _inner.ActivateListener();

                _lastStatus =
                    "PERFORMANCE_GUARDIAN_STANDBY_LISTENER_ACTIVE";
            }
            catch (Exception ex)
            {
                _active =
                    false;

                _standbyListener =
                    null;

                _failure =
                    ex.ToString();

                _lastStatus =
                    "PERFORMANCE_GUARDIAN_STANDBY_LISTENER_START_FAILED";

                failure =
                    ex;
            }
        }

        if (failure is not null)
        {
            try
            {
                created?.Dispose();
            }
            catch
            {
            }

            try
            {
                _inner.Stop(
                    "STANDBY_LISTENER_START_ROLLBACK");
            }
            catch
            {
            }

            throw failure;
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
                "Modern Standby Guardian stop requires a reason.",
                nameof(reason));
        }

        IDisposable? listener;

        lock (_sync)
        {
            if (!_primed &&
                !_active &&
                _standbyListener is null)
            {
                _inner.Stop(
                    reason);

                return;
            }

            _active =
                false;

            _primed =
                false;

            listener =
                _standbyListener;

            _standbyListener =
                null;

            _lastBoundary =
                "STOP";

            _lastStatus =
                "PERFORMANCE_GUARDIAN_STANDBY_RUNTIME_STOPPED";
        }

        listener?.Dispose();

        _inner.Stop(
            reason);
    }

    private void HandleSignal(
        GuardianModernStandbySignal signal)
    {
        GuardianModernStandbyEvidence? evidence =
            null;

        lock (_sync)
        {
            if (!_active)
                return;

            switch (signal.Kind)
            {
                case GuardianModernStandbySignalKind.SessionDisplayOff:
                    _displayOffSignals++;

                    if (!_suspended)
                    {
                        evidence =
                            ReleaseForSuspendLocked(
                                signal.CapturedAtUtc,
                                primaryDisplayBoundary:
                                    true,
                                fallback:
                                    false);
                    }

                    break;

                case GuardianModernStandbySignalKind.Suspend:
                    _suspendSignals++;

                    if (!_suspended)
                    {
                        evidence =
                            ReleaseForSuspendLocked(
                                signal.CapturedAtUtc,
                                primaryDisplayBoundary:
                                    false,
                                fallback:
                                    true);
                    }
                    else
                    {
                        _lastBoundary =
                            "PBT_APMSUSPEND";

                        _lastStatus =
                            "PERFORMANCE_GUARDIAN_SUSPEND_ALREADY_RELEASED";
                    }

                    break;

                case GuardianModernStandbySignalKind.ResumeAutomatic:
                    _resumeAutomaticSignals++;
                    SuppressMaintenanceResumeLocked(
                        "PBT_APMRESUMEAUTOMATIC");
                    break;

                case GuardianModernStandbySignalKind.ResumeSuspend:
                    _resumeSuspendSignals++;
                    SuppressMaintenanceResumeLocked(
                        "PBT_APMRESUMESUSPEND");
                    break;

                case GuardianModernStandbySignalKind.ResumeCritical:
                    _resumeCriticalSignals++;
                    SuppressMaintenanceResumeLocked(
                        "PBT_APMRESUMECRITICAL");
                    break;

                case GuardianModernStandbySignalKind.SessionDisplayOn:
                    _displayOnSignals++;

                    if (_suspended)
                    {
                        evidence =
                            ReacquireAfterDisplayOnLocked(
                                signal.CapturedAtUtc);
                    }

                    break;
            }
        }

        if (evidence.HasValue)
        {
            try
            {
                _evidenceSink?.Invoke(
                    evidence.Value);
            }
            catch
            {
                // Qualification evidence persistence must never unwind through
                // the Windows power-broadcast callback.
            }
        }
    }

    private GuardianModernStandbyEvidence ReleaseForSuspendLocked(
        DateTimeOffset capturedAtUtc,
        bool primaryDisplayBoundary,
        bool fallback)
    {
        _suspended =
            true;

        _suspendReleaseAttempts++;

        if (fallback)
            _suspendFallbackReleaseAttempts++;

        _lastBoundary =
            primaryDisplayBoundary
                ? "SESSION_DISPLAY_STATUS_OFF"
                : "PBT_APMSUSPEND_FALLBACK";

        Exception? failure =
            null;

        try
        {
            _inner.Stop(
                primaryDisplayBoundary
                    ? "MODERN_STANDBY_DISPLAY_OFF"
                    : "MODERN_STANDBY_SUSPEND_FALLBACK");
        }
        catch (Exception ex)
        {
            failure =
                ex;
        }

        try
        {
            _domains.ReleaseAsync(
                    _cpuEnabled,
                    _gpuEnabled,
                    primaryDisplayBoundary
                        ? "MODERN_STANDBY_DISPLAY_OFF"
                        : "MODERN_STANDBY_SUSPEND_FALLBACK",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            failure =
                failure is null
                    ? ex
                    : new AggregateException(
                        failure,
                        ex);
        }

        _primaryDisplayReleaseCompleted |=
            primaryDisplayBoundary &&
            failure is null;

        _suspendReleaseSucceeded =
            failure is null;

        if (failure is null)
        {
            _suspendReleaseSuccesses++;

            _lastStatus =
                primaryDisplayBoundary
                    ? "PERFORMANCE_GUARDIAN_DISPLAY_OFF_RELEASED"
                    : "PERFORMANCE_GUARDIAN_SUSPEND_FALLBACK_RELEASED";
        }
        else
        {
            _failure =
                failure.ToString();

            _lastStatus =
                "PERFORMANCE_GUARDIAN_SUSPEND_RELEASE_FAILED";
        }

        return EvidenceLocked(
            _lastBoundary!,
            capturedAtUtc);
    }

    private GuardianModernStandbyEvidence ReacquireAfterDisplayOnLocked(
        DateTimeOffset capturedAtUtc)
    {
        _lastBoundary =
            "SESSION_DISPLAY_STATUS_ON";

        _resumeReacquireAttempts++;

        if (!_suspendReleaseSucceeded)
        {
            _lastStatus =
                "PERFORMANCE_GUARDIAN_RESUME_BLOCKED_AFTER_RELEASE_FAILURE";

            return EvidenceLocked(
                _lastBoundary,
                capturedAtUtc);
        }

        var domainsEnabled =
            false;

        var sourcePrimed =
            false;

        try
        {
            var prime =
                _inner.Prime(
                    _cpuEnabled,
                    _gpuEnabled);

            sourcePrimed =
                true;

            _domains.EnableAsync(
                    _cpuEnabled,
                    _gpuEnabled,
                    prime.Observation.Source,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            domainsEnabled =
                true;

            _inner.ActivateListener();

            _suspended =
                false;

            _suspendReleaseSucceeded =
                false;

            _resumeReacquireSuccesses++;

            _lastStatus =
                "PERFORMANCE_GUARDIAN_DISPLAY_ON_REACQUIRED";

            return EvidenceLocked(
                _lastBoundary,
                capturedAtUtc);
        }
        catch (Exception ex)
        {
            Exception? cleanupFailure =
                null;

            try
            {
                if (sourcePrimed)
                {
                    _inner.Stop(
                        "MODERN_STANDBY_RESUME_ROLLBACK");
                }
            }
            catch (Exception cleanup)
            {
                cleanupFailure =
                    cleanup;
            }

            if (domainsEnabled)
            {
                try
                {
                    _domains.ReleaseAsync(
                            _cpuEnabled,
                            _gpuEnabled,
                            "MODERN_STANDBY_RESUME_ROLLBACK",
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception cleanup)
                {
                    cleanupFailure =
                        cleanupFailure is null
                            ? cleanup
                            : new AggregateException(
                                cleanupFailure,
                                cleanup);
                }
            }

            _failure =
                cleanupFailure is null
                    ? ex.ToString()
                    : new AggregateException(
                        ex,
                        cleanupFailure).ToString();

            _lastStatus =
                "PERFORMANCE_GUARDIAN_DISPLAY_ON_REACQUIRE_FAILED";

            return EvidenceLocked(
                _lastBoundary,
                capturedAtUtc);
        }
    }

    private void SuppressMaintenanceResumeLocked(
        string boundary)
    {
        _lastBoundary =
            boundary;

        if (_suspended)
        {
            _maintenanceResumeSignalsSuppressed++;

            _lastStatus =
                "PERFORMANCE_GUARDIAN_MAINTENANCE_RESUME_SUPPRESSED";
        }
        else
        {
            _lastStatus =
                "PERFORMANCE_GUARDIAN_RESUME_SIGNAL_WHILE_AWAKE";
        }
    }

    private GuardianModernStandbyEvidence EvidenceLocked(
        string boundary,
        DateTimeOffset capturedAtUtc) =>
        new(
            Boundary:
                boundary,
            CapturedAtUtc:
                capturedAtUtc,
            Lifecycle:
                StandbySnapshotLocked(),
            Domains:
                _domains.Snapshot,
            Source:
                _inner.Snapshot);

    private GuardianModernStandbyLifecycleSnapshot StandbySnapshotLocked() =>
        new(
            Active:
                _active,
            Suspended:
                _suspended,
            PrimaryDisplayReleaseCompleted:
                _primaryDisplayReleaseCompleted,
            ListenerRegistrations:
                _listenerRegistrations,
            DisplayOffSignals:
                _displayOffSignals,
            DisplayOnSignals:
                _displayOnSignals,
            SuspendSignals:
                _suspendSignals,
            ResumeAutomaticSignals:
                _resumeAutomaticSignals,
            ResumeSuspendSignals:
                _resumeSuspendSignals,
            ResumeCriticalSignals:
                _resumeCriticalSignals,
            MaintenanceResumeSignalsSuppressed:
                _maintenanceResumeSignalsSuppressed,
            SuspendReleaseAttempts:
                _suspendReleaseAttempts,
            SuspendReleaseSuccesses:
                _suspendReleaseSuccesses,
            SuspendFallbackReleaseAttempts:
                _suspendFallbackReleaseAttempts,
            ResumeReacquireAttempts:
                _resumeReacquireAttempts,
            ResumeReacquireSuccesses:
                _resumeReacquireSuccesses,
            LastBoundary:
                _lastBoundary,
            LastStatus:
                _lastStatus,
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

        try
        {
            Stop(
                "STANDBY_RUNTIME_DISPOSE");
        }
        catch
        {
            try
            {
                _inner.Dispose();
            }
            catch
            {
            }

            _disposed =
                true;

            throw;
        }

        _inner.Dispose();

        _disposed =
            true;
    }
}

internal sealed class WindowsGuardianModernStandbyNotificationListenerFactory :
    IGuardianModernStandbyNotificationListenerFactory
{
    public IDisposable Create(
        Action<GuardianModernStandbySignal> onSignal) =>
        new WindowsGuardianModernStandbyNotificationListener(
            onSignal);
}

internal sealed class WindowsGuardianModernStandbyNotificationListener :
    IDisposable
{
    private const int StartupTimeoutMilliseconds =
        5000;

    private const int StopJoinTimeoutMilliseconds =
        5000;

    private readonly Action<GuardianModernStandbySignal> _onSignal;
    private readonly ManualResetEventSlim _ready =
        new(initialState: false);
    private readonly Thread _thread;

    private Exception? _startupFailure;
    private IntPtr _windowHandle;
    private bool _disposed;

    internal WindowsGuardianModernStandbyNotificationListener(
        Action<GuardianModernStandbySignal> onSignal)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Modern Standby Guardian listener requires Windows.");
        }

        _onSignal =
            onSignal ??
            throw new ArgumentNullException(
                nameof(onSignal));

        _thread =
            new Thread(
                ThreadMain)
            {
                IsBackground = true,
                Name =
                    "VictusFanControl.PerformanceGuardian.ModernStandby"
            };

        _thread.SetApartmentState(
            ApartmentState.STA);

        _thread.Start();

        if (!_ready.Wait(
                StartupTimeoutMilliseconds))
        {
            Dispose();

            throw new TimeoutException(
                "Modern Standby Guardian listener did not become ready.");
        }

        if (_startupFailure is not null)
        {
            var failure =
                _startupFailure;

            Dispose();

            throw new InvalidOperationException(
                "Modern Standby Guardian listener failed during startup.",
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
                new NotificationWindow(
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

        if (handle != IntPtr.Zero)
        {
            _ =
                NativeMethods.PostMessage(
                    handle,
                    NotificationWindow.WmGuardianStop,
                    IntPtr.Zero,
                    IntPtr.Zero);
        }

        if (_thread.IsAlive &&
            !_thread.Join(
                StopJoinTimeoutMilliseconds))
        {
            throw new TimeoutException(
                "Modern Standby Guardian listener thread did not stop.");
        }

        _ready.Dispose();
    }

    private sealed class NotificationWindow :
        NativeWindow,
        IDisposable
    {
        internal const int WmGuardianStop =
            0x8002;

        private const int WmPowerBroadcast =
            0x0218;

        private const int PbtApmSuspend =
            0x0004;

        private const int PbtApmResumeCritical =
            0x0006;

        private const int PbtApmResumeSuspend =
            0x0007;

        private const int PbtApmResumeAutomatic =
            0x0012;

        private const int PbtPowerSettingChange =
            0x8013;

        private const uint DeviceNotifyWindowHandle =
            0;

        private static readonly Guid GuidSessionDisplayStatus =
            new(
                "2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

        private readonly Action<GuardianModernStandbySignal> _onSignal;
        private readonly ApplicationContext _context;

        private IntPtr _suspendResumeHandle;
        private IntPtr _displayStatusHandle;
        private bool _disposed;

        internal NotificationWindow(
            Action<GuardianModernStandbySignal> onSignal,
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
                        "VictusFanControl.PerformanceGuardian.ModernStandbyWindow"
                });

            _suspendResumeHandle =
                NativeMethods.RegisterSuspendResumeNotification(
                    Handle,
                    DeviceNotifyWindowHandle);

            if (_suspendResumeHandle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "RegisterSuspendResumeNotification failed.");
            }

            var setting =
                GuidSessionDisplayStatus;

            _displayStatusHandle =
                NativeMethods.RegisterPowerSettingNotification(
                    Handle,
                    ref setting,
                    DeviceNotifyWindowHandle);

            if (_displayStatusHandle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "RegisterPowerSettingNotification(GUID_SESSION_DISPLAY_STATUS) failed.");
            }
        }

        protected override void WndProc(
            ref Message m)
        {
            if (m.Msg ==
                WmGuardianStop)
            {
                _context.ExitThread();
                return;
            }

            if (m.Msg ==
                WmPowerBroadcast)
            {
                HandlePowerBroadcast(
                    unchecked(
                        (int)m.WParam.ToInt64()),
                    m.LParam);
            }

            base.WndProc(
                ref m);
        }

        private void HandlePowerBroadcast(
            int code,
            IntPtr data)
        {
            var now =
                DateTimeOffset.UtcNow;

            switch (code)
            {
                case PbtApmSuspend:
                    _onSignal(
                        new GuardianModernStandbySignal(
                            GuardianModernStandbySignalKind.Suspend,
                            now));
                    return;

                case PbtApmResumeAutomatic:
                    _onSignal(
                        new GuardianModernStandbySignal(
                            GuardianModernStandbySignalKind.ResumeAutomatic,
                            now));
                    return;

                case PbtApmResumeSuspend:
                    _onSignal(
                        new GuardianModernStandbySignal(
                            GuardianModernStandbySignalKind.ResumeSuspend,
                            now));
                    return;

                case PbtApmResumeCritical:
                    _onSignal(
                        new GuardianModernStandbySignal(
                            GuardianModernStandbySignalKind.ResumeCritical,
                            now));
                    return;

                case PbtPowerSettingChange:
                    HandlePowerSettingChange(
                        data,
                        now);
                    return;
            }
        }

        private void HandlePowerSettingChange(
            IntPtr data,
            DateTimeOffset now)
        {
            if (data == IntPtr.Zero)
                return;

            var header =
                Marshal.PtrToStructure<PowerBroadcastSetting>(
                    data);

            if (header.PowerSetting !=
                    GuidSessionDisplayStatus ||
                header.DataLength <
                    sizeof(uint))
            {
                return;
            }

            var valueAddress =
                IntPtr.Add(
                    data,
                    Marshal.SizeOf<PowerBroadcastSetting>());

            var value =
                unchecked(
                    (uint)Marshal.ReadInt32(
                        valueAddress));

            if (value == 0)
            {
                _onSignal(
                    new GuardianModernStandbySignal(
                        GuardianModernStandbySignalKind.SessionDisplayOff,
                        now));
            }
            else if (value == 1)
            {
                _onSignal(
                    new GuardianModernStandbySignal(
                        GuardianModernStandbySignalKind.SessionDisplayOn,
                        now));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed =
                true;

            if (_displayStatusHandle !=
                IntPtr.Zero)
            {
                _ =
                    NativeMethods.UnregisterPowerSettingNotification(
                        _displayStatusHandle);

                _displayStatusHandle =
                    IntPtr.Zero;
            }

            if (_suspendResumeHandle !=
                IntPtr.Zero)
            {
                _ =
                    NativeMethods.UnregisterSuspendResumeNotification(
                        _suspendResumeHandle);

                _suspendResumeHandle =
                    IntPtr.Zero;
            }

            DestroyHandle();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public uint DataLength;
        }
    }

    private static class NativeMethods
    {
        [DllImport(
            "user32.dll",
            SetLastError = true)]
        internal static extern IntPtr RegisterSuspendResumeNotification(
            IntPtr hRecipient,
            uint flags);

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterSuspendResumeNotification(
            IntPtr handle);

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        internal static extern IntPtr RegisterPowerSettingNotification(
            IntPtr hRecipient,
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
            int msg,
            IntPtr wParam,
            IntPtr lParam);
    }
}

internal static class GuardianModernStandbyLifecycleRuntimeSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            DisplayOffMaintenanceWakeDisplayOn(
                output);

            SuspendFallbackReleasesOnce(
                output);

            output.WriteLine(
                "Performance Guardian Modern Standby lifecycle self-test: PASS (display-Off release, maintenance-wake suppression, display-On reacquire, suspend fallback; fake domains only).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance Guardian Modern Standby lifecycle self-test: FAIL - " +
                ex);

            return 1;
        }
    }

    private static void DisplayOffMaintenanceWakeDisplayOn(
        TextWriter output)
    {
        var inner =
            new FakeSourceRuntime();

        var domains =
            new FakeDomains();

        var listener =
            new FakeListenerFactory();

        using var runtime =
            new GuardianModernStandbyLifecycleRuntime(
                inner,
                domains,
                listener);

        var prime =
            runtime.Prime(
                cpuEnabled: true,
                gpuEnabled: true);

        domains.EnableAsync(
                cpuEnabled: true,
                gpuEnabled: true,
                prime.Observation.Source,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        runtime.ActivateListener();

        listener.Emit(
            GuardianModernStandbySignalKind.SessionDisplayOff);

        Require(
            domains.ReleaseCalls == 1 &&
            inner.StopCalls == 1 &&
            runtime.StandbySnapshot.Suspended &&
            runtime.StandbySnapshot.PrimaryDisplayReleaseCompleted,
            "display Off must release both domains and stop AC/DC runtime exactly once");

        listener.Emit(
            GuardianModernStandbySignalKind.ResumeAutomatic);

        listener.Emit(
            GuardianModernStandbySignalKind.ResumeSuspend);

        Require(
            domains.EnableCalls == 1 &&
            runtime.StandbySnapshot.MaintenanceResumeSignalsSuppressed == 2,
            "maintenance resume broadcasts while display Off must not reacquire");

        listener.Emit(
            GuardianModernStandbySignalKind.SessionDisplayOn);

        Require(
            domains.EnableCalls == 2 &&
            inner.PrimeCalls == 2 &&
            inner.ActivateCalls == 2 &&
            runtime.StandbySnapshot.ResumeReacquireSuccesses == 1 &&
            !runtime.StandbySnapshot.Suspended,
            "display On must fresh-prime and reacquire exactly once");

        runtime.Stop(
            "SELF_TEST");

        domains.ReleaseAsync(
                true,
                true,
                "SELF_TEST",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Require(
            domains.ReleaseCalls == 2 &&
            inner.StopCalls == 2,
            "final client cleanup remains independent after resume reacquire");

        output.WriteLine(
            "PASS standby display Off -> suppressed maintenance wake -> display On reacquire");
    }

    private static void SuspendFallbackReleasesOnce(
        TextWriter output)
    {
        var inner =
            new FakeSourceRuntime();

        var domains =
            new FakeDomains();

        var listener =
            new FakeListenerFactory();

        using var runtime =
            new GuardianModernStandbyLifecycleRuntime(
                inner,
                domains,
                listener);

        var prime =
            runtime.Prime(
                true,
                true);

        domains.EnableAsync(
                true,
                true,
                prime.Observation.Source,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        runtime.ActivateListener();

        listener.Emit(
            GuardianModernStandbySignalKind.Suspend);

        listener.Emit(
            GuardianModernStandbySignalKind.Suspend);

        Require(
            domains.ReleaseCalls == 1 &&
            runtime.StandbySnapshot.SuspendFallbackReleaseAttempts == 1 &&
            runtime.StandbySnapshot.SuspendReleaseSuccesses == 1,
            "PBT_APMSUSPEND fallback releases at most once when display Off was not seen");

        output.WriteLine(
            "PASS standby PBT_APMSUSPEND safety fallback is single-shot");
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

    private sealed class FakeSourceRuntime :
        IGuardianPowerSourceRuntime
    {
        internal int PrimeCalls;
        internal int ActivateCalls;
        internal int StopCalls;

        private bool _active;

        public GuardianPowerSourceRuntimeSnapshot Snapshot =>
            new(
                Active:
                    _active,
                CpuEnabled:
                    _active,
                GpuEnabled:
                    _active,
                StartCalls:
                    PrimeCalls,
                StopCalls:
                    StopCalls,
                ListenerRegistrations:
                    ActivateCalls,
                NotificationSignals:
                    0,
                ReconciliationSignals:
                    ActivateCalls,
                DuplicateSignals:
                    0,
                CpuDispatchAttempts:
                    0,
                GpuDispatchAttempts:
                    0,
                LastSource:
                    PerformancePowerSourceKind.Ac,
                LastStatus:
                    "FAKE_SOURCE",
                LastStopReason:
                    null,
                Failure:
                    null);

        public PerformanceSourceDispatchResult Prime(
            bool cpuEnabled,
            bool gpuEnabled)
        {
            PrimeCalls++;

            return new PerformanceSourceDispatchResult(
                Observation:
                    new PerformancePowerSourceObservation(
                        Succeeded: true,
                        Source:
                            PerformancePowerSourceKind.Ac,
                        RawAcLineStatus: 1,
                        BatteryPercent: 100,
                        BatteryFlags: 1,
                        Status:
                            "FAKE_AC"),
                PreviousSource:
                    PerformancePowerSourceKind.Unknown,
                Primed:
                    true,
                DuplicateSuppressed:
                    false,
                CpuAttempted:
                    false,
                CpuResult:
                    null,
                CpuException:
                    null,
                GpuAttempted:
                    false,
                GpuResult:
                    null,
                GpuException:
                    null,
                Succeeded:
                    true,
                Status:
                    "FAKE_PRIME");
        }

        public void ActivateListener()
        {
            ActivateCalls++;
            _active = true;
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
            if (!_active &&
                PrimeCalls == StopCalls)
            {
                return;
            }

            StopCalls++;
            _active = false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDomains :
        IGuardianDomainLifecycle
    {
        internal int EnableCalls;
        internal int ReleaseCalls;

        public GuardianDomainLifecycleSnapshot Snapshot =>
            new(
                EnableCalls:
                    EnableCalls,
                ReleaseCalls:
                    ReleaseCalls,
                LastCpuEnabled:
                    true,
                LastGpuEnabled:
                    true,
                LastInitialSource:
                    PerformancePowerSourceKind.Ac,
                LastReleaseReason:
                    ReleaseCalls > 0
                        ? "FAKE_RELEASE"
                        : null,
                CpuHardwareWriteAttempts:
                    EnableCalls + ReleaseCalls,
                GpuHardwareWriteAttempts:
                    EnableCalls + ReleaseCalls,
                CpuState:
                    ReleaseCalls >= EnableCalls
                        ? "Disabled"
                        : "Owned",
                GpuState:
                    ReleaseCalls >= EnableCalls
                        ? "Disabled"
                        : "ActiveUnverified",
                CpuStatus:
                    "FAKE_CPU",
                GpuStatus:
                    "FAKE_GPU");

        public ValueTask EnableAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            PerformancePowerSourceKind initialSource,
            CancellationToken cancellationToken)
        {
            EnableCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            string reason,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeListenerFactory :
        IGuardianModernStandbyNotificationListenerFactory
    {
        private Action<GuardianModernStandbySignal>? _callback;

        public IDisposable Create(
            Action<GuardianModernStandbySignal> onSignal)
        {
            _callback =
                onSignal;

            return new CallbackLease(
                () =>
                {
                    _callback =
                        null;
                });
        }

        internal void Emit(
            GuardianModernStandbySignalKind kind)
        {
            _callback?.Invoke(
                new GuardianModernStandbySignal(
                    kind,
                    DateTimeOffset.UtcNow));
        }

        private sealed class CallbackLease :
            IDisposable
        {
            private readonly Action _dispose;
            private bool _disposed;

            internal CallbackLease(
                Action dispose)
            {
                _dispose =
                    dispose;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed =
                    true;
                _dispose();
            }
        }
    }
}
