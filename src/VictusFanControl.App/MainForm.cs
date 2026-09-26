using System.Diagnostics;
using System.Drawing;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class MainForm : Form
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeCritical = 0x0006;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int MaxEventLogChars = 120_000;

    private const int SuspendHardwareTestLevel = 30;
    private const int GateG2TargetCycles = 5;
    private const double SuspendHardwareTestMaxCpuTemperatureC = 80;
    private const double SuspendHardwareTestMaxGpuTemperatureC = 75;
    private const double SuspendHardwareTestMaxCpuPowerW = 50;
    private const double SuspendHardwareTestMaxGpuPowerW = 70;

    private static readonly string SuspendHardwareTestRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VictusFanControl");

    private static readonly string SuspendHardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "suspend-custom.ready");

    private static readonly string SuspendHardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "suspend-custom.result");

    private static readonly string GateDHardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-d.ready");

    private static readonly string GateDHardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-d.result");

    private static readonly string GateEHardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-e.ready");

    private static readonly string GateEHardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-e.result");

    private static readonly string GateELocalRestorePath =
        Path.Combine(SuspendHardwareTestRoot, "gate-e.local-restore");

    private static readonly string GateF1HardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-f1-owned.ready");

    private static readonly string GateF1HardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-f1-owned.result");

    private static readonly string GateF1LocalRestoreStartedPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-f1.local-restore-started");

    private static readonly string GateF2HardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-f2-write-armed.ready");

    private static readonly string GateF2HardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-f2-write-armed.result");

    private static readonly string GateF2LocalRestoreStartedPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-f2.local-restore-started");

    private static readonly string GateG1HardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-g1.ready");

    private static readonly string GateG1PreSleepPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-g1.presleep");

    private static readonly string GateG1ReentryPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-g1.reentry");

    private static readonly string GateG1HardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-g1.result");

    private static readonly string GateG2HardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "gate-g2.result");

    private readonly TelemetryWorker _worker;
    private readonly FanControlCoordinator _fanCoordinator;
    private readonly string _fanBackendStartupDetail;
    private readonly HardwareIdentity _hardwareIdentity;
    private readonly string _modulesDirectory;
    private readonly bool _suspendLifecycleHardwareTest;
    private readonly bool _gateDHardwareTest;
    private readonly bool _gateEHardwareTest;
    private readonly bool _gateF1HardwareTest;
    private readonly bool _gateF2HardwareTest;
    private readonly bool _gateG1HardwareTest;
    private readonly bool _gateG2HardwareTest;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private readonly Label _stateValue = new();
    private readonly Label _stateReason = new();
    private readonly Label _boardValue = new();
    private readonly Label _authorityValue = new();
    private readonly Label _readinessValue = new();
    private readonly Label _freshnessValue = new();
    private readonly Label _safetyReasonValue = new();

    private readonly Label _cpuTemperature = ValueLabel();
    private readonly Label _cpuPower = ValueLabel();
    private readonly Label _cpuLoad = ValueLabel();
    private readonly Label _cpuFan = ValueLabel();
    private readonly Label _gpuTemperature = ValueLabel();
    private readonly Label _gpuPower = ValueLabel();
    private readonly Label _gpuLoad = ValueLabel();
    private readonly Label _gpuFan = ValueLabel();

    private readonly TextBox _diagnostics = new();
    private readonly TextBox _eventLog = new();

    private ToolStripMenuItem? _trayStateItem;
    private ToolStripMenuItem? _trayCpuItem;
    private ToolStripMenuItem? _trayGpuItem;
    private ToolStripMenuItem? _trayAuthorityItem;

    private readonly SortedDictionary<long, string> _pendingSequencedEvents = new();
    private long _nextEventSequence = 1;
    private bool _allowExit;
    private bool _closeHintShown;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    private int _suspendHardwareTestAdvanceGate;
    private int _gateDHardwareTestAdvanceGate;
    private bool _gateDHardwareTestArmed;
    private bool _gateDHardwareTestCompleted;
    private int _gateEHardwareTestAdvanceGate;
    private bool _gateEHardwareTestArmed;
    private bool _gateEHardwareTestCompleted;
    private string? _gateELocalRestoreReason;
    private int _gateF1HardwareTestAdvanceGate;
    private bool _gateF1HardwareTestArmed;
    private bool _gateF1HardwareTestCompleted;
    private int _gateF2HardwareTestAdvanceGate;
    private bool _gateF2HardwareTestArmed;
    private bool _gateF2HardwareTestCompleted;
    private bool _suspendHardwareTestArmed;
    private bool _suspendHardwareTestSuspendObserved;
    private bool _suspendHardwareTestResumeObserved;
    private bool _suspendHardwareTestPreSleepRestoreVerified;
    private bool _suspendHardwareTestCompleted;
    private DateTimeOffset? _suspendHardwareTestArmedAt;
    private bool _suspendHardwareTestBackendAckVerified;

    private int _gateG1HardwareTestAdvanceGate;
    private bool _gateG1HardwareTestArmed;
    private bool _gateG1HardwareTestSuspendObserved;
    private bool _gateG1HardwareTestResumeObserved;
    private bool _gateG1HardwareTestPreSleepVerified;
    private bool _gateG1HardwareTestCompleted;
    private bool _gateG1HardwareTestBackendAckVerified;
    private DateTimeOffset? _gateG1HardwareTestArmedAt;
    private int _gateG1WatchdogPid;
    private int _gateG1AcceptedResumeCount;
    private int _gateGCurrentCycle = 1;

    private bool GateGHardwareTest =>
        _gateG1HardwareTest || _gateG2HardwareTest;

    private string GateGLabel =>
        _gateG2HardwareTest ? "GATE G2" : "GATE G1";

    private int GateGTargetCycleCount =>
        _gateG2HardwareTest ? GateG2TargetCycles : 1;

    private string GateGReadyPath =>
        _gateG2HardwareTest
            ? Path.Combine(
                SuspendHardwareTestRoot,
                $"gate-g2.cycle-{_gateGCurrentCycle}.ready")
            : GateG1HardwareTestReadyPath;

    private string GateGPreSleepPath =>
        _gateG2HardwareTest
            ? Path.Combine(
                SuspendHardwareTestRoot,
                $"gate-g2.cycle-{_gateGCurrentCycle}.presleep")
            : GateG1PreSleepPath;

    private string GateGReentryPath =>
        _gateG2HardwareTest
            ? Path.Combine(
                SuspendHardwareTestRoot,
                $"gate-g2.cycle-{_gateGCurrentCycle}.reentry")
            : GateG1ReentryPath;

    private string GateGCycleResultPath =>
        _gateG2HardwareTest
            ? Path.Combine(
                SuspendHardwareTestRoot,
                $"gate-g2.cycle-{_gateGCurrentCycle}.result")
            : GateG1HardwareTestResultPath;

    private string GateGFinalResultPath =>
        _gateG2HardwareTest
            ? GateG2HardwareTestResultPath
            : GateG1HardwareTestResultPath;

    private volatile TelemetrySnapshot? _lastSnapshot;

    public MainForm(
        string modulesDirectory,
        bool suspendLifecycleHardwareTest = false,
        bool gateDHardwareTest = false,
        bool gateEHardwareTest = false,
        bool gateF1HardwareTest = false,
        bool gateF2HardwareTest = false,
        bool gateG1HardwareTest = false,
        bool gateG2HardwareTest = false)
    {
        Text = "VictusFanControl v0.4-dev — backend integrated / automatic policy OFF";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 560);
        Size = new Size(900, 680);

        _modulesDirectory = modulesDirectory;
        _suspendLifecycleHardwareTest = suspendLifecycleHardwareTest;
        _gateDHardwareTest = gateDHardwareTest;
        _gateEHardwareTest = gateEHardwareTest;
        _gateF1HardwareTest = gateF1HardwareTest;
        _gateF2HardwareTest = gateF2HardwareTest;
        _gateG1HardwareTest = gateG1HardwareTest;
        _gateG2HardwareTest = gateG2HardwareTest;
        _hardwareIdentity = HardwareIdentityReader.ReadCurrent();

        if (_suspendLifecycleHardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            TryDeleteFile(SuspendHardwareTestReadyPath);
            TryDeleteFile(SuspendHardwareTestResultPath);
        }

        if (_gateDHardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            TryDeleteFile(GateDHardwareTestReadyPath);
            TryDeleteFile(GateDHardwareTestResultPath);
        }

        if (_gateEHardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            TryDeleteFile(GateEHardwareTestReadyPath);
            TryDeleteFile(GateEHardwareTestResultPath);
            TryDeleteFile(GateELocalRestorePath);
        }

        if (_gateF1HardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            TryDeleteFile(GateF1HardwareTestReadyPath);
            TryDeleteFile(GateF1HardwareTestResultPath);
            TryDeleteFile(GateF1LocalRestoreStartedPath);
        }

        if (_gateF2HardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            TryDeleteFile(GateF2HardwareTestReadyPath);
            TryDeleteFile(GateF2HardwareTestResultPath);
            TryDeleteFile(GateF2LocalRestoreStartedPath);
        }

        if (_gateG1HardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            TryDeleteFile(GateG1HardwareTestReadyPath);
            TryDeleteFile(GateG1PreSleepPath);
            TryDeleteFile(GateG1ReentryPath);
            TryDeleteFile(GateG1HardwareTestResultPath);
        }

        if (_gateG2HardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);

            foreach (var path in Directory.EnumerateFiles(
                         SuspendHardwareTestRoot,
                         "gate-g2.*"))
            {
                TryDeleteFile(path);
            }
        }

        IFanControlBackend backend;
        try
        {
            IFanControlWatchdogLeaseClient? watchdogLease = null;

            if (_gateF2HardwareTest)
            {
                watchdogLease =
                    new GateF2CommitHoldWatchdogLeaseClient(
                        new NamedPipeFanControlWatchdogLeaseClient(),
                        GateF2HardwareTestReadyPath,
                        SuspendHardwareTestLevel,
                        SuspendHardwareTestLevel);
            }
            else if (_gateDHardwareTest ||
                     _gateEHardwareTest ||
                     _gateF1HardwareTest ||
                     _gateG1HardwareTest ||
                     _gateG2HardwareTest)
            {
                watchdogLease =
                    new NamedPipeFanControlWatchdogLeaseClient();
            }

            backend = new Hp88F8FanControlBackend(
                modulesDirectory,
                watchdogLease);

            _fanBackendStartupDetail = backend.CanWrite
                ? (_gateDHardwareTest ||
                   _gateEHardwareTest ||
                   _gateF1HardwareTest ||
                   _gateF2HardwareTest ||
                   _gateG1HardwareTest ||
                   _gateG2HardwareTest)
                    ? $"HP 88F8 backend initialized with mandatory Gate {(_gateG2HardwareTest ? "G2" : _gateG1HardwareTest ? "G1" : _gateF2HardwareTest ? "F2" : _gateF1HardwareTest ? "F1" : _gateEHardwareTest ? "E" : "D")} watchdog lease."
                    : "HP 88F8 write/restore backend initialized."
                : "HP 88F8 backend present but not write-capable on this hardware.";
        }
        catch (Exception ex)
        {
            backend = new DisabledFanControlBackend();
            _fanBackendStartupDetail =
                $"HP 88F8 backend initialization failed; fail-closed read-only fallback: {ex.Message}";
        }

        _fanCoordinator = new FanControlCoordinator(backend);
        _fanCoordinator.AuthorityChanged += FanCoordinatorOnAuthorityChanged;

        _worker = new TelemetryWorker(modulesDirectory);
        _worker.SnapshotAvailable += WorkerOnSnapshotAvailable;
        _worker.DiagnosticsAvailable += WorkerOnDiagnosticsAvailable;
        _worker.EventLogged += WorkerOnEventLogged;
        _worker.StateMachine.StateChanged += StateMachineOnStateChanged;

        _trayIcon = CreateTrayIcon();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) =>
        {
            UpdateSafetyStatus();
            UpdateTray();
        };

        Controls.Add(BuildUi());

        Shown += (_, _) =>
        {
            AppendEvent($"Modules: {modulesDirectory}");
            AppendEvent($"Board: {_hardwareIdentity.BoardDisplay}; System={_hardwareIdentity.SystemProductName}; SKU={_hardwareIdentity.SystemSku}; BIOS={_hardwareIdentity.BiosVersion}");
            AppendEvent($"Persistent log: {AppLog.CurrentLogPath}");
            AppendEvent($"Fan backend: {_fanCoordinator.BackendName}; CanWrite={_fanCoordinator.BackendCanWrite}; {_fanBackendStartupDetail}");
            AppendEvent("Automatic fan policy is OFF. The integrated backend cannot acquire custom authority unless an explicit future policy requests it through FanControlCoordinator.");

            if (_suspendLifecycleHardwareTest)
            {
                AppendEvent(
                    "SUSPEND TEST: explicit hardware mode enabled. Waiting for initial Healthy telemetry before one bounded 30/30 command. Automatic policy remains OFF.");
            }

            if (_gateDHardwareTest)
            {
                AppendEvent(
                    "GATE D TEST: watchdog-protected hardware mode enabled. Waiting for Healthy telemetry and a service-backed lease before one bounded 30/30 command. Automatic policy remains OFF.");
            }

            if (_gateEHardwareTest)
            {
                AppendEvent(
                    "GATE E TEST: watchdog-death hardware mode enabled. Waiting for Healthy telemetry and a service-backed lease before one bounded 30/30 command. The test expects watchdog IPC loss to trigger the live controller's local HP restore. Automatic policy remains OFF.");
            }

            if (_gateF1HardwareTest)
            {
                AppendEvent(
                    "GATE F1 TEST: OWNED double-death hardware mode enabled. Waiting for Healthy telemetry and a durable service-backed OWNED 30/30 lease. The external harness will force-kill watchdog then GUI; any local GUI restore attempt invalidates causality. Automatic policy remains OFF.");
            }

            if (_gateF2HardwareTest)
            {
                AppendEvent(
                    "GATE F2 TEST: WRITE_ARMED double-death mode enabled. The real backend will perform WMI 30/30 plus EC+dual-tach ACK, then a test-only lease wrapper will hold immediately before Commit. The external harness will kill watchdog then GUI. Automatic policy remains OFF.");
            }

            if (_gateG1HardwareTest)
            {
                AppendEvent(
                    "GATE G1 TEST: full watchdog suspend/resume lifecycle mode enabled. The test requires durable OWNED 30/30 before suspend, journal-free firmware handoff inside PBT_APMSUSPEND, the same watchdog PID across sleep, five-snapshot telemetry recovery, then one controlled post-resume re-entry and final firmware restore. Automatic policy remains OFF.");
            }

            if (_gateG2HardwareTest)
            {
                AppendEvent(
                    $"GATE G2 TEST: {GateG2TargetCycles} consecutive full watchdog suspend/resume cycles enabled in the same GUI and watchdog processes. Every cycle requires durable OWNED 30/30, pre-sleep Firmware + FF/FF + journal absent, one accepted resume, five-snapshot Healthy recovery, one controlled 30/30 re-entry, and final Firmware restore. Automatic policy remains OFF.");
            }

            _uiTimer.Start();
            _worker.Start();
            UpdateSafetyStatus();
        };

        FormClosing += OnFormClosingToTray;
        FormClosed += (_, _) =>
        {
            _uiTimer.Stop();
            _uiTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPowerBroadcast)
        {
            var code = m.WParam.ToInt32();
            switch (code)
            {
                case PbtApmSuspend:
                    HandleSuspendLifecycle("WM_POWERBROADCAST/PBT_APMSUSPEND");
                    break;

                case PbtApmResumeAutomatic:
                    HandleResumeLifecycle("WM_POWERBROADCAST/PBT_APMRESUMEAUTOMATIC");
                    break;

                case PbtApmResumeSuspend:
                    HandleResumeLifecycle("WM_POWERBROADCAST/PBT_APMRESUMESUSPEND");
                    break;

                case PbtApmResumeCritical:
                    HandleResumeLifecycle("WM_POWERBROADCAST/PBT_APMRESUMECRITICAL");
                    break;
            }
        }

        base.WndProc(ref m);
    }


    private void HandleSuspendLifecycle(string source)
    {
        // Fence fan admission first. BlockCustomAdmissionAndRestoreAsync closes
        // its volatile fence before waiting on any in-flight command, so no
        // stale healthy result can acquire Custom authority while the telemetry
        // worker is transitioning into Suspended.
        var boundary = DateTimeOffset.UtcNow;

        var testWasCustom = false;
        var backendAckVerified = false;
        DateTimeOffset? armedAt = null;

        var gateG1WasCustom = false;
        var gateG1BackendAckVerified = false;
        DateTimeOffset? gateG1ArmedAt = null;

        if (_suspendLifecycleHardwareTest &&
            _suspendHardwareTestArmed &&
            !_suspendHardwareTestCompleted)
        {
            _suspendHardwareTestSuspendObserved = true;
            testWasCustom = _fanCoordinator.Authority == FanAuthority.Custom;
            backendAckVerified = _suspendHardwareTestBackendAckVerified;
            armedAt = _suspendHardwareTestArmedAt;

            // Do not start a second EC transaction here. The production backend
            // already returns from ApplyAsync only after EC setpoint acknowledgement
            // plus dual-tach acknowledgement. Out-of-band full EC snapshots while
            // Custom is active bypass the backend IO gate and can hold
            // Global\Access_EC long enough for the continuous ownership supervisor
            // to time out and perform a false fail-safe handoff.
            var armedAge = armedAt.HasValue
                ? Math.Max(0, (boundary - armedAt.Value).TotalSeconds)
                : double.NaN;

            AppendEvent(
                $"SUSPEND TEST: suspend event entered with authority={_fanCoordinator.Authority}; " +
                $"validatedBackendAck={backendAckVerified}; expectedOwnedSetpoint={SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; " +
                $"armedAge={(double.IsNaN(armedAge) ? "n/a" : $"{armedAge:0.000}s")}. " +
                "No out-of-band pre-restore EC probe is issued in the suspend handler.");
        }

        if (GateGHardwareTest &&
            _gateG1HardwareTestArmed &&
            !_gateG1HardwareTestCompleted)
        {
            _gateG1HardwareTestSuspendObserved = true;
            gateG1WasCustom =
                _fanCoordinator.Authority == FanAuthority.Custom;
            gateG1BackendAckVerified =
                _gateG1HardwareTestBackendAckVerified;
            gateG1ArmedAt =
                _gateG1HardwareTestArmedAt;

            var armedAge = gateG1ArmedAt.HasValue
                ? Math.Max(
                    0,
                    (boundary - gateG1ArmedAt.Value).TotalSeconds)
                : double.NaN;

            AppendEvent(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: PBT_APMSUSPEND entered with authority={_fanCoordinator.Authority}; " +
                $"validatedBackendAck={gateG1BackendAckVerified}; expectedOwnedSetpoint={SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; " +
                $"watchdogPid={_gateG1WatchdogPid}; armedAge={(double.IsNaN(armedAge) ? "n/a" : $"{armedAge:0.000}s")}. " +
                "No out-of-band EC probe is issued until the coordinator has completed the watchdog-backed firmware handoff.");
        }

        try
        {
            _fanCoordinator.BlockCustomAdmissionAndRestoreAsync(
                    $"System suspend detected ({source}).",
                    boundary,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            AppendEvent($"CRITICAL: fan firmware restore during suspend failed: {ex.Message}");
            AppLog.Write($"Fan firmware restore during suspend failed: {ex}");
        }
        finally
        {
            if (_suspendLifecycleHardwareTest &&
                _suspendHardwareTestArmed &&
                !_suspendHardwareTestCompleted)
            {
                try
                {
                    var after =
                        new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

                    _suspendHardwareTestPreSleepRestoreVerified =
                        testWasCustom &&
                        backendAckVerified &&
                        _fanCoordinator.Authority == FanAuthority.Firmware &&
                        after.CpuSetpoint == byte.MaxValue &&
                        after.GpuSetpoint == byte.MaxValue;

                    AppendEvent(
                        _suspendHardwareTestPreSleepRestoreVerified
                            ? $"SUSPEND TEST: PRE-SLEEP RESTORE VERIFIED before returning from {source}; " +
                              $"entered Custom after validated backend ACK at {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; authority=Firmware; EC after restore={after}"
                            : $"SUSPEND TEST: PRE-SLEEP RESTORE VERIFICATION FAILED; " +
                              $"wasCustom={testWasCustom}; backendAckVerified={backendAckVerified}; authority={_fanCoordinator.Authority}; after={after}");
                }
                catch (Exception ex)
                {
                    _suspendHardwareTestPreSleepRestoreVerified = false;
                    AppendEvent(
                        $"SUSPEND TEST: PRE-SLEEP RESTORE VERIFICATION FAILED while reading EC: {ex.Message}");
                }
            }

            if (GateGHardwareTest &&
                _gateG1HardwareTestArmed &&
                !_gateG1HardwareTestCompleted)
            {
                try
                {
                    var after =
                        new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

                    var watchdog =
                        GateG1WatchdogStateReader.Read();

                    GateG1WatchdogStateReader.RequireReady(
                        watchdog,
                        _gateG1WatchdogPid);

                    _gateG1HardwareTestPreSleepVerified =
                        gateG1WasCustom &&
                        gateG1BackendAckVerified &&
                        _fanCoordinator.Authority == FanAuthority.Firmware &&
                        after.CpuSetpoint == byte.MaxValue &&
                        after.GpuSetpoint == byte.MaxValue &&
                        !watchdog.JournalPresent;

                    var marker =
                        $"{(_gateG1HardwareTestPreSleepVerified ? "PASS" : "FAIL")}|{DateTimeOffset.Now:O}|" +
                        $"source={source}|wasCustom={gateG1WasCustom}|backendAck={gateG1BackendAckVerified}|" +
                        $"authority={_fanCoordinator.Authority}|ec={after.CpuSetpoint}/{after.GpuSetpoint}|" +
                        $"journal={(watchdog.JournalPresent ? "PRESENT" : "absent")}|watchdogPid={watchdog.ProcessId}";

                    GateG1WatchdogStateReader.WriteDurableMarker(
                        GateGPreSleepPath,
                        marker);

                    AppendEvent(
                        _gateG1HardwareTestPreSleepVerified
                            ? $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: PRE-SLEEP HANDOFF VERIFIED before NotifySuspend/return; authority=Firmware; EC={after}; watchdog PID={watchdog.ProcessId}; durable journal absent."
                            : $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: PRE-SLEEP HANDOFF VERIFICATION FAILED; {marker}");
                }
                catch (Exception ex)
                {
                    _gateG1HardwareTestPreSleepVerified = false;

                    try
                    {
                        GateG1WatchdogStateReader.WriteDurableMarker(
                            GateGPreSleepPath,
                            $"FAIL|{DateTimeOffset.Now:O}|cycle={_gateGCurrentCycle}/{GateGTargetCycleCount}|source={source}|verificationException={ex.Message}");
                    }
                    catch (Exception markerEx)
                    {
                        AppLog.Write(
                            $"{GateGLabel}: could not write failed pre-sleep marker for cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: {markerEx}");
                    }

                    AppendEvent(
                        $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: PRE-SLEEP HANDOFF VERIFICATION FAILED: {ex.Message}");
                }
            }

            _worker.NotifySuspend(source);
        }
    }

    private void HandleResumeLifecycle(string source)
    {
        var boundary = DateTimeOffset.UtcNow;
        var accepted = _worker.NotifyResume(source);

        // A coalesced duplicate must NOT close admission again after a completed
        // recovery, otherwise no second Healthy transition would exist to reopen
        // the fence.
        if (!accepted)
        {
            return;
        }

        if (_suspendLifecycleHardwareTest &&
            _suspendHardwareTestArmed &&
            !_suspendHardwareTestCompleted)
        {
            _suspendHardwareTestResumeObserved = true;
            AppendEvent($"SUSPEND TEST: accepted resume event from {source}.");
        }

        if (GateGHardwareTest &&
            _gateG1HardwareTestArmed &&
            !_gateG1HardwareTestCompleted)
        {
            _gateG1HardwareTestResumeObserved = true;
            _gateG1AcceptedResumeCount++;
            AppendEvent(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: accepted resume event #{_gateG1AcceptedResumeCount} from {source}; custom admission remains fenced until Healthy + watchdog-ready verification.");
        }

        try
        {
            _fanCoordinator.BlockCustomAdmissionAndRestoreAsync(
                    $"Resume requires telemetry revalidation ({source}).",
                    boundary,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            AppendEvent($"CRITICAL: fan authority fencing during resume failed: {ex.Message}");
            AppLog.Write($"Fan authority fencing during resume failed: {ex}");
        }
    }

    private async Task ReopenFanAdmissionAfterHealthyAsync()
    {
        var timestamp = _lastSnapshot?.Timestamp;
        if (!timestamp.HasValue)
        {
            return;
        }

        try
        {
            var reopened = await _fanCoordinator.AllowCustomAdmissionAfterRecoveryAsync(
                timestamp.Value,
                "Telemetry healthy after lifecycle recovery.",
                CancellationToken.None);

            if (reopened)
            {
                Ui(() => AppendEvent(
                    $"Fan custom-admission fence reopened after validated telemetry sample {timestamp.Value:O}. Automatic policy remains OFF."));
            }
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown race.
        }
        catch (Exception ex)
        {
            Ui(() => AppendEvent($"Fan custom-admission reopen failed: {ex.Message}"));
        }
    }

    private System.Windows.Forms.Control BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var overview = new TabPage("Overview");
        overview.Controls.Add(BuildOverview());

        var diagnostics = new TabPage("Diagnostics");
        diagnostics.Controls.Add(BuildDiagnostics());

        var fanCurve = new TabPage("Fan Curve");
        fanCurve.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "The validated HP 88F8 backend is integrated behind FanControlCoordinator.\r\nAutomatic fan policy is intentionally OFF; no curve commands are issued by this GUI yet.",
            AutoSize = false
        });

        tabs.TabPages.Add(overview);
        tabs.TabPages.Add(fanCurve);
        tabs.TabPages.Add(diagnostics);
        return tabs;
    }

    private System.Windows.Forms.Control BuildOverview()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5,
            AutoScroll = true
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var statePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 10)
        };
        statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _stateValue.Text = "Starting";
        _stateValue.AutoSize = true;
        _stateValue.Font = new Font(Font, FontStyle.Bold);

        _stateReason.Text = "Waiting for telemetry validation.";
        _stateReason.AutoSize = true;
        _stateReason.MaximumSize = new Size(650, 0);
        _stateReason.Margin = new Padding(18, 3, 0, 0);

        statePanel.Controls.Add(new Label { Text = "System state:", AutoSize = true }, 0, 0);
        statePanel.Controls.Add(_stateValue, 1, 0);
        statePanel.Controls.Add(new Label { Text = "Reason:", AutoSize = true }, 0, 1);
        statePanel.Controls.Add(_stateReason, 1, 1);

        root.Controls.Add(statePanel);
        root.Controls.Add(BuildSafetyGroup());
        root.Controls.Add(BuildSensorGroup(
            "CPU",
            ("Temperature", _cpuTemperature),
            ("Package power", _cpuPower),
            ("Load", _cpuLoad),
            ("Fan", _cpuFan)));
        root.Controls.Add(BuildSensorGroup(
            "GPU",
            ("Temperature", _gpuTemperature),
            ("Power", _gpuPower),
            ("Load", _gpuLoad),
            ("Fan", _gpuFan)));

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(3, 16, 3, 3),
            Text = "Backend integrated: HP firmware remains authoritative until a future explicit controller acquires custom authority through FanControlCoordinator."
        });

        return root;
    }

    private System.Windows.Forms.Control BuildSafetyGroup()
    {
        var group = new GroupBox
        {
            Text = "Pre-control safety",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(3, 8, 3, 8)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 5
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var value in new[] { _boardValue, _authorityValue, _readinessValue, _freshnessValue, _safetyReasonValue })
        {
            value.AutoSize = true;
            value.MaximumSize = new Size(650, 0);
        }

        _authorityValue.Font = new Font(Font, FontStyle.Bold);
        _readinessValue.Font = new Font(Font, FontStyle.Bold);

        table.Controls.Add(new Label { Text = "Board:", AutoSize = true }, 0, 0);
        table.Controls.Add(_boardValue, 1, 0);
        table.Controls.Add(new Label { Text = "Fan authority:", AutoSize = true }, 0, 1);
        table.Controls.Add(_authorityValue, 1, 1);
        table.Controls.Add(new Label { Text = "Preconditions:", AutoSize = true }, 0, 2);
        table.Controls.Add(_readinessValue, 1, 2);
        table.Controls.Add(new Label { Text = "Telemetry freshness:", AutoSize = true }, 0, 3);
        table.Controls.Add(_freshnessValue, 1, 3);
        table.Controls.Add(new Label { Text = "Gate:", AutoSize = true }, 0, 4);
        table.Controls.Add(_safetyReasonValue, 1, 4);

        group.Controls.Add(table);
        return group;
    }

    private System.Windows.Forms.Control BuildDiagnostics()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(6)
        };

        var copy = new Button { Text = "Copy diagnostics", AutoSize = true };
        copy.Click += (_, _) =>
        {
            var text = $"DIAGNOSTICS{Environment.NewLine}{_diagnostics.Text}{Environment.NewLine}{Environment.NewLine}EVENTS{Environment.NewLine}{_eventLog.Text}";
            if (!string.IsNullOrWhiteSpace(text))
            {
                Clipboard.SetText(text);
            }
        };

        var clear = new Button { Text = "Clear visible events", AutoSize = true };
        clear.Click += (_, _) => _eventLog.Clear();

        var openLogs = new Button { Text = "Open log folder", AutoSize = true };
        openLogs.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLog.LogDirectory,
                UseShellExecute = true
            });
        };

        var readEcState = new Button { Text = "Read 88F8 EC state", AutoSize = true };
        readEcState.Click += async (_, _) =>
        {
            if (_fanCoordinator.Authority != FanAuthority.Firmware)
            {
                AppendEvent(
                    $"88F8 EC state probe refused while fan authority is {_fanCoordinator.Authority}. " +
                    "Out-of-band full EC snapshots are allowed only while HP firmware authority is already established.");
                return;
            }

            readEcState.Enabled = false;
            try
            {
                var state = await Task.Run(
                    () => new Hp88F8EcControlStateProbe(_modulesDirectory).Read());

                AppendEvent($"88F8 EC state: {state}");
            }
            catch (Exception ex)
            {
                AppendEvent($"88F8 EC state probe FAILED: {ex.Message}");
            }
            finally
            {
                readEcState.Enabled = true;
            }
        };

        var scanOmen = new Button { Text = "Scan OMEN processes", AutoSize = true };
        scanOmen.Click += (_, _) =>
        {
            var matches = ExternalControllerScanner.ScanPotentialOmenProcesses();
            if (matches.Count == 0)
            {
                AppendEvent("OMEN process scan: no potential OMEN/Gaming Hub process found.");
                return;
            }

            AppendEvent($"OMEN process scan: {matches.Count} potential process(es) found.");
            foreach (var item in matches)
            {
                AppendEvent(
                    $"  PID={item.ProcessId} name={item.ProcessName} product={item.ProductName ?? "n/a"} description={item.FileDescription ?? "n/a"}");
            }
        };

        buttons.Controls.Add(copy);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(openLogs);
        buttons.Controls.Add(readEcState);
        buttons.Controls.Add(scanOmen);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 280
        };

        _diagnostics.Dock = DockStyle.Fill;
        _diagnostics.Multiline = true;
        _diagnostics.ReadOnly = true;
        _diagnostics.ScrollBars = ScrollBars.Vertical;
        _diagnostics.Font = new Font(FontFamily.GenericMonospace, 9);

        _eventLog.Dock = DockStyle.Fill;
        _eventLog.Multiline = true;
        _eventLog.ReadOnly = true;
        _eventLog.ScrollBars = ScrollBars.Vertical;
        _eventLog.Font = new Font(FontFamily.GenericMonospace, 9);

        split.Panel1.Controls.Add(_diagnostics);
        split.Panel2.Controls.Add(_eventLog);

        root.Controls.Add(buttons, 0, 0);
        root.Controls.Add(split, 0, 1);
        return root;
    }

    private static GroupBox BuildSensorGroup(
        string title,
        params (string Name, Label Value)[] fields)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(3, 8, 3, 8)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = fields.Length
        };

        for (var i = 0; i < fields.Length; i++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / fields.Length));
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false,
                Dock = DockStyle.Fill
            };
            panel.Controls.Add(new Label { Text = fields[i].Name, AutoSize = true });
            panel.Controls.Add(fields[i].Value);
            table.Controls.Add(panel, i, 0);
        }

        group.Controls.Add(table);
        return group;
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();

        _trayStateItem = new ToolStripMenuItem("State: Starting") { Enabled = false };
        _trayCpuItem = new ToolStripMenuItem("CPU: waiting") { Enabled = false };
        _trayGpuItem = new ToolStripMenuItem("GPU: waiting") { Enabled = false };
        _trayAuthorityItem = new ToolStripMenuItem("Fan authority: HP firmware") { Enabled = false };

        var open = new ToolStripMenuItem("Open VictusFanControl");
        var exit = new ToolStripMenuItem("Exit");

        open.Click += (_, _) => RestoreFromTray();
        exit.Click += (_, _) =>
        {
            _allowExit = true;
            Close();
        };

        menu.Items.Add(_trayStateItem);
        menu.Items.Add(_trayCpuItem);
        menu.Items.Add(_trayGpuItem);
        menu.Items.Add(_trayAuthorityItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "VFC Starting",
            ContextMenuStrip = menu,
            Visible = true
        };

        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }



    private async Task EnforceLatestFanSafetyAsync(string reason)
    {
        var result = SafetyGate.Evaluate(
            _hardwareIdentity,
            _worker.StateMachine.State,
            _lastSnapshot,
            DateTimeOffset.UtcNow,
            fanWritePathPresent: _fanCoordinator.BackendCanWrite);

        try
        {
            await _fanCoordinator.EnforceSafetyAsync(
                result,
                reason,
                CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown race.
        }
        catch (Exception ex)
        {
            if (_fanCoordinator.Authority == FanAuthority.Firmware)
            {
                // EnforceSafetyAsync deliberately rethrows the backend/status
                // failure that triggered the handoff even when the fail-safe
                // local restore itself succeeded. Report that distinction
                // accurately; Gate E depends on this exact failure domain.
                Ui(() => AppendEvent(
                    $"Safety supervisor detected backend failure and restored HP firmware authority: {ex.Message}"));
                AppLog.Write(
                    $"Safety-supervisor backend failure triggered a successful firmware handoff: {ex}");
            }
            else
            {
                Ui(() => AppendEvent(
                    $"CRITICAL: safety-supervisor firmware handoff failed: {ex.Message}"));
                AppLog.Write($"Safety-supervisor firmware handoff failed: {ex}");
            }
        }
    }

    private void FanCoordinatorOnAuthorityChanged(
        object? sender,
        FanAuthorityChangedEventArgs e)
    {
        if (_gateF1HardwareTest &&
            _gateF1HardwareTestArmed &&
            !_gateF1HardwareTestCompleted &&
            e.Previous == FanAuthority.Custom &&
            e.Current == FanAuthority.Restoring)
        {
            // Gate F1 requires BOTH original failure domains to be gone before
            // firmware recovery begins. Record the transition synchronously,
            // before the backend restore starts, so the harness can reject a
            // false PASS if the still-live GUI wins the race after watchdog
            // termination but before the GUI force-kill takes effect.
            try
            {
                File.WriteAllText(
                    GateF1LocalRestoreStartedPath,
                    $"LOCAL-RESTORE-STARTED|{DateTimeOffset.Now:O}|reason={e.Reason}");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"GATE F1 TEST: could not write local-restore-started marker: {markerEx}");
            }
        }

        if (_gateF2HardwareTest &&
            _gateF2HardwareTestArmed &&
            !_gateF2HardwareTestCompleted &&
            e.Previous == FanAuthority.Custom &&
            e.Current == FanAuthority.Restoring)
        {
            try
            {
                File.WriteAllText(
                    GateF2LocalRestoreStartedPath,
                    $"LOCAL-RESTORE-STARTED|{DateTimeOffset.Now:O}|reason={e.Reason}");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"GATE F2 TEST: could not write local-restore-started marker: {markerEx}");
            }
        }

        if (_gateEHardwareTest &&
            _gateEHardwareTestArmed &&
            !_gateEHardwareTestCompleted)
        {
            if (e.Previous == FanAuthority.Custom &&
                e.Current == FanAuthority.Restoring &&
                (e.Reason.StartsWith(
                     "Backend control-dependency probe failed during custom authority:",
                     StringComparison.Ordinal) ||
                 e.Reason.StartsWith(
                     "Backend health/ownership probe failed during custom authority:",
                     StringComparison.Ordinal)))
            {
                _gateELocalRestoreReason = e.Reason;
            }
            else if (e.Previous == FanAuthority.Restoring &&
                     e.Current == FanAuthority.Firmware &&
                     _gateELocalRestoreReason is not null)
            {
                _gateEHardwareTestCompleted = true;

                var watchdogTransportLoss =
                    _gateELocalRestoreReason.Contains(
                        FanControlWatchdogTransportException.Marker,
                        StringComparison.Ordinal);

                var localMarkerKind =
                    watchdogTransportLoss
                        ? "LOCAL-RESTORE"
                        : "LOCAL-RESTORE-UNRELATED";

                var resultKind =
                    watchdogTransportLoss
                        ? "PASS-LOCAL-RESTORE"
                        : "FAIL-LOCAL-RESTORE-TRIGGER";

                try
                {
                    File.WriteAllText(
                        GateELocalRestorePath,
                        $"{localMarkerKind}|{DateTimeOffset.Now:O}|authority={e.Current}|reason={_gateELocalRestoreReason}");
                    File.WriteAllText(
                        GateEHardwareTestResultPath,
                        $"{resultKind}|{DateTimeOffset.Now:O}|{_gateELocalRestoreReason}");
                }
                catch (Exception markerEx)
                {
                    AppLog.Write(
                        $"GATE E TEST: could not write local-restore marker: {markerEx}");
                }

                AppLog.Write(
                    watchdogTransportLoss
                        ? $"GATE E TEST: live controller locally restored HP firmware after classified watchdog transport loss. {_gateELocalRestoreReason}"
                        : $"GATE E TEST: firmware restore was safe but NOT caused by watchdog transport loss; Gate E must not pass. {_gateELocalRestoreReason}");
            }
            else if (e.Current == FanAuthority.Faulted)
            {
                _gateEHardwareTestCompleted = true;

                try
                {
                    File.WriteAllText(
                        GateEHardwareTestResultPath,
                        $"FAIL|{DateTimeOffset.Now:O}|authority=Faulted|reason={e.Reason}");
                }
                catch (Exception markerEx)
                {
                    AppLog.Write(
                        $"GATE E TEST: could not write fault marker: {markerEx}");
                }
            }
        }

        Ui(() =>
        {
            AppendEvent(
                $"Fan authority @ {e.Timestamp.ToLocalTime():HH:mm:ss.fff zzz}: {e.Previous} -> {e.Current}. {e.Reason}");
            UpdateSafetyStatus();
            UpdateTray();
        });
    }

    private void WorkerOnSnapshotAvailable(object? sender, TelemetrySnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _ = EnforceLatestFanSafetyAsync("latest telemetry snapshot");

        Ui(() =>
        {
            _cpuTemperature.Text = Format(snapshot.CpuTemperatureC, "°C");
            _cpuPower.Text = Format(snapshot.CpuPackagePowerW, "W");
            _cpuLoad.Text = Format(snapshot.CpuLoadPercent, "%");
            _cpuFan.Text = Format(snapshot.CpuFanRpm, "RPM", 0);

            _gpuTemperature.Text = Format(snapshot.GpuTemperatureC, "°C");
            _gpuPower.Text = Format(snapshot.GpuPowerW, "W");
            _gpuLoad.Text = Format(snapshot.GpuLoadPercent, "%");
            _gpuFan.Text = Format(snapshot.GpuFanRpm, "RPM", 0);

            UpdateSafetyStatus();
            UpdateTray();
        });
    }

    private void WorkerOnDiagnosticsAvailable(object? sender, string text) =>
        Ui(() => _diagnostics.Text = text);

    private void WorkerOnEventLogged(object? sender, string text) =>
        Ui(() => QueueSequencedEvent(text));

    private void StateMachineOnStateChanged(object? sender, SystemStateChangedEventArgs e)
    {
        if (e.Current == SystemState.Healthy)
        {
            // RuntimeStateMachine raises StateChanged synchronously on the
            // telemetry worker thread. Never begin WMI/EC fan-control work
            // inline here: async methods execute synchronously until their
            // first incomplete await, and the HP admission/first-command path
            // can spend multiple seconds in synchronous WMI/EC calls before
            // yielding. That previously prevented TelemetryWorker from
            // completing its Healthy transition and starting the next sample,
            // while the independent 3 s watchdog already observed Healthy and
            // falsely declared telemetry stale. Dispatch the Healthy follow-up
            // to the thread pool so telemetry can return from Transition()
            // immediately and keep its liveness heartbeat moving.
            _ = Task.Run(HandleHealthyStateAsync);
        }
        else
        {
            _ = EnforceLatestFanSafetyAsync($"runtime state changed to {e.Current}");
        }

        Ui(() =>
        {
            _stateValue.Text = e.Current.ToString();
            _stateReason.Text = e.Reason;

            if (_trayStateItem is not null)
            {
                _trayStateItem.Text = $"State: {e.Current}";
            }

            UpdateSafetyStatus();
            UpdateTray();
        });
    }

    private async Task HandleHealthyStateAsync()
    {
        if (_worker.StateMachine.State != SystemState.Healthy)
        {
            return;
        }

        if (GateGHardwareTest)
        {
            // Gate G1/G2 own the admission-reopen ordering: watchdog Ready +
            // journal absent must be proved after the five-snapshot resume
            // recovery and before the lifecycle fence is reopened.
            await AdvanceGateG1HardwareTestAsync();
            return;
        }

        await ReopenFanAdmissionAfterHealthyAsync();

        // The detached continuation may have been queued just before a newer
        // degradation/suspend transition. Never advance a hardware test from a
        // stale Healthy notification.
        if (_worker.StateMachine.State != SystemState.Healthy)
        {
            return;
        }

        if (_suspendLifecycleHardwareTest)
        {
            await AdvanceSuspendLifecycleHardwareTestAsync();
        }

        if (_gateDHardwareTest)
        {
            await AdvanceGateDHardwareTestAsync();
        }

        if (_gateEHardwareTest)
        {
            await AdvanceGateEHardwareTestAsync();
        }

        if (_gateF1HardwareTest)
        {
            await AdvanceGateF1HardwareTestAsync();
        }

        if (_gateF2HardwareTest)
        {
            await AdvanceGateF2HardwareTestAsync();
        }
    }

    private async Task AdvanceGateDHardwareTestAsync()
    {
        if (_gateDHardwareTestCompleted ||
            _gateDHardwareTestArmed ||
            Interlocked.CompareExchange(
                ref _gateDHardwareTestAdvanceGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            var snapshot = _lastSnapshot ??
                throw new InvalidOperationException(
                    "No telemetry snapshot is available for Gate D admission.");

            EnsureSuspendHardwareTestLightLoad(snapshot);

            var safety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                snapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate refused Gate D custom authority: " +
                    string.Join(" | ", safety.Reasons));
            }

            var before =
                new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

            if (before.CpuSetpoint != byte.MaxValue ||
                before.GpuSetpoint != byte.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Gate D requires firmware-owned FF/FF before admission; read {before.CpuSetpoint}/{before.GpuSetpoint}.");
            }

            var entered = await _fanCoordinator.TryEnterCustomAsync(
                safety,
                CancellationToken.None);

            if (!entered ||
                _fanCoordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "Coordinator did not grant watchdog-protected Custom authority for Gate D.");
            }

            var latest = _lastSnapshot ?? snapshot;
            EnsureSuspendHardwareTestLightLoad(latest);

            var commandSafety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                latest,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!commandSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped before Gate D 30/30 command: " +
                    string.Join(" | ", commandSafety.Reasons));
            }

            await _fanCoordinator.ApplyAsync(
                new FanCommand(
                    SuspendHardwareTestLevel,
                    SuspendHardwareTestLevel,
                    "explicit Gate D watchdog/lease hardware validation"),
                commandSafety,
                CancellationToken.None);

            _gateDHardwareTestArmed = true;

            AppendEvent(
                $"GATE D TEST: ARMED at {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; production backend completed EC+dual-tach ACK and watchdog Commit before READY.");

            File.WriteAllText(
                GateDHardwareTestReadyPath,
                $"READY|{DateTimeOffset.Now:O}|authority={_fanCoordinator.Authority}|cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|ack=backend-ec+tachs+watchdog-owned");
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    "Gate D hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"GATE D TEST: cleanup restore also failed: {restoreEx}");
            }

            _gateDHardwareTestCompleted = true;
            TryDeleteFile(GateDHardwareTestReadyPath);

            try
            {
                File.WriteAllText(
                    GateDHardwareTestResultPath,
                    $"FAIL|{DateTimeOffset.Now:O}|{ex.Message}");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"GATE D TEST: could not write failure marker: {markerEx}");
            }

            AppendEvent($"GATE D TEST RESULT: FAIL: {ex.Message}");
            Environment.ExitCode = 71;

            Ui(() =>
            {
                _allowExit = true;
                Close();
            });
        }
        finally
        {
            Interlocked.Exchange(
                ref _gateDHardwareTestAdvanceGate,
                0);
        }
    }

    private async Task AdvanceGateEHardwareTestAsync()
    {
        if (_gateEHardwareTestCompleted ||
            _gateEHardwareTestArmed ||
            Interlocked.CompareExchange(
                ref _gateEHardwareTestAdvanceGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            var snapshot = _lastSnapshot ??
                throw new InvalidOperationException(
                    "No telemetry snapshot is available for Gate E admission.");

            EnsureSuspendHardwareTestLightLoad(snapshot);

            var safety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                snapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate refused Gate E custom authority: " +
                    string.Join(" | ", safety.Reasons));
            }

            var before =
                new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

            if (before.CpuSetpoint != byte.MaxValue ||
                before.GpuSetpoint != byte.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Gate E requires firmware-owned FF/FF before admission; read {before.CpuSetpoint}/{before.GpuSetpoint}.");
            }

            var entered = await _fanCoordinator.TryEnterCustomAsync(
                safety,
                CancellationToken.None);

            if (!entered ||
                _fanCoordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "Coordinator did not grant watchdog-protected Custom authority for Gate E.");
            }

            var latest = _lastSnapshot ?? snapshot;
            EnsureSuspendHardwareTestLightLoad(latest);

            var commandSafety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                latest,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!commandSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped before Gate E 30/30 command: " +
                    string.Join(" | ", commandSafety.Reasons));
            }

            await _fanCoordinator.ApplyAsync(
                new FanCommand(
                    SuspendHardwareTestLevel,
                    SuspendHardwareTestLevel,
                    "explicit Gate E watchdog-death hardware validation"),
                commandSafety,
                CancellationToken.None);

            _gateEHardwareTestArmed = true;

            AppendEvent(
                $"GATE E TEST: ARMED at {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; production backend completed EC+dual-tach ACK and watchdog Commit before READY.");

            File.WriteAllText(
                GateEHardwareTestReadyPath,
                $"READY|{DateTimeOffset.Now:O}|authority={_fanCoordinator.Authority}|cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|ack=backend-ec+tachs+watchdog-owned");
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    "Gate E hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"GATE E TEST: cleanup restore also failed: {restoreEx}");
            }

            _gateEHardwareTestCompleted = true;
            TryDeleteFile(GateEHardwareTestReadyPath);

            try
            {
                File.WriteAllText(
                    GateEHardwareTestResultPath,
                    $"FAIL|{DateTimeOffset.Now:O}|{ex.Message}");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"GATE E TEST: could not write failure marker: {markerEx}");
            }

            AppendEvent($"GATE E TEST RESULT: FAIL: {ex.Message}");
            Environment.ExitCode = 81;

            Ui(() =>
            {
                _allowExit = true;
                Close();
            });
        }
        finally
        {
            Interlocked.Exchange(
                ref _gateEHardwareTestAdvanceGate,
                0);
        }
    }


    private async Task AdvanceGateF1HardwareTestAsync()
    {
        if (_gateF1HardwareTestCompleted ||
            _gateF1HardwareTestArmed ||
            Interlocked.CompareExchange(
                ref _gateF1HardwareTestAdvanceGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            var snapshot = _lastSnapshot ??
                throw new InvalidOperationException(
                    "No telemetry snapshot is available for Gate F1 admission.");

            EnsureSuspendHardwareTestLightLoad(snapshot);

            var safety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                snapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate refused Gate F1 custom authority: " +
                    string.Join(" | ", safety.Reasons));
            }

            var before =
                new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

            if (before.CpuSetpoint != byte.MaxValue ||
                before.GpuSetpoint != byte.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Gate F1 requires firmware-owned FF/FF before admission; read {before.CpuSetpoint}/{before.GpuSetpoint}.");
            }

            var entered = await _fanCoordinator.TryEnterCustomAsync(
                safety,
                CancellationToken.None);

            if (!entered ||
                _fanCoordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "Coordinator did not grant watchdog-protected Custom authority for Gate F1.");
            }

            var latest = _lastSnapshot ?? snapshot;
            EnsureSuspendHardwareTestLightLoad(latest);

            var commandSafety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                latest,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!commandSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped before Gate F1 30/30 command: " +
                    string.Join(" | ", commandSafety.Reasons));
            }

            await _fanCoordinator.ApplyAsync(
                new FanCommand(
                    SuspendHardwareTestLevel,
                    SuspendHardwareTestLevel,
                    "explicit Gate F1 OWNED double-death hardware validation"),
                commandSafety,
                CancellationToken.None);

            _gateF1HardwareTestArmed = true;

            AppendEvent(
                $"GATE F1 TEST: ARMED at {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; production backend completed EC+dual-tach ACK and watchdog Commit before READY. Awaiting external double-kill.");

            File.WriteAllText(
                GateF1HardwareTestReadyPath,
                $"READY|{DateTimeOffset.Now:O}|authority={_fanCoordinator.Authority}|cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|ack=backend-ec+tachs+watchdog-owned");
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    "Gate F1 hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"GATE F1 TEST: cleanup restore also failed: {restoreEx}");
            }

            _gateF1HardwareTestCompleted = true;
            TryDeleteFile(GateF1HardwareTestReadyPath);

            try
            {
                File.WriteAllText(
                    GateF1HardwareTestResultPath,
                    $"FAIL|{DateTimeOffset.Now:O}|{ex.Message}");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"GATE F1 TEST: could not write failure marker: {markerEx}");
            }

            AppendEvent($"GATE F1 TEST RESULT: FAIL: {ex.Message}");
            Environment.ExitCode = 91;

            Ui(() =>
            {
                _allowExit = true;
                Close();
            });
        }
        finally
        {
            Interlocked.Exchange(
                ref _gateF1HardwareTestAdvanceGate,
                0);
        }
    }


    private async Task AdvanceGateF2HardwareTestAsync()
    {
        if (_gateF2HardwareTestCompleted ||
            _gateF2HardwareTestArmed ||
            Interlocked.CompareExchange(
                ref _gateF2HardwareTestAdvanceGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            var snapshot = _lastSnapshot ??
                throw new InvalidOperationException(
                    "No telemetry snapshot is available for Gate F2 admission.");

            EnsureSuspendHardwareTestLightLoad(snapshot);

            var safety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                snapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate refused Gate F2 custom authority: " +
                    string.Join(" | ", safety.Reasons));
            }

            var before =
                new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

            if (before.CpuSetpoint != byte.MaxValue ||
                before.GpuSetpoint != byte.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Gate F2 requires firmware-owned FF/FF before admission; read {before.CpuSetpoint}/{before.GpuSetpoint}.");
            }

            var entered = await _fanCoordinator.TryEnterCustomAsync(
                safety,
                CancellationToken.None);

            if (!entered ||
                _fanCoordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "Coordinator did not grant watchdog-protected Custom authority for Gate F2.");
            }

            var latest = _lastSnapshot ?? snapshot;
            EnsureSuspendHardwareTestLightLoad(latest);

            var commandSafety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                latest,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!commandSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped before Gate F2 30/30 command: " +
                    string.Join(" | ", commandSafety.Reasons));
            }

            // Mark the F2 hardware-test session before entering ApplyAsync.
            // On the successful path ApplyAsync never returns: the test-only
            // lease wrapper writes READY inside CommitAsync, after production
            // WMI + EC + dual-tach ACK, then deliberately holds before Commit.
            _gateF2HardwareTestArmed = true;

            AppendEvent(
                $"GATE F2 TEST: dispatching {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; waiting for the pre-Commit WRITE_ARMED hold after real EC+dual-tach ACK.");

            await _fanCoordinator.ApplyAsync(
                new FanCommand(
                    SuspendHardwareTestLevel,
                    SuspendHardwareTestLevel,
                    "explicit Gate F2 WRITE_ARMED post-ACK/pre-Commit hardware validation"),
                commandSafety,
                CancellationToken.None);

            throw new InvalidOperationException(
                "Gate F2 Commit hold unexpectedly returned; Commit must remain unforwarded until process death.");
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    "Gate F2 hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"GATE F2 TEST: cleanup restore also failed: {restoreEx}");
            }

            _gateF2HardwareTestCompleted = true;
            TryDeleteFile(GateF2HardwareTestReadyPath);

            try
            {
                File.WriteAllText(
                    GateF2HardwareTestResultPath,
                    $"FAIL|{DateTimeOffset.Now:O}|{ex.Message}");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"GATE F2 TEST: could not write failure marker: {markerEx}");
            }

            AppendEvent($"GATE F2 TEST RESULT: FAIL: {ex.Message}");
            Environment.ExitCode = 92;

            Ui(() =>
            {
                _allowExit = true;
                Close();
            });
        }
        finally
        {
            Interlocked.Exchange(
                ref _gateF2HardwareTestAdvanceGate,
                0);
        }
    }

    private async Task AdvanceSuspendLifecycleHardwareTestAsync()
    {
        if (_suspendHardwareTestCompleted ||
            Interlocked.CompareExchange(
                ref _suspendHardwareTestAdvanceGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            if (!_suspendHardwareTestArmed)
            {
                var snapshot = _lastSnapshot ??
                    throw new InvalidOperationException(
                        "No telemetry snapshot is available for suspend-test admission.");

                EnsureSuspendHardwareTestLightLoad(snapshot);

                var safety = SafetyGate.Evaluate(
                    _hardwareIdentity,
                    _worker.StateMachine.State,
                    snapshot,
                    DateTimeOffset.UtcNow,
                    fanWritePathPresent: _fanCoordinator.BackendCanWrite);

                if (!safety.CustomControlPermitted)
                {
                    throw new InvalidOperationException(
                        "SafetyGate refused suspend-test custom authority: " +
                        string.Join(" | ", safety.Reasons));
                }

                var before =
                    new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

                if (before.CpuSetpoint != byte.MaxValue ||
                    before.GpuSetpoint != byte.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Suspend test requires firmware-owned FF/FF before admission; read {before.CpuSetpoint}/{before.GpuSetpoint}.");
                }

                var entered = await _fanCoordinator.TryEnterCustomAsync(
                    safety,
                    CancellationToken.None);

                if (!entered ||
                    _fanCoordinator.Authority != FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        "Coordinator did not grant Custom authority for suspend test.");
                }

                var latest = _lastSnapshot ?? snapshot;
                EnsureSuspendHardwareTestLightLoad(latest);

                var commandSafety = SafetyGate.Evaluate(
                    _hardwareIdentity,
                    _worker.StateMachine.State,
                    latest,
                    DateTimeOffset.UtcNow,
                    fanWritePathPresent: _fanCoordinator.BackendCanWrite);

                if (!commandSafety.CustomControlPermitted)
                {
                    throw new InvalidOperationException(
                        "SafetyGate dropped before suspend-test 30/30 command: " +
                        string.Join(" | ", commandSafety.Reasons));
                }

                await _fanCoordinator.ApplyAsync(
                    new FanCommand(
                        SuspendHardwareTestLevel,
                        SuspendHardwareTestLevel,
                        "explicit suspend lifecycle hardware validation"),
                    commandSafety,
                    CancellationToken.None);

                // Hp88F8FanControlBackend.ApplyAsync does not return until the EC
                // setpoint is 30/30 and both tachometers have acknowledged the
                // command. Do not immediately open a second AcpiEcReader here:
                // that out-of-band full snapshot bypasses the backend IO gate and
                // can contend with the continuous safety supervisor on
                // Global\Access_EC, causing a false ownership-probe failure and
                // an unnecessary firmware handoff.
                _suspendHardwareTestBackendAckVerified = true;
                _suspendHardwareTestArmedAt = DateTimeOffset.UtcNow;
                _suspendHardwareTestArmed = true;

                AppendEvent(
                    $"SUSPEND TEST: ARMED at {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel} with authority=Custom after production backend EC+dual-tach ACK. " +
                    "No out-of-band EC snapshot is issued while Custom is active.");

                File.WriteAllText(
                    SuspendHardwareTestReadyPath,
                    $"READY|{DateTimeOffset.Now:O}|authority={_fanCoordinator.Authority}|cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|ack=backend-ec+tachs");
                return;
            }

            if (!_suspendHardwareTestResumeObserved)
            {
                return;
            }

            var afterResume =
                new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

            var recovered =
                _suspendHardwareTestSuspendObserved &&
                _suspendHardwareTestPreSleepRestoreVerified &&
                _worker.StateMachine.State == SystemState.Healthy &&
                _fanCoordinator.Authority == FanAuthority.Firmware &&
                afterResume.CpuSetpoint == byte.MaxValue &&
                afterResume.GpuSetpoint == byte.MaxValue;

            if (!recovered)
            {
                throw new InvalidOperationException(
                    "Post-resume verification failed: " +
                    $"suspendObserved={_suspendHardwareTestSuspendObserved}, " +
                    $"preSleepRestore={_suspendHardwareTestPreSleepRestoreVerified}, " +
                    $"state={_worker.StateMachine.State}, authority={_fanCoordinator.Authority}, " +
                    $"EC={afterResume}.");
            }

            CompleteSuspendHardwareTest(
                success: true,
                exitCode: 0,
                message:
                    $"PASS: suspend arrived while Custom 30/30 was owned, FF/FF -> LegacyDefault was verified before the suspend handler returned, and resume recovered to Healthy/Firmware with EC FF/FF. EC={afterResume}");
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    "Suspend hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"SUSPEND TEST: cleanup restore also failed: {restoreEx}");
            }

            CompleteSuspendHardwareTest(
                success: false,
                exitCode: 61,
                message: $"FAIL: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(
                ref _suspendHardwareTestAdvanceGate,
                0);
        }
    }

    private async Task AdvanceGateG1HardwareTestAsync()
    {
        if (_gateG1HardwareTestCompleted ||
            Interlocked.CompareExchange(
                ref _gateG1HardwareTestAdvanceGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            if (!_gateG1HardwareTestArmed)
            {
                var snapshot = _lastSnapshot ??
                    throw new InvalidOperationException(
                        "No telemetry snapshot is available for Gate G1 admission.");

                EnsureSuspendHardwareTestLightLoad(snapshot);

                var watchdog =
                    GateG1WatchdogStateReader.Read();
                GateG1WatchdogStateReader.RequireReady(watchdog);

                if (watchdog.JournalPresent)
                {
                    throw new InvalidOperationException(
                        $"Gate G1 baseline requires no durable lease journal; found '{watchdog.JournalPath}'.");
                }

                _gateG1WatchdogPid = watchdog.ProcessId;

                var before =
                    new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

                if (before.CpuSetpoint != byte.MaxValue ||
                    before.GpuSetpoint != byte.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Gate G1 requires firmware-owned FF/FF before admission; read {before.CpuSetpoint}/{before.GpuSetpoint}.");
                }

                var safety = SafetyGate.Evaluate(
                    _hardwareIdentity,
                    _worker.StateMachine.State,
                    snapshot,
                    DateTimeOffset.UtcNow,
                    fanWritePathPresent: _fanCoordinator.BackendCanWrite);

                if (!safety.CustomControlPermitted)
                {
                    throw new InvalidOperationException(
                        "SafetyGate refused Gate G1 initial custom authority: " +
                        string.Join(" | ", safety.Reasons));
                }

                var entered = await _fanCoordinator.TryEnterCustomAsync(
                    safety,
                    CancellationToken.None);

                if (!entered ||
                    _fanCoordinator.Authority != FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        "Coordinator did not grant watchdog-protected Custom authority for Gate G1.");
                }

                var latest = _lastSnapshot ?? snapshot;
                EnsureSuspendHardwareTestLightLoad(latest);

                var commandSafety = SafetyGate.Evaluate(
                    _hardwareIdentity,
                    _worker.StateMachine.State,
                    latest,
                    DateTimeOffset.UtcNow,
                    fanWritePathPresent: _fanCoordinator.BackendCanWrite);

                if (!commandSafety.CustomControlPermitted)
                {
                    throw new InvalidOperationException(
                        "SafetyGate dropped before Gate G1 initial 30/30 command: " +
                        string.Join(" | ", commandSafety.Reasons));
                }

                await _fanCoordinator.ApplyAsync(
                    new FanCommand(
                        SuspendHardwareTestLevel,
                        SuspendHardwareTestLevel,
                        "explicit Gate G1 full-watchdog suspend lifecycle validation"),
                    commandSafety,
                    CancellationToken.None);

                _gateG1HardwareTestBackendAckVerified = true;
                _gateG1HardwareTestArmedAt = DateTimeOffset.UtcNow;
                _gateG1HardwareTestArmed = true;

                GateG1WatchdogStateReader.WriteDurableMarker(
                    GateG1HardwareTestReadyPath,
                    $"READY|{DateTimeOffset.Now:O}|authority={_fanCoordinator.Authority}|" +
                    $"cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|" +
                    $"ack=backend-ec+tachs+watchdog-owned|watchdogPid={_gateG1WatchdogPid}");

                AppendEvent(
                    $"GATE G1: READY at {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; production backend completed durable WriteIntent -> WMI -> EC+dual-tach ACK -> Commit/OWNED. Watchdog PID={_gateG1WatchdogPid}. Awaiting external Windows suspend request.");
                return;
            }

            if (!_gateG1HardwareTestResumeObserved)
            {
                return;
            }

            if (!_gateG1HardwareTestSuspendObserved)
            {
                throw new InvalidOperationException(
                    "Gate G1 did not observe PBT_APMSUSPEND while the initial watchdog-owned 30/30 cycle was armed.");
            }

            if (!_gateG1HardwareTestPreSleepVerified)
            {
                throw new InvalidOperationException(
                    "Gate G1 pre-sleep handoff was not verified inside PBT_APMSUSPEND.");
            }

            if (_gateG1AcceptedResumeCount != 1)
            {
                throw new InvalidOperationException(
                    $"Gate G1 requires exactly one accepted resume event; observed {_gateG1AcceptedResumeCount}.");
            }

            if (_worker.StateMachine.State != SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"Gate G1 post-resume continuation requires Healthy telemetry; state={_worker.StateMachine.State}.");
            }

            var watchdogAfterResume =
                GateG1WatchdogStateReader.Read();

            GateG1WatchdogStateReader.RequireReady(
                watchdogAfterResume,
                _gateG1WatchdogPid);

            if (watchdogAfterResume.JournalPresent)
            {
                throw new InvalidOperationException(
                    $"Gate G1 post-resume watchdog is not clean; journal remains at '{watchdogAfterResume.JournalPath}'.");
            }

            var afterResume =
                new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

            if (afterResume.CpuSetpoint != byte.MaxValue ||
                afterResume.GpuSetpoint != byte.MaxValue ||
                _fanCoordinator.Authority != FanAuthority.Firmware)
            {
                throw new InvalidOperationException(
                    $"Gate G1 post-resume firmware baseline invalid: authority={_fanCoordinator.Authority}, EC={afterResume}.");
            }

            var recoveryTimestamp = _lastSnapshot?.Timestamp ??
                throw new InvalidOperationException(
                    "Gate G1 Healthy state has no validated post-resume telemetry snapshot.");

            var reopened =
                await _fanCoordinator.AllowCustomAdmissionAfterRecoveryAsync(
                    recoveryTimestamp,
                    "Gate G1: Healthy post-boundary telemetry and watchdog Ready/journal-absent verified.",
                    CancellationToken.None);

            if (!reopened)
            {
                throw new InvalidOperationException(
                    "Gate G1 lifecycle fence refused to reopen after validated recovery.");
            }

            AppendEvent(
                $"GATE G1: admission fence reopened only after Healthy recovery snapshot {recoveryTimestamp:O}, watchdog PID {_gateG1WatchdogPid} Ready and journal absent.");

            if (_worker.StateMachine.State != SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    "Gate G1 telemetry degraded after fence reopen and before controlled re-entry.");
            }

            var reentrySnapshot = _lastSnapshot ??
                throw new InvalidOperationException(
                    "Gate G1 has no telemetry snapshot for controlled re-entry.");

            EnsureSuspendHardwareTestLightLoad(reentrySnapshot);

            var reentrySafety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                reentrySnapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!reentrySafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate refused Gate G1 controlled post-resume re-entry: " +
                    string.Join(" | ", reentrySafety.Reasons));
            }

            var reentered =
                await _fanCoordinator.TryEnterCustomAsync(
                    reentrySafety,
                    CancellationToken.None);

            if (!reentered ||
                _fanCoordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "Gate G1 could not reacquire watchdog-protected Custom authority after validated recovery.");
            }

            var commandSnapshot = _lastSnapshot ?? reentrySnapshot;
            EnsureSuspendHardwareTestLightLoad(commandSnapshot);

            var reentryCommandSafety = SafetyGate.Evaluate(
                _hardwareIdentity,
                _worker.StateMachine.State,
                commandSnapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!reentryCommandSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped before Gate G1 controlled re-entry 30/30 command: " +
                    string.Join(" | ", reentryCommandSafety.Reasons));
            }

            await _fanCoordinator.ApplyAsync(
                new FanCommand(
                    SuspendHardwareTestLevel,
                    SuspendHardwareTestLevel,
                    "Gate G1 controlled post-resume re-entry validation"),
                reentryCommandSafety,
                CancellationToken.None);

            GateG1WatchdogStateReader.WriteDurableMarker(
                GateG1ReentryPath,
                $"REENTRY|{DateTimeOffset.Now:O}|authority={_fanCoordinator.Authority}|" +
                $"cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|" +
                $"ack=backend-ec+tachs+watchdog-owned|watchdogPid={_gateG1WatchdogPid}");

            AppendEvent(
                "GATE G1: controlled post-resume Custom 30/30 re-entry completed with backend EC+dual-tach ACK and a new watchdog OWNED lease.");

            await _fanCoordinator.RestoreFirmwareAsync(
                "Gate G1 controlled post-resume re-entry complete.",
                CancellationToken.None);

            var watchdogFinal =
                GateG1WatchdogStateReader.Read();

            GateG1WatchdogStateReader.RequireReady(
                watchdogFinal,
                _gateG1WatchdogPid);

            var finalEc =
                new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

            if (_fanCoordinator.Authority != FanAuthority.Firmware ||
                finalEc.CpuSetpoint != byte.MaxValue ||
                finalEc.GpuSetpoint != byte.MaxValue ||
                watchdogFinal.JournalPresent)
            {
                throw new InvalidOperationException(
                    $"Gate G1 final handoff invalid: authority={_fanCoordinator.Authority}, EC={finalEc}, journal={(watchdogFinal.JournalPresent ? "PRESENT" : "absent")}.");
            }

            CompleteGateG1HardwareTest(
                success: true,
                exitCode: 0,
                message:
                    $"PASS: initial OWNED 30/30 handed off to Firmware + EC FF/FF + journal absent before sleep; exactly one resume was accepted; telemetry recovered to Healthy; watchdog PID {_gateG1WatchdogPid} remained stable; one controlled post-resume 30/30 re-entry succeeded; final authority=Firmware, EC={finalEc}, journal absent.");
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    "Gate G1 hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"GATE G1: cleanup restore also failed: {restoreEx}");
            }

            CompleteGateG1HardwareTest(
                success: false,
                exitCode: 111,
                message: $"FAIL: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(
                ref _gateG1HardwareTestAdvanceGate,
                0);
        }
    }

    private void CompleteGateG1HardwareTest(
        bool success,
        int exitCode,
        string message)
    {
        if (_gateG1HardwareTestCompleted)
        {
            return;
        }

        _gateG1HardwareTestCompleted = true;
        TryDeleteFile(GateG1HardwareTestReadyPath);

        var result =
            $"{(success ? "PASS" : "FAIL")}|{DateTimeOffset.Now:O}|{message}";

        try
        {
            GateG1WatchdogStateReader.WriteDurableMarker(
                GateG1HardwareTestResultPath,
                result);
        }
        catch (Exception ex)
        {
            AppLog.Write(
                $"GATE G1: could not write result marker: {ex}");
        }

        AppendEvent($"GATE G1 RESULT: {message}");
        Environment.ExitCode = exitCode;

        Ui(() =>
        {
            _allowExit = true;
            Close();
        });
    }

    private static void EnsureSuspendHardwareTestLightLoad(
        TelemetrySnapshot snapshot)
    {
        if (snapshot.CpuTemperatureC > SuspendHardwareTestMaxCpuTemperatureC ||
            snapshot.GpuTemperatureC > SuspendHardwareTestMaxGpuTemperatureC ||
            snapshot.CpuPackagePowerW > SuspendHardwareTestMaxCpuPowerW ||
            snapshot.GpuPowerW > SuspendHardwareTestMaxGpuPowerW)
        {
            throw new InvalidOperationException(
                "Suspend hardware test requires light load. " +
                $"Limits: CPU <= {SuspendHardwareTestMaxCpuTemperatureC:0} C / " +
                $"{SuspendHardwareTestMaxCpuPowerW:0} W, GPU <= " +
                $"{SuspendHardwareTestMaxGpuTemperatureC:0} C / " +
                $"{SuspendHardwareTestMaxGpuPowerW:0} W.");
        }
    }

    private void CompleteSuspendHardwareTest(
        bool success,
        int exitCode,
        string message)
    {
        if (_suspendHardwareTestCompleted)
        {
            return;
        }

        _suspendHardwareTestCompleted = true;
        TryDeleteFile(SuspendHardwareTestReadyPath);

        var result =
            $"{(success ? "PASS" : "FAIL")}|{DateTimeOffset.Now:O}|{message}";

        try
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            File.WriteAllText(
                SuspendHardwareTestResultPath,
                result);
        }
        catch (Exception ex)
        {
            AppLog.Write(
                $"SUSPEND TEST: could not write result marker: {ex}");
        }

        AppendEvent($"SUSPEND TEST RESULT: {message}");
        Environment.ExitCode = exitCode;

        Ui(() =>
        {
            _allowExit = true;
            Close();
        });
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Test markers are diagnostic-only and must not destabilize runtime.
        }
    }

    private void UpdateSafetyStatus()
    {
        var result = SafetyGate.EvaluateForDisplay(
            _hardwareIdentity,
            _worker.StateMachine.State,
            _lastSnapshot,
            DateTimeOffset.UtcNow,
            fanWritePathPresent: _fanCoordinator.BackendCanWrite);

        _boardValue.Text = $"{_hardwareIdentity.BoardDisplay} — {(result.BoardAllowed ? "ALLOWLISTED" : "BLOCKED")}";
        _authorityValue.Text = _fanCoordinator.Authority switch
        {
            FanAuthority.Firmware => "HP Firmware",
            FanAuthority.Custom => "VictusFanControl",
            FanAuthority.Restoring => "Restoring HP firmware",
            FanAuthority.Faulted => "FAULTED / uncertain",
            _ => _fanCoordinator.Authority.ToString()
        };
        _authorityValue.ForeColor = _fanCoordinator.Authority == FanAuthority.Faulted
            ? Color.DarkRed
            : SystemColors.ControlText;

        _readinessValue.Text = result.CustomControlPermitted
            ? "READY — backend available; automatic policy OFF"
            : result.PreconditionsReady
                ? "PRECONDITIONS READY — backend unavailable"
                : "BLOCKED";
        _readinessValue.ForeColor = result.CustomControlPermitted
            ? Color.DarkGreen
            : Color.DarkGoldenrod;

        if (_lastSnapshot is null)
        {
            _freshnessValue.Text = "waiting for first sample";
        }
        else
        {
            var age = DateTimeOffset.UtcNow - _lastSnapshot.Timestamp;
            _freshnessValue.Text = $"{Math.Max(0, age.TotalSeconds):0.0} s — {(result.SnapshotFresh ? "fresh" : "STALE")}";
        }

        var visibleReasons = result.Reasons
            .Where(reason => !reason.StartsWith("Fan write/restore backend", StringComparison.Ordinal))
            .Take(3)
            .ToArray();

        _safetyReasonValue.Text = visibleReasons.Length == 0
            ? result.CustomControlPermitted
                ? "Safety preconditions pass and the backend is available. Automatic policy remains OFF."
                : "Safety preconditions pass; no write-capable backend is available."
            : string.Join(" | ", visibleReasons);
    }

    private void UpdateTray()
    {
        if (_trayStateItem is null || _trayCpuItem is null || _trayGpuItem is null || _trayAuthorityItem is null)
        {
            return;
        }

        var state = _worker.StateMachine.State;
        _trayStateItem.Text = $"State: {state}";

        if (_lastSnapshot is null)
        {
            _trayCpuItem.Text = "CPU: waiting";
            _trayGpuItem.Text = "GPU: waiting";
        }
        else
        {
            _trayCpuItem.Text =
                $"CPU: {FormatCompact(_lastSnapshot.CpuTemperatureC, "C")} | {FormatCompact(_lastSnapshot.CpuFanRpm, "RPM", 0)}";
            _trayGpuItem.Text =
                $"GPU: {FormatCompact(_lastSnapshot.GpuTemperatureC, "C")} | {FormatCompact(_lastSnapshot.GpuFanRpm, "RPM", 0)}";
        }

        _trayAuthorityItem.Text = $"Fan authority: {_fanCoordinator.Authority}";

        var tooltip = _lastSnapshot is null
            ? $"VFC {state}"
            : $"VFC {state} | CPU {FormatCompact(_lastSnapshot.CpuTemperatureC, "C")} GPU {FormatCompact(_lastSnapshot.GpuTemperatureC, "C")}";

        _trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private async void OnFormClosingToTray(object? sender, FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();

            if (!_closeHintShown)
            {
                _closeHintShown = true;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "VictusFanControl is still running",
                    "Use the tray icon to reopen it or choose Exit to stop it.",
                    ToolTipIcon.Info);
            }

            return;
        }

        if (_shutdownComplete)
        {
            return;
        }

        // Windows shutdown cannot depend on an async-void continuation surviving
        // after the form closes. Stop the worker synchronously while the window
        // message is still being handled.
        if (e.CloseReason == CloseReason.WindowsShutDown)
        {
            try
            {
                _fanCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Write($"Fan coordinator shutdown/restore during Windows shutdown failed: {ex}");
            }

            try
            {
                _worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Write($"Worker shutdown during Windows shutdown failed: {ex}");
            }

            _shutdownComplete = true;
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        Enabled = false;
        HideToTray();
        AppLog.Write("Explicit application shutdown started.");

        try
        {
            await _fanCoordinator.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Fan coordinator shutdown/restore failed: {ex}");
        }

        try
        {
            await _worker.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Worker shutdown failed: {ex}");
        }

        _shutdownComplete = true;
        Close();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void QueueSequencedEvent(string text)
    {
        if (!TryParseSequence(text, out var sequence))
        {
            AppendEvent(text);
            return;
        }

        _pendingSequencedEvents[sequence] = text;

        while (_pendingSequencedEvents.Remove(_nextEventSequence, out var next))
        {
            AppendEvent(next);
            _nextEventSequence++;
        }
    }

    private static bool TryParseSequence(string text, out long sequence)
    {
        sequence = 0;
        if (string.IsNullOrWhiteSpace(text) || text[0] != '#')
        {
            return false;
        }

        var end = text.IndexOf(' ');
        if (end <= 1)
        {
            return false;
        }

        return long.TryParse(text.AsSpan(1, end - 1), out sequence);
    }

    private void AppendEvent(string text)
    {
        AppLog.Write(text);

        if (_eventLog.TextLength > MaxEventLogChars)
        {
            var remove = Math.Min(30_000, _eventLog.TextLength);
            var currentText = _eventLog.Text ?? string.Empty;
            var boundary = currentText.IndexOf(Environment.NewLine, remove, StringComparison.Ordinal);
            if (boundary < 0)
            {
                boundary = remove;
            }

            _eventLog.Select(0, Math.Min(_eventLog.TextLength, boundary + Environment.NewLine.Length));
            _eventLog.SelectedText = string.Empty;
        }

        if (_eventLog.TextLength > 0)
        {
            _eventLog.AppendText(Environment.NewLine);
        }

        _eventLog.AppendText(text);
        _eventLog.SelectionStart = _eventLog.TextLength;
        _eventLog.ScrollToCaret();
    }

    private void Ui(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }

        action();
    }

    private static Label ValueLabel() => new()
    {
        Text = "—",
        AutoSize = true,
        Font = new Font(FontFamily.GenericSansSerif, 14, FontStyle.Bold)
    };

    private static string Format(double? value, string suffix, int decimals = 1) =>
        value.HasValue ? $"{value.Value.ToString($"F{decimals}")} {suffix}" : "n/a";

    private static string FormatCompact(double? value, string suffix, int decimals = 1) =>
        value.HasValue ? $"{value.Value.ToString($"F{decimals}")}{suffix}" : "n/a";
}
