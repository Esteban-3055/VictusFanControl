using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace VictusFanControl.ModernStandbyProbe;

internal sealed class ModernStandbyProbeForm : Form
{
    private const uint DeviceNotifyWindowHandle = 0;
    private const int MaxEvents = 512;
    private const int ExitDelayMilliseconds = 10000;

    private readonly ProbeOptions _options;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly List<ProbeEvent> _events = new();
    private readonly List<IntPtr> _powerSettingRegistrations = new();
    private readonly System.Windows.Forms.Timer _timeoutTimer = new();
    private readonly System.Windows.Forms.Timer _exitDelayTimer = new();

    private IntPtr _suspendResumeRegistration;
    private int _sequence;
    private int _droppedEventCount;
    private bool _registrationSucceeded;
    private bool _sessionDisplayOffSeen;
    private bool _sessionDisplayOnAfterOffSeen;
    private bool _suspendSeen;
    private bool _resumeAutomaticSeen;
    private bool _resumeSuspendSeen;
    private bool _resumeCriticalSeen;
    private bool _reportWritten;
    private bool _registrationsReleased;
    private ProbeEvent? _displayOffEvent;
    private ProbeEvent? _displayOnEvent;
    private string _exitReason = "window closed";

    public ModernStandbyProbeForm(ProbeOptions options)
    {
        _options = options;
        _startedAtUtc = DateTimeOffset.UtcNow;

        Text = "VictusFanControl Modern Standby M0 Observer";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        Opacity = 0;
        WindowState = FormWindowState.Minimized;

        _timeoutTimer.Interval = 1000;
        _timeoutTimer.Tick += (_, _) =>
        {
            if ((DateTimeOffset.UtcNow - _startedAtUtc).TotalMinutes <
                _options.TimeoutMinutes)
            {
                return;
            }

            Record(
                "PROBE",
                "TIMEOUT",
                $"No completed session-display Off->On cycle within {_options.TimeoutMinutes} minute(s).");

            _exitReason = "timeout";
            ExitCode = 5;
            Close();
        };

        _exitDelayTimer.Interval = ExitDelayMilliseconds;
        _exitDelayTimer.Tick += (_, _) =>
        {
            _exitDelayTimer.Stop();

            Record(
                "PROBE",
                "POST_RESUME_CAPTURE_COMPLETE",
                $"Held the observer open for {ExitDelayMilliseconds} ms after the first session-display On following Off.");

            _exitReason = "session-display Off->On cycle completed";
            ExitCode = 0;
            Close();
        };
    }

    public int ExitCode { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            RegisterNativeNotifications();

            Record(
                "PROBE",
                "REGISTERED",
                "Read-only window observer registered for suspend/resume plus session-display, console-display, AC/DC and lid notifications.");

            RecordInitialPowerStatus();

            _registrationSucceeded = true;
            WriteReadyMarker();
            _timeoutTimer.Start();
        }
        catch (Exception ex)
        {
            Record(
                "PROBE",
                "REGISTRATION_FAILURE",
                $"{ex.GetType().Name}: {ex.Message}");

            _exitReason = "notification registration failed";
            ExitCode = 4;

            BeginInvoke(
                new Action(Close));
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Hide();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timeoutTimer.Stop();
        _exitDelayTimer.Stop();

        UnregisterNativeNotifications();
        WriteReportBestEffort();

        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timeoutTimer.Dispose();
            _exitDelayTimer.Dispose();
        }

        UnregisterNativeNotifications();
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativePowerEvents.WmPowerBroadcast)
        {
            HandlePowerBroadcast(
                unchecked((int)m.WParam.ToInt64()),
                m.LParam);
        }

        base.WndProc(ref m);
    }

    private void RegisterNativeNotifications()
    {
        _suspendResumeRegistration =
            RegisterSuspendResumeNotification(
                Handle,
                DeviceNotifyWindowHandle);

        if (_suspendResumeRegistration == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "RegisterSuspendResumeNotification failed.");
        }

        RegisterPowerSetting(
            NativePowerEvents.GuidSessionDisplayStatus);

        RegisterPowerSetting(
            NativePowerEvents.GuidConsoleDisplayState);

        RegisterPowerSetting(
            NativePowerEvents.GuidAcDcPowerSource);

        RegisterPowerSetting(
            NativePowerEvents.GuidLidSwitchStateChange);
    }

    private void RegisterPowerSetting(Guid setting)
    {
        var mutableSetting = setting;

        var registration =
            RegisterPowerSettingNotification(
                Handle,
                ref mutableSetting,
                DeviceNotifyWindowHandle);

        if (registration == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"RegisterPowerSettingNotification failed for {NativePowerEvents.DescribeSetting(setting)}.");
        }

        _powerSettingRegistrations.Add(registration);
    }

    private void UnregisterNativeNotifications()
    {
        if (_registrationsReleased)
        {
            return;
        }

        _registrationsReleased = true;

        foreach (var registration in
                 _powerSettingRegistrations)
        {
            if (registration != IntPtr.Zero)
            {
                _ =
                    UnregisterPowerSettingNotification(
                        registration);
            }
        }

        _powerSettingRegistrations.Clear();

        if (_suspendResumeRegistration != IntPtr.Zero)
        {
            _ =
                UnregisterSuspendResumeNotification(
                    _suspendResumeRegistration);

            _suspendResumeRegistration = IntPtr.Zero;
        }
    }

    private void HandlePowerBroadcast(
        int code,
        IntPtr data)
    {
        if (code == NativePowerEvents.PbtPowerSettingChange)
        {
            HandlePowerSettingChange(data);
            return;
        }

        var eventName =
            NativePowerEvents.DescribeBroadcast(code);

        Record(
            "WM_POWERBROADCAST",
            eventName,
            $"wParam=0x{code:X4}");

        switch (code)
        {
            case NativePowerEvents.PbtApmSuspend:
                _suspendSeen = true;
                break;

            case NativePowerEvents.PbtApmResumeAutomatic:
                _resumeAutomaticSeen = true;
                break;

            case NativePowerEvents.PbtApmResumeSuspend:
                _resumeSuspendSeen = true;
                break;

            case NativePowerEvents.PbtApmResumeCritical:
                _resumeCriticalSeen = true;
                break;
        }
    }

    private void HandlePowerSettingChange(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            Record(
                "POWER_SETTING",
                "MALFORMED",
                "PBT_POWERSETTINGCHANGE arrived with a null payload.");
            return;
        }

        try
        {
            var header =
                Marshal.PtrToStructure<PowerBroadcastSetting>(
                    data);

            uint? value = null;

            if (header.DataLength >= sizeof(uint))
            {
                var valueAddress =
                    IntPtr.Add(
                        data,
                        Marshal.SizeOf<PowerBroadcastSetting>());

                value =
                    unchecked(
                        (uint)Marshal.ReadInt32(
                            valueAddress));
            }

            var settingName =
                NativePowerEvents.DescribeSetting(
                    header.PowerSetting);

            var valueDetail =
                value.HasValue
                    ? DescribeSettingValue(
                        header.PowerSetting,
                        value.Value)
                    : "no DWORD value";

            var recorded =
                Record(
                    "POWER_SETTING",
                    settingName,
                    $"dataLength={header.DataLength}; value={valueDetail}");

            if (header.PowerSetting ==
                    NativePowerEvents.GuidSessionDisplayStatus &&
                value.HasValue)
            {
                HandleSessionDisplayState(
                    value.Value,
                    recorded);
            }
        }
        catch (Exception ex)
        {
            Record(
                "POWER_SETTING",
                "PARSE_FAILURE",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HandleSessionDisplayState(
        uint value,
        ProbeEvent recorded)
    {
        if (value == 0)
        {
            _sessionDisplayOffSeen = true;
            _displayOffEvent ??= recorded;
            return;
        }

        if (value != 1 ||
            !_sessionDisplayOffSeen ||
            _sessionDisplayOnAfterOffSeen)
        {
            return;
        }

        _sessionDisplayOnAfterOffSeen = true;
        _displayOnEvent = recorded;

        Record(
            "PROBE",
            "SESSION_DISPLAY_CYCLE_COMPLETE",
            "Observed GUID_SESSION_DISPLAY_STATUS Off followed by On. This is capture evidence only; it does not by itself prove DRIPS/Modern Standby Sleep.");

        if (_options.AutoExitAfterDisplayCycle)
        {
            _exitDelayTimer.Start();
        }
    }

    private static string DescribeSettingValue(
        Guid setting,
        uint value)
    {
        if (setting ==
                NativePowerEvents.GuidSessionDisplayStatus ||
            setting ==
                NativePowerEvents.GuidConsoleDisplayState)
        {
            return
                $"{value} ({NativePowerEvents.DescribeDisplayState(value)})";
        }

        if (setting ==
            NativePowerEvents.GuidAcDcPowerSource)
        {
            return
                $"{value} ({NativePowerEvents.DescribePowerSource(value)})";
        }

        if (setting ==
            NativePowerEvents.GuidLidSwitchStateChange)
        {
            return
                $"{value} ({NativePowerEvents.DescribeLidState(value)})";
        }

        return value.ToString();
    }

    private void RecordInitialPowerStatus()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            Record(
                "BASELINE",
                "SYSTEM_POWER_STATUS_UNAVAILABLE",
                $"GetSystemPowerStatus failed with Win32 error {Marshal.GetLastWin32Error()}.");
            return;
        }

        Record(
            "BASELINE",
            "SYSTEM_POWER_STATUS",
            $"ac={status.ACLineStatus}; batteryFlag=0x{status.BatteryFlag:X2}; batteryPercent={status.BatteryLifePercent}; systemStatusFlag={status.SystemStatusFlag}");
    }

    private ProbeEvent Record(
        string source,
        string eventName,
        string detail)
    {
        var clocks = ClockSample.Capture();

        var item =
            new ProbeEvent(
                Sequence: checked(++_sequence),
                Utc: clocks.Utc,
                UnbiasedMilliseconds:
                    clocks.UnbiasedMilliseconds,
                TickCountMilliseconds:
                    clocks.TickCountMilliseconds,
                QueryPerformanceCounterTicks:
                    clocks.QueryPerformanceCounterTicks,
                Source: source,
                Event: eventName,
                Detail: detail);

        if (_events.Count >= MaxEvents)
        {
            _events.RemoveAt(0);
            _droppedEventCount++;
        }

        _events.Add(item);
        return item;
    }

    private ProbeCycleTiming? BuildCycleTiming()
    {
        if (_displayOffEvent is null ||
            _displayOnEvent is null)
        {
            return null;
        }

        double? unbiasedElapsed = null;

        if (_displayOffEvent.UnbiasedMilliseconds.HasValue &&
            _displayOnEvent.UnbiasedMilliseconds.HasValue)
        {
            unbiasedElapsed =
                SignedDelta(
                    _displayOnEvent.UnbiasedMilliseconds.Value,
                    _displayOffEvent.UnbiasedMilliseconds.Value);
        }

        var tickElapsed =
            SignedDelta(
                _displayOnEvent.TickCountMilliseconds,
                _displayOffEvent.TickCountMilliseconds);

        var qpcElapsed =
            _displayOnEvent.QueryPerformanceCounterTicks -
            _displayOffEvent.QueryPerformanceCounterTicks;

        var qpcMilliseconds =
            qpcElapsed *
            1000.0 /
            Stopwatch.Frequency;

        return new ProbeCycleTiming(
            WallMilliseconds:
                (_displayOnEvent.Utc -
                 _displayOffEvent.Utc).TotalMilliseconds,
            UnbiasedMilliseconds:
                unbiasedElapsed,
            TickCountMilliseconds:
                tickElapsed,
            QueryPerformanceCounterTicks:
                qpcElapsed,
            QueryPerformanceCounterMilliseconds:
                qpcMilliseconds);
    }

    private static double SignedDelta(
        ulong after,
        ulong before) =>
        after >= before
            ? (double)(after - before)
            : -(double)(before - after);

    private void WriteReadyMarker()
    {
        var document =
            new ProbeReadyDocument(
                SchemaVersion: 1,
                Ready: true,
                ReadOnly: true,
                RegisteredAtUtc:
                    DateTimeOffset.UtcNow,
                ProcessId:
                    Environment.ProcessId,
                SessionId:
                    Process.GetCurrentProcess().SessionId,
                OutputPath:
                    _options.OutputPath,
                Detail:
                    "M0 observer is registered. It performs no PawnIO, EC, HP WMI, NVML, fan-control, watchdog-lease or sleep-inhibition operation.");

        WriteJsonAtomically(
            _options.ReadyPath,
            document);
    }

    private void WriteReportBestEffort()
    {
        if (_reportWritten)
        {
            return;
        }

        try
        {
            var report =
                new ProbeReport(
                    SchemaVersion: 1,
                    ProbeName:
                        "HP 8C40 Modern Standby M0 window observer",
                    ReadOnly: true,
                    StartedAtUtc:
                        _startedAtUtc,
                    CompletedAtUtc:
                        DateTimeOffset.UtcNow,
                    ProcessId:
                        Environment.ProcessId,
                    SessionId:
                        Process.GetCurrentProcess().SessionId,
                    ExitReason:
                        _exitReason,
                    RegistrationSucceeded:
                        _registrationSucceeded,
                    SessionDisplayOffSeen:
                        _sessionDisplayOffSeen,
                    SessionDisplayOnAfterOffSeen:
                        _sessionDisplayOnAfterOffSeen,
                    SuspendSeen:
                        _suspendSeen,
                    ResumeAutomaticSeen:
                        _resumeAutomaticSeen,
                    ResumeSuspendSeen:
                        _resumeSuspendSeen,
                    ResumeCriticalSeen:
                        _resumeCriticalSeen,
                    DroppedEventCount:
                        _droppedEventCount,
                    CycleTiming:
                        BuildCycleTiming(),
                    Events:
                        _events.ToArray());

            WriteJsonAtomically(
                _options.OutputPath,
                report);

            _reportWritten = true;

            Console.WriteLine(
                $"Modern Standby M0 observer report: {_options.OutputPath}");
        }
        catch (Exception ex)
        {
            ExitCode =
                ExitCode == 0
                    ? 7
                    : ExitCode;

            Console.Error.WriteLine(
                $"Could not persist Modern Standby M0 report: {ex}");
        }
    }

    private static void WriteJsonAtomically<T>(
        string path,
        T value)
    {
        var directory =
            Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                $"Path '{path}' has no parent directory.");

        Directory.CreateDirectory(directory);

        var temp =
            path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(
                    value,
                    ProbeJson.Options));

            File.Move(
                temp,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(
        IntPtr hRecipient,
        uint flags);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterSuspendResumeNotification(
        IntPtr handle);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient,
        ref Guid powerSettingGuid,
        uint flags);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(
        IntPtr handle);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(
        out SystemPowerStatus status);
}
