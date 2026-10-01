using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private const int PbtPowerSettingChange = 0x8013;
    private const int DeviceNotifyWindowHandle = 0;
    private const int MaxEventLogChars = 120_000;

    private static readonly Guid GuidSessionDisplayStatus =
        new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    private enum GateGResumeProofState
    {
        Pending = 0,
        Verifying = 1,
        Verified = 2,
        Failed = 3
    }

    private const int SuspendHardwareTestLevel = 30;
    private const int GateG2TargetCycles = 5;
    private const int GateGInterCycleStableSnapshotsRequired = 2;
    private static readonly TimeSpan GateGInterCycleStabilityTimeout =
        TimeSpan.FromSeconds(15);
    // Microsoft documents only an approximately two-second PBT_APMSUSPEND
    // handling window. Gate G requires the critical restore + lease release +
    // telemetry suspend mark to finish with margin before that deadline.
    private const double GateGSuspendProofBudgetMs = 1800;
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

    private static readonly string M6HardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "m6-modern-standby.ready");

    private static readonly string M6PreSleepPath =
        Path.Combine(SuspendHardwareTestRoot, "m6-modern-standby.presleep");

    private static readonly string M6ResumeGatePath =
        Path.Combine(SuspendHardwareTestRoot, "m6-modern-standby.resume-gate");

    private static readonly string M6ReentryPath =
        Path.Combine(SuspendHardwareTestRoot, "m6-modern-standby.reentry");

    private static readonly string M6HardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "m6-modern-standby.result");

    private static readonly string M9DHardwareTestReadyPath =
        Path.Combine(SuspendHardwareTestRoot, "m9d-production-lifecycle.ready");

    private static readonly string M9DPreSleepPath =
        Path.Combine(SuspendHardwareTestRoot, "m9d-production-lifecycle.presleep");

    private static readonly string M9DResumeGatePath =
        Path.Combine(SuspendHardwareTestRoot, "m9d-production-lifecycle.resume-gate");

    private static readonly string M9DReentryPath =
        Path.Combine(SuspendHardwareTestRoot, "m9d-production-lifecycle.reentry");

    private static readonly string M9DHardwareTestResultPath =
        Path.Combine(SuspendHardwareTestRoot, "m9d-production-lifecycle.result");

    private readonly TelemetryWorker _worker;
    private readonly FanControlCoordinator _fanCoordinator;
    private readonly string _fanBackendStartupDetail;
    private readonly HardwareIdentity _hardwareIdentity;
    private readonly HardwareTargetProfile? _targetProfile;
    private readonly Hp8C40ThermalEmergencyConfirmation _thermalEmergencyConfirmation = new();
    private readonly string _modulesDirectory;
    private readonly bool _suspendLifecycleHardwareTest;
    private readonly bool _gateDHardwareTest;
    private readonly bool _gateEHardwareTest;
    private readonly bool _gateF1HardwareTest;
    private readonly bool _gateF2HardwareTest;
    private readonly bool _gateG1HardwareTest;
    private readonly bool _gateG2HardwareTest;
    private readonly bool _m6ModernStandbyHardwareTest;
    private readonly bool _m7HibernationHardwareTest;
    private readonly bool _m9dProductionLifecycleHardwareTest;
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
    private int _gateGResumeProofState =
        (int)GateGResumeProofState.Pending;
    private int _gateGCurrentCycle = 1;
    private DateTimeOffset? _gateGSuspendBoundaryUtc;
    private string? _gateGSuspendSource;
    private bool _gateGSuspendWasCustom;
    private bool _gateGSuspendBackendAckVerified;
    private bool _gateGTelemetrySuspendedBeforeRestore;
    private DateTimeOffset? _gateGTelemetrySuspendMarkedAtUtc;

    private IntPtr _m6SessionDisplayRegistration;
    private IntPtr _m6SuspendResumeRegistration;
    private int _m6AdvanceGate;
    private bool _m6Armed;
    private bool _m6Completed;
    private bool _m6BackendAckVerified;
    private bool _m6SessionDisplayOff;
    private bool _m6DisplayOffObserved;
    private bool _m6PrimaryDisplayOffObserved;
    private bool _m6DisplayOnObserved;
    private bool _m6PbtSuspendObserved;
    private bool _m6ResumeAutomaticObservedWhileDisplayOff;
    private bool _m6ResumeSuspendObservedWhileDisplayOff;
    private bool _m6PreSleepRestoreVerified;
    private int _m6AcceptedUserResumeCount;
    private int _m6WatchdogPid;
    private long _m6WatchdogStartUtcTicks;
    private DateTimeOffset? _m6DisplayOffBoundaryUtc;
    private DateTimeOffset? _m6DisplayOnBoundaryUtc;
    private Task<double>? _m6PreSleepRestoreTask;
    private string? _m6DisplayOffSource;
    private bool _m6DisplayOffWasCustom;
    private bool _m6DisplayOffBackendAckVerified;
    private bool _m6PreSleepRestoreCompletionAttempted;

    private bool DisplayAware8C40LifecycleHardwareTest =>
        _m6ModernStandbyHardwareTest ||
        _m7HibernationHardwareTest ||
        _m9dProductionLifecycleHardwareTest;

    private string DisplayAwareLifecycleTransitionMode =>
        _m9dProductionLifecycleHardwareTest
            ? "m9d-production-modern-standby"
            : _m7HibernationHardwareTest
                ? "hibernation"
                : "modern-standby";

    private string DisplayAwareReadyPath =>
        _m9dProductionLifecycleHardwareTest
            ? M9DHardwareTestReadyPath
            : M6HardwareTestReadyPath;

    private string DisplayAwarePreSleepPath =>
        _m9dProductionLifecycleHardwareTest
            ? M9DPreSleepPath
            : M6PreSleepPath;

    private string DisplayAwareResumeGatePath =>
        _m9dProductionLifecycleHardwareTest
            ? M9DResumeGatePath
            : M6ResumeGatePath;

    private string DisplayAwareReentryPath =>
        _m9dProductionLifecycleHardwareTest
            ? M9DReentryPath
            : M6ReentryPath;

    private string DisplayAwareResultPath =>
        _m9dProductionLifecycleHardwareTest
            ? M9DHardwareTestResultPath
            : M6HardwareTestResultPath;

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
        bool gateG2HardwareTest = false,
        bool m6ModernStandbyHardwareTest = false,
        bool m7HibernationHardwareTest = false,
        bool m9dProductionLifecycleHardwareTest = false)
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
        _m6ModernStandbyHardwareTest = m6ModernStandbyHardwareTest;
        _m7HibernationHardwareTest = m7HibernationHardwareTest;
        _m9dProductionLifecycleHardwareTest = m9dProductionLifecycleHardwareTest;
        _hardwareIdentity = HardwareIdentityReader.ReadCurrent();
        _targetProfile =
            HpHardwareTargetResolver.Resolve(
                _hardwareIdentity,
                out _);

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

        if (DisplayAware8C40LifecycleHardwareTest)
        {
            Directory.CreateDirectory(SuspendHardwareTestRoot);
            // M7 deliberately reuses the already-hardened M6 marker transport;
            // transitionMode distinguishes hibernation from Modern Standby.
            TryDeleteFile(DisplayAwareReadyPath);
            TryDeleteFile(DisplayAwarePreSleepPath);
            TryDeleteFile(DisplayAwareResumeGatePath);
            TryDeleteFile(DisplayAwareReentryPath);
            TryDeleteFile(DisplayAwareResultPath);
        }

        IFanControlBackend backend;
        try
        {
            IFanControlWatchdogLeaseClient? watchdogLease = null;

            if (_gateF2HardwareTest)
            {
                watchdogLease =
                    new GateF2CommitHoldWatchdogLeaseClient(
                        new NamedPipeFanControlWatchdogLeaseClient(
                            Hp88F8TargetProfile.Instance.Id),
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
                    new NamedPipeFanControlWatchdogLeaseClient(
                            Hp88F8TargetProfile.Instance.Id);
            }

            if (DisplayAware8C40LifecycleHardwareTest)
            {
                watchdogLease =
                    new NamedPipeFanControlWatchdogLeaseClient(
                        Hp8C40TargetProfile.Instance.Id,
                        FanControlWatchdogLeaseContract.Hp8C40M4PipeName);

                if (_m9dProductionLifecycleHardwareTest)
                {
                    HpFanBackendSelection selection;

                    using (Hp8C40ProductionWatchdogGate
                               .EnterM9DPhysicalQualificationConstructionScope(
                                   _hardwareIdentity,
                                   Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationToken))
                    {
                        selection =
                            HpFanControlBackendFactory.Create(
                                modulesDirectory,
                                _hardwareIdentity,
                                watchdogLease);
                    }

                    if (Hp8C40ProductionWatchdogGate
                            .IsM9DPhysicalQualificationScopeActive)
                    {
                        throw new InvalidOperationException(
                            "M9D construction scope remained active after production factory/backend construction.");
                    }

                    backend = selection.Backend;
                    _fanBackendStartupDetail =
                        "Exact HP 8C40 M9D full-GUI lifecycle qualification selected through the normal production factory + public backend with M4 watchdog lease; full production promotion remains blocked.";
                }
                else
                {
                    backend =
                        Hp8C40FanControlBackend.CreateLifecycleQualificationBackend(
                            modulesDirectory,
                            watchdogLease,
                            Hp8C40FanControlBackend.LifecycleQualificationToken);

                    _fanBackendStartupDetail =
                        _m7HibernationHardwareTest
                            ? "Exact HP 8C40 M7 hibernation qualification backend selected with M4 watchdog lease; production factory remains blocked."
                            : "Exact HP 8C40 M6 Modern Standby qualification backend selected with M4 watchdog lease; production factory remains blocked.";
                }
            }
            else
            {
                // M9 preparation: the normal production path is wired to the
                // already-qualified HP 8C40 target-bound lease, but the gate
                // remains closed. With WatchdogRecoveryValidated=false and
                // ProductionConstructionAuthorized=false this returns null and
                // causes no service connection, lease acquisition or hardware
                // behavior change.
                watchdogLease ??=
                    Hp8C40ProductionWatchdogGate
                        .CreateLeaseIfAuthorized(
                            _hardwareIdentity);

                var selection = HpFanControlBackendFactory.Create(
                    modulesDirectory,
                    _hardwareIdentity,
                    watchdogLease);

                backend = selection.Backend;
                _fanBackendStartupDetail = selection.Detail;

                if ((_suspendLifecycleHardwareTest ||
                     _gateDHardwareTest ||
                     _gateEHardwareTest ||
                     _gateF1HardwareTest ||
                     _gateF2HardwareTest ||
                     _gateG1HardwareTest ||
                     _gateG2HardwareTest) &&
                    !string.Equals(
                        selection.TargetProfile?.Id,
                        Hp88F8TargetProfile.Instance.Id,
                        StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        "The legacy suspend/Gate D-G hardware harnesses are exact-target " +
                        "HP 88F8 / Legacy S3 validation only. They must never be reused " +
                        "on HP 8C40 Modern Standby; use the M-series gates instead.");
                }
            }
        }
        catch (Exception ex)
        {
            backend = new DisabledFanControlBackend();
            _fanBackendStartupDetail =
                $"HP fan backend initialization failed; fail-closed read-only fallback: {ex.Message}";
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
                    "GATE G1 TEST: full watchdog suspend/resume lifecycle mode enabled. The test requires durable OWNED 30/30 before suspend, prompt admission/telemetry fencing before blocking restore IO, validated Firmware + FF/FF + watchdog-release/journal-absent handoff before resume acceptance, the same watchdog PID across sleep, five-snapshot telemetry recovery, then one controlled post-resume re-entry and final firmware restore. Automatic policy remains OFF.");
            }

            if (_gateG2HardwareTest)
            {
                AppendEvent(
                    $"GATE G2 TEST: {GateG2TargetCycles} consecutive full watchdog suspend/resume cycles enabled in the same GUI and watchdog processes. Every cycle requires durable OWNED 30/30, prompt suspend fencing, completed Firmware + FF/FF + watchdog Ready/journal absent before resume acceptance, one accepted resume, five-snapshot Healthy recovery, one controlled 30/30 re-entry, and final Firmware restore. Automatic policy remains OFF.");
            }

            if (DisplayAware8C40LifecycleHardwareTest)
            {
                RegisterM6PowerNotifications();

                AppendEvent(
                    _m9dProductionLifecycleHardwareTest
                        ? "M9D PRODUCTION-PATH MODERN STANDBY TEST: exact HP 8C40 full GUI lifecycle mode enabled through the normal factory/public backend path. SESSION_DISPLAY_STATUS Off is the proactive release boundary; Custom may reopen only after display On plus fresh Healthy telemetry. Production promotion and automatic policy remain OFF."
                        : _m7HibernationHardwareTest
                            ? "M7 HIBERNATION TEST: exact HP 8C40 watchdog-backed lifecycle mode enabled. SESSION_DISPLAY_STATUS Off is the proactive release boundary; registered PBT_APMSUSPEND is the synchronous completion barrier; Custom may reopen only after SESSION_DISPLAY_STATUS On plus fresh Healthy telemetry. Automatic policy remains OFF."
                            : "M6 MODERN STANDBY TEST: exact HP 8C40 watchdog-backed lifecycle mode enabled. SESSION_DISPLAY_STATUS Off is the proactive release boundary; PBT resume notifications while display remains Off are observational only; Custom may reopen only after SESSION_DISPLAY_STATUS On plus fresh Healthy telemetry. Automatic policy remains OFF.");
            }

            _uiTimer.Start();
            _worker.Start();
            UpdateSafetyStatus();
        };

        FormClosing += OnFormClosingToTray;
        FormClosed += (_, _) =>
        {
            UnregisterM6PowerNotifications();
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

            if (DisplayAware8C40LifecycleHardwareTest)
            {
                if (code == PbtPowerSettingChange)
                {
                    HandleM6PowerSettingChange(m.LParam);
                }
                else
                {
                    HandleM6PowerBroadcast(code);
                }
            }
            else
            {
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
        }

        base.WndProc(ref m);
    }

    private void RegisterM6PowerNotifications()
    {
        if (!DisplayAware8C40LifecycleHardwareTest)
        {
            return;
        }

        if (_m6SessionDisplayRegistration != IntPtr.Zero ||
            _m6SuspendResumeRegistration != IntPtr.Zero)
        {
            if (_m6SessionDisplayRegistration != IntPtr.Zero &&
                _m6SuspendResumeRegistration != IntPtr.Zero)
            {
                return;
            }

            throw new InvalidOperationException(
                "M6 power-notification registration is partially initialized.");
        }

        _m6SuspendResumeRegistration =
            RegisterSuspendResumeNotification(
                Handle,
                DeviceNotifyWindowHandle);

        if (_m6SuspendResumeRegistration == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"M6 RegisterSuspendResumeNotification failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            var setting = GuidSessionDisplayStatus;

            _m6SessionDisplayRegistration =
                RegisterPowerSettingNotification(
                    Handle,
                    ref setting,
                    DeviceNotifyWindowHandle);

            if (_m6SessionDisplayRegistration == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"M6 RegisterPowerSettingNotification(GUID_SESSION_DISPLAY_STATUS) failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }
        catch
        {
            _ =
                UnregisterSuspendResumeNotification(
                    _m6SuspendResumeRegistration);

            _m6SuspendResumeRegistration = IntPtr.Zero;
            throw;
        }
    }

    private void UnregisterM6PowerNotifications()
    {
        if (_m6SessionDisplayRegistration != IntPtr.Zero)
        {
            _ =
                UnregisterPowerSettingNotification(
                    _m6SessionDisplayRegistration);

            _m6SessionDisplayRegistration = IntPtr.Zero;
        }

        if (_m6SuspendResumeRegistration != IntPtr.Zero)
        {
            _ =
                UnregisterSuspendResumeNotification(
                    _m6SuspendResumeRegistration);

            _m6SuspendResumeRegistration = IntPtr.Zero;
        }
    }
    private void HandleM6PowerSettingChange(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            AppendEvent(
                "M6: PBT_POWERSETTINGCHANGE arrived with a null payload; ignored fail-closed.");
            return;
        }

        try
        {
            var header =
                Marshal.PtrToStructure<PowerBroadcastSetting>(
                    data);

            if (header.PowerSetting !=
                    GuidSessionDisplayStatus ||
                header.DataLength < sizeof(uint))
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

            switch (value)
            {
                case 0:
                    _m6PrimaryDisplayOffObserved = true;
                    HandleM6DisplayOffBoundary(
                        "GUID_SESSION_DISPLAY_STATUS/Off",
                        primaryDisplaySignal: true);
                    break;

                case 1:
                    HandleM6SessionDisplayOn(
                        "GUID_SESSION_DISPLAY_STATUS/On");
                    break;

                default:
                    AppendEvent(
                        $"M6: SESSION_DISPLAY_STATUS value={value} observed; no lifecycle transition accepted.");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendEvent(
                $"M6: failed to parse SESSION_DISPLAY_STATUS notification: {ex.Message}");
            AppLog.Write(
                $"M6 power-setting parse failure: {ex}");
        }
    }

    private void HandleM6PowerBroadcast(int code)
    {
        switch (code)
        {
            case PbtApmSuspend:
                _m6PbtSuspendObserved = true;

                AppendEvent(
                    $"M6: registered PBT_APMSUSPEND observed; displayOff={_m6SessionDisplayOff}, primaryDisplayBoundary={_m6PrimaryDisplayOffObserved}, authority={_fanCoordinator.Authority}. Completing the already-started pre-sleep restore before returning from the suspend notification.");

                if (_m6Armed &&
                    !_m6DisplayOffObserved)
                {
                    // Safety fallback only. A physical M6 PASS still requires
                    // the earlier interactive-session Display Off boundary.
                    HandleM6DisplayOffBoundary(
                        "WM_POWERBROADCAST/PBT_APMSUSPEND fallback",
                        primaryDisplaySignal: false);
                }

                if (_m6Armed)
                {
                    CompleteM6PreSleepRestore(
                        "registered-WM_POWERBROADCAST/PBT_APMSUSPEND");
                }

                break;

            case PbtApmResumeAutomatic:
                if (_m6SessionDisplayOff)
                {
                    _m6ResumeAutomaticObservedWhileDisplayOff = true;
                    AppendEvent(
                        "M6: PBT_APMRESUMEAUTOMATIC observed while SESSION_DISPLAY_STATUS remains Off; telemetry/admission resume deliberately deferred.");
                }
                else
                {
                    AppendEvent(
                        "M6: PBT_APMRESUMEAUTOMATIC observed after display On; treated as observational duplicate.");
                }

                break;

            case PbtApmResumeSuspend:
                if (_m6SessionDisplayOff)
                {
                    _m6ResumeSuspendObservedWhileDisplayOff = true;
                    AppendEvent(
                        "M6: PBT_APMRESUMESUSPEND observed while SESSION_DISPLAY_STATUS remains Off; telemetry/admission resume deliberately deferred.");
                }
                else
                {
                    AppendEvent(
                        "M6: PBT_APMRESUMESUSPEND observed after display On; treated as observational duplicate.");
                }

                break;

            case PbtApmResumeCritical:
                AppendEvent(
                    $"M6: PBT_APMRESUMECRITICAL observed; displayOff={_m6SessionDisplayOff}. Admission remains lifecycle-fenced until user-visible display On plus Healthy telemetry.");
                break;
        }
    }

    private void HandleM6DisplayOffBoundary(
        string source,
        bool primaryDisplaySignal)
    {
        if (_m6Completed ||
            _m6DisplayOffObserved)
        {
            return;
        }

        _m6DisplayOffObserved = true;
        _m6SessionDisplayOff = true;
        _m6DisplayOffBoundaryUtc =
            DateTimeOffset.UtcNow;
        _m6DisplayOffSource = source;
        _m6DisplayOffWasCustom =
            _fanCoordinator.Authority ==
            FanAuthority.Custom;
        _m6DisplayOffBackendAckVerified =
            _m6BackendAckVerified;

        AppendEvent(
            $"M6: lifecycle boundary from {source}; primaryDisplaySignal={primaryDisplaySignal}; authority={_fanCoordinator.Authority}; backendAck={_m6DisplayOffBackendAckVerified}. Closing admission and pausing telemetry immediately; verified restore starts off the UI thread and registered PBT_APMSUSPEND is the synchronous pre-suspend completion barrier.");

        // Close admission and publish Suspended synchronously before any
        // potentially blocking hardware operation.
        _fanCoordinator.CloseCustomAdmissionForLifecycleBoundary();
        _worker.NotifySuspend(source);

        var boundary =
            _m6DisplayOffBoundaryUtc.Value;

        _m6PreSleepRestoreTask =
            Task.Run(
                async () =>
                {
                    var watch = Stopwatch.StartNew();

                    var quiesced =
                        await _worker.WaitForHardwareReadQuiescenceAsync(
                                TimeSpan.FromMilliseconds(750),
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    if (!quiesced)
                    {
                        throw new TimeoutException(
                            "M6 telemetry hardware activity did not quiesce within 750 ms after SESSION_DISPLAY_STATUS Off.");
                    }

                    await _fanCoordinator.BlockCustomAdmissionAndRestoreAsync(
                            $"M6 Modern Standby pre-suspend handoff ({source}).",
                            boundary,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    watch.Stop();
                    return watch.Elapsed.TotalMilliseconds;
                });
    }

    private void CompleteM6PreSleepRestore(string restoreTrigger)
    {
        if (_m6PreSleepRestoreCompletionAttempted)
        {
            return;
        }

        _m6PreSleepRestoreCompletionAttempted = true;

        var source =
            _m6DisplayOffSource ??
            "missing-display-off-boundary";

        try
        {
            var restoreTask =
                _m6PreSleepRestoreTask ??
                throw new InvalidOperationException(
                    "M6 suspend notification arrived without a pre-sleep restore task.");

            var restoreMs =
                restoreTask.GetAwaiter().GetResult();

            var restoreEvidence =
                _fanCoordinator.LastRestoreEvidence;

            var watchdog =
                M6WatchdogStateReader.Read();

            M6WatchdogStateReader.RequireReady(
                watchdog,
                _m6WatchdogPid == 0
                    ? null
                    : _m6WatchdogPid,
                _m6WatchdogStartUtcTicks == 0
                    ? null
                    : _m6WatchdogStartUtcTicks);

            var ec =
                ReadStableM6FirmwareAutoProof();

            _m6PreSleepRestoreVerified =
                _m6PrimaryDisplayOffObserved &&
                _m6Armed &&
                _m6DisplayOffWasCustom &&
                _m6DisplayOffBackendAckVerified &&
                _m6PbtSuspendObserved &&
                _fanCoordinator.Authority ==
                    FanAuthority.Firmware &&
                restoreEvidence is
                {
                    LocalFirmwareAckVerified: true,
                    WatchdogLeaseRequired: true,
                    WatchdogReleaseVerified: true
                } &&
                !watchdog.JournalPresent &&
                ec.Verified;

            var marker =
                $"{(_m6PreSleepRestoreVerified ? "PASS" : "FAIL")}|" +
                $"{DateTimeOffset.Now:O}|" +
                $"source={source}|" +
                $"displayOffAt={_m6DisplayOffBoundaryUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a"}|" +
                $"restoreTrigger={restoreTrigger}|" +
                $"primaryDisplaySignal={_m6PrimaryDisplayOffObserved}|" +
                $"wasCustom={_m6DisplayOffWasCustom}|" +
                $"backendAck={_m6DisplayOffBackendAckVerified}|" +
                $"authority={_fanCoordinator.Authority}|" +
                $"restoreMs={restoreMs.ToString("0.0", CultureInfo.InvariantCulture)}|" +
                $"localFirmwareAck={restoreEvidence?.LocalFirmwareAckVerified ?? false}|" +
                $"watchdogRelease={restoreEvidence?.WatchdogReleaseVerified ?? false}|" +
                $"watchdogPid={watchdog.ProcessId}|" +
                $"watchdogStartTicks={watchdog.ProcessStartUtcTicks}|" +
                $"journal={(watchdog.JournalPresent ? "PRESENT" : "absent")}|" +
                $"ec={ec.Cpu}/{ec.Gpu}|" +
                $"ecProof={(ec.Verified ? "stable-two-sample-FF/FF" : ec.Detail)}|" +
                $"pbtSuspendAlreadyObserved={_m6PbtSuspendObserved}|" +
                $"guiPid={Environment.ProcessId}";

            M6WatchdogStateReader.WriteDurableMarker(
                DisplayAwarePreSleepPath,
                marker);

            AppendEvent(
                _m6PreSleepRestoreVerified
                    ? $"M6: PRE-SLEEP RELEASE VERIFIED across session-display Off -> registered PBT_APMSUSPEND; restore work {restoreMs:0.0} ms; Firmware + stable FF/FF + watchdog Release + journal absent; watchdog PID {watchdog.ProcessId} unchanged."
                    : $"M6: PRE-SLEEP RELEASE FAILED validation; {marker}");
        }
        catch (Exception ex)
        {
            _m6PreSleepRestoreVerified = false;

            try
            {
                M6WatchdogStateReader.WriteDurableMarker(
                    DisplayAwarePreSleepPath,
                    $"FAIL|{DateTimeOffset.Now:O}|source={source}|displayOffAt={_m6DisplayOffBoundaryUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a"}|restoreTrigger={restoreTrigger}|exception={ex.Message}|guiPid={Environment.ProcessId}");
            }
            catch
            {
            }

            AppendEvent(
                $"M6: CRITICAL pre-sleep release verification failed: {ex.Message}");
            AppLog.Write(
                $"M6 pre-sleep release failure: {ex}");
        }
    }
    private void HandleM6SessionDisplayOn(string source)
    {
        if (_m6Completed)
        {
            return;
        }

        _m6SessionDisplayOff = false;

        if (!_m6DisplayOffObserved)
        {
            AppendEvent(
                "M6: SESSION_DISPLAY_STATUS On observed without a preceding M6 Off boundary; ignored.");
            return;
        }

        if (_m6DisplayOnObserved)
        {
            AppendEvent(
                "M6: duplicate SESSION_DISPLAY_STATUS On ignored.");
            return;
        }

        _m6DisplayOnObserved = true;
        _m6DisplayOnBoundaryUtc =
            DateTimeOffset.UtcNow;

        try
        {
            if (!_m6PreSleepRestoreVerified ||
                !_m6PrimaryDisplayOffObserved)
            {
                throw new InvalidOperationException(
                    "M6 cannot accept display-On resume because the proactive SESSION_DISPLAY_STATUS Off firmware handoff was not verified.");
            }

            var watchdog =
                M6WatchdogStateReader.Read();

            M6WatchdogStateReader.RequireReady(
                watchdog,
                _m6WatchdogPid,
                _m6WatchdogStartUtcTicks);

            if (watchdog.JournalPresent)
            {
                throw new InvalidOperationException(
                    $"M6 display-On resume requires journal absence; found '{watchdog.JournalPath}'.");
            }

            var ec =
                ReadStableM6FirmwareAutoProof();

            if (!ec.Verified)
            {
                throw new InvalidOperationException(
                    $"M6 display-On resume firmware baseline is not stable FF/FF: {ec.Detail}; last={ec.Cpu}/{ec.Gpu}.");
            }

            // Advance the freshness fence to the actual user-visible wake
            // boundary before allowing telemetry to resume. Maintenance PBT
            // notifications while display was Off never reached NotifyResume.
            _fanCoordinator.BlockCustomAdmissionAndRestoreAsync(
                    $"M6 user-visible resume gate ({source}).",
                    _m6DisplayOnBoundaryUtc.Value,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            var accepted =
                _worker.NotifyResume(source);

            if (!accepted)
            {
                throw new InvalidOperationException(
                    "M6 SESSION_DISPLAY_STATUS On was not accepted as the single telemetry resume boundary.");
            }

            _m6AcceptedUserResumeCount++;

            var marker =
                $"GATED|{DateTimeOffset.Now:O}|" +
                $"source={source}|" +
                $"acceptedUserResumes={_m6AcceptedUserResumeCount}|" +
                $"resumeAutomaticWhileOff={_m6ResumeAutomaticObservedWhileDisplayOff}|" +
                $"resumeSuspendWhileOff={_m6ResumeSuspendObservedWhileDisplayOff}|" +
                $"pbtSuspendObserved={_m6PbtSuspendObserved}|" +
                $"authority={_fanCoordinator.Authority}|" +
                $"ec={ec.Cpu}/{ec.Gpu}|" +
                $"watchdogPid={watchdog.ProcessId}|" +
                $"journal=absent|" +
                $"guiPid={Environment.ProcessId}";

            M6WatchdogStateReader.WriteDurableMarker(
                DisplayAwareResumeGatePath,
                marker);

            AppendEvent(
                "M6: SESSION_DISPLAY_STATUS On accepted as the sole user-facing resume boundary. Admission remains closed until five-snapshot Healthy telemetry recovery and watchdog Ready/journal-absent revalidation.");
        }
        catch (Exception ex)
        {
            CompleteM6ModernStandbyHardwareTest(
                success: false,
                exitCode: 131,
                message:
                    $"Display-On resume gate failed: {ex.Message}");
        }
    }

    private (bool Verified, byte Cpu, byte Gpu, int Samples, string Detail)
        ReadStableM6FirmwareAutoProof(
            int maxSamples = 8)
    {
        var probe =
            new Hp8C40EcControlStateProbe(
                _modulesDirectory);

        var consecutiveFirmwareAuto = 0;
        var unexpectedCpu = -1;
        var unexpectedGpu = -1;
        var unexpectedSamples = 0;
        byte lastCpu = 0;
        byte lastGpu = 0;

        for (var sample = 1;
             sample <= maxSamples;
             sample++)
        {
            var setpoint =
                probe.ReadSetpoint();

            lastCpu = setpoint.CpuSetpoint;
            lastGpu = setpoint.GpuSetpoint;

            if (lastCpu == byte.MaxValue &&
                lastGpu == byte.MaxValue)
            {
                consecutiveFirmwareAuto++;
                unexpectedCpu = -1;
                unexpectedGpu = -1;
                unexpectedSamples = 0;

                if (consecutiveFirmwareAuto >= 2)
                {
                    return (
                        true,
                        lastCpu,
                        lastGpu,
                        sample,
                        "two consecutive FF/FF samples");
                }
            }
            else
            {
                consecutiveFirmwareAuto = 0;

                if (lastCpu == unexpectedCpu &&
                    lastGpu == unexpectedGpu)
                {
                    unexpectedSamples++;
                }
                else
                {
                    unexpectedCpu = lastCpu;
                    unexpectedGpu = lastGpu;
                    unexpectedSamples = 1;
                }

                if (unexpectedSamples >= 2)
                {
                    return (
                        false,
                        lastCpu,
                        lastGpu,
                        sample,
                        $"unexpected setpoint {lastCpu}/{lastGpu} repeated twice");
                }
            }

            if (sample < maxSamples)
            {
                Thread.Sleep(75);
            }
        }

        return (
            false,
            lastCpu,
            lastGpu,
            maxSamples,
            "stable FF/FF was not observed twice consecutively");
    }


    private void HandleSuspendLifecycle(string source)
    {
        var boundary = DateTimeOffset.UtcNow;

        var testWasCustom = false;
        var backendAckVerified = false;
        DateTimeOffset? armedAt = null;

        if (_suspendLifecycleHardwareTest &&
            _suspendHardwareTestArmed &&
            !_suspendHardwareTestCompleted)
        {
            _suspendHardwareTestSuspendObserved = true;
            testWasCustom = _fanCoordinator.Authority == FanAuthority.Custom;
            backendAckVerified = _suspendHardwareTestBackendAckVerified;
            armedAt = _suspendHardwareTestArmedAt;

            var armedAge = armedAt.HasValue
                ? Math.Max(0, (boundary - armedAt.Value).TotalSeconds)
                : double.NaN;

            AppendEvent(
                $"SUSPEND TEST: suspend event entered with authority={_fanCoordinator.Authority}; " +
                $"validatedBackendAck={backendAckVerified}; expectedOwnedSetpoint={SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; " +
                $"armedAge={(double.IsNaN(armedAge) ? "n/a" : $"{armedAge:0.000}s")}.");
        }

        if (GateGHardwareTest &&
            _gateG1HardwareTestArmed &&
            !_gateG1HardwareTestCompleted)
        {
            _gateG1HardwareTestSuspendObserved = true;
            _gateGSuspendBoundaryUtc = boundary;
            _gateGSuspendSource = source;
            _gateGSuspendWasCustom =
                _fanCoordinator.Authority == FanAuthority.Custom;
            _gateGSuspendBackendAckVerified =
                _gateG1HardwareTestBackendAckVerified;

            var armedAge = _gateG1HardwareTestArmedAt.HasValue
                ? Math.Max(
                    0,
                    (boundary - _gateG1HardwareTestArmedAt.Value).TotalSeconds)
                : double.NaN;

            AppendEvent(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: PBT_APMSUSPEND entered with authority={_fanCoordinator.Authority}; " +
                $"validatedBackendAck={_gateGSuspendBackendAckVerified}; expectedOwnedSetpoint={SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; " +
                $"watchdogPid={_gateG1WatchdogPid}; armedAge={(double.IsNaN(armedAge) ? "n/a" : $"{armedAge:0.000}s")}. " +
                "Lifecycle admission is fenced and telemetry is marked Suspended before restore IO starts.");
        }

        // Close the lifecycle fence synchronously before changing telemetry
        // state. This is intentionally separate from the restore await so
        // stale pre-boundary safety cannot reacquire Custom in the gap.
        _fanCoordinator.CloseCustomAdmissionForLifecycleBoundary();

        // Physical S3 testing proved that Windows may freeze the UI thread
        // immediately after the critical restore completes, before an outer
        // async caller continuation/finally gets CPU time. Therefore the
        // telemetry suspend boundary must be established before restore IO.
        _worker.NotifySuspend(source);

        if (GateGHardwareTest &&
            _gateG1HardwareTestArmed &&
            !_gateG1HardwareTestCompleted)
        {
            _gateGTelemetrySuspendMarkedAtUtc = DateTimeOffset.UtcNow;
            _gateGTelemetrySuspendedBeforeRestore =
                _worker.StateMachine.State == SystemState.Suspended;
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
            // Legacy bounded suspend validation keeps its historical direct EC
            // proof. Gate G does not use this mode or this out-of-band reader.
            if (_suspendLifecycleHardwareTest &&
                _suspendHardwareTestArmed &&
                !_suspendHardwareTestCompleted)
            {
                try
                {
                    var after =
                        new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

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
        }
    }

    private void HandleResumeLifecycle(string source)
    {
        var boundary = DateTimeOffset.UtcNow;

        // Gate G no longer requires the entire HP/WMI/watchdog restore to finish
        // before physical S3. Windows documents only an approximately two-second
        // PBT_APMSUSPEND handling window and may freeze the caller while restore
        // IO is still in progress. The hard safety boundary is therefore:
        //   1. admission fenced + telemetry Suspended before blocking restore IO;
        //   2. the durable restore transaction must finish before this process
        //      accepts the first resume into telemetry recovery.
        //
        // Because WM_POWERBROADCAST is delivered on this same UI thread, this
        // method cannot run until the synchronous suspend handler has returned.
        // Verify the completed restore and watchdog/journal state now, while the
        // telemetry state is still Suspended.
        if (GateGHardwareTest &&
            _gateG1HardwareTestArmed &&
            !_gateG1HardwareTestCompleted)
        {
            if (!EnsureGateGHandoffVerifiedBeforeResumeAcceptance(
                    boundary,
                    source))
            {
                // Fail closed: while the one-shot causal proof is still being
                // verified, or after it has failed, do not let another Windows
                // resume notification advance telemetry or mutate the marker.
                return;
            }

            if (_gateG1HardwareTestResumeObserved)
            {
                // PBT_APMRESUMEAUTOMATIC and PBT_APMRESUMESUSPEND can both
                // describe one Windows wake. Once this Gate G cycle accepted
                // exactly one resume, later notifications are observational
                // duplicates: never route them back through proof/telemetry.
                AppendEvent(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: duplicate resume signal ignored after the cycle already accepted one resume: {source}.");
                return;
            }
        }

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

    private bool EnsureGateGHandoffVerifiedBeforeResumeAcceptance(
        DateTimeOffset resumeBoundary,
        string resumeSource)
    {
        while (true)
        {
            var proofState =
                (GateGResumeProofState)Volatile.Read(
                    ref _gateGResumeProofState);

            switch (proofState)
            {
                case GateGResumeProofState.Verified:
                    return true;

                case GateGResumeProofState.Failed:
                    return false;

                case GateGResumeProofState.Verifying:
                    // Re-entrant/duplicate Windows resume delivery must never
                    // run a second proof while the first proof still owns the
                    // causal boundary. Keep telemetry Suspended and let the
                    // first verifier finish.
                    AppLog.Write(
                        $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: resume signal ignored while handoff proof is already Verifying: {resumeSource}.");
                    return false;

                case GateGResumeProofState.Pending:
                    if (Interlocked.CompareExchange(
                            ref _gateGResumeProofState,
                            (int)GateGResumeProofState.Verifying,
                            (int)GateGResumeProofState.Pending) !=
                        (int)GateGResumeProofState.Pending)
                    {
                        continue;
                    }

                    try
                    {
                        var verified =
                            VerifyGateGHandoffBeforeResumeAcceptance(
                                resumeBoundary,
                                resumeSource);

                        Volatile.Write(
                            ref _gateGResumeProofState,
                            (int)(verified
                                ? GateGResumeProofState.Verified
                                : GateGResumeProofState.Failed));

                        return verified;
                    }
                    catch
                    {
                        // The proof method is already fail-closed, but latch an
                        // unexpected escaping exception too so a later duplicate
                        // resume can never retry and turn an uncertain cycle into
                        // a PASS.
                        Volatile.Write(
                            ref _gateGResumeProofState,
                            (int)GateGResumeProofState.Failed);
                        throw;
                    }

                default:
                    Volatile.Write(
                        ref _gateGResumeProofState,
                        (int)GateGResumeProofState.Failed);
                    return false;
            }
        }
    }

    private bool VerifyGateGHandoffBeforeResumeAcceptance(
        DateTimeOffset resumeBoundary,
        string resumeSource)
    {
        var suspendBoundary = _gateGSuspendBoundaryUtc;
        var suspendSource = _gateGSuspendSource;

        if (!suspendBoundary.HasValue ||
            string.IsNullOrWhiteSpace(suspendSource))
        {
            _gateG1HardwareTestPreSleepVerified = false;

            try
            {
                GateG1WatchdogStateReader.WriteDurableMarker(
                    GateGPreSleepPath,
                    $"FAIL|{DateTimeOffset.UtcNow:O}|cycle={_gateGCurrentCycle}/{GateGTargetCycleCount}|resumeSource={resumeSource}|missingSuspendContext=True");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"{GateGLabel}: could not persist missing-context suspend-handoff marker: {markerEx}");
            }

            AppendEvent(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: suspend handoff proof failed because suspend context was not captured.");
            return false;
        }

        try
        {
            var restoreEvidence =
                _fanCoordinator.LastRestoreEvidence;
            var firmwareAt =
                _fanCoordinator.LastFirmwareAuthorityAtUtc;
            var telemetryMarkedAt =
                _gateGTelemetrySuspendMarkedAtUtc;
            var watchdog =
                GateG1WatchdogStateReader.Read();

            GateG1WatchdogStateReader.RequireReady(
                watchdog,
                _gateG1WatchdogPid);

            var restoreEvidenceFresh =
                restoreEvidence.HasValue &&
                restoreEvidence.Value.CompletedAtUtc >= suspendBoundary.Value;
            var firmwareTransitionFresh =
                firmwareAt.HasValue &&
                firmwareAt.Value >= suspendBoundary.Value;
            var telemetryTransitionFresh =
                telemetryMarkedAt.HasValue &&
                telemetryMarkedAt.Value >= suspendBoundary.Value;

            var restoreMs =
                restoreEvidenceFresh
                    ? Math.Max(
                        0,
                        (restoreEvidence!.Value.CompletedAtUtc -
                         suspendBoundary.Value).TotalMilliseconds)
                    : double.PositiveInfinity;
            var firmwareMs =
                firmwareTransitionFresh
                    ? Math.Max(
                        0,
                        (firmwareAt!.Value -
                         suspendBoundary.Value).TotalMilliseconds)
                    : double.PositiveInfinity;
            var telemetryMs =
                telemetryTransitionFresh
                    ? Math.Max(
                        0,
                        (telemetryMarkedAt!.Value -
                         suspendBoundary.Value).TotalMilliseconds)
                    : double.PositiveInfinity;

            var localFirmwareAckVerified =
                restoreEvidenceFresh &&
                restoreEvidence!.Value.LocalFirmwareAckVerified;
            var watchdogReleaseVerified =
                restoreEvidenceFresh &&
                restoreEvidence!.Value.WatchdogLeaseRequired &&
                restoreEvidence.Value.WatchdogReleaseVerified;
            var firmwareAuthorityCurrent =
                _fanCoordinator.Authority == FanAuthority.Firmware;
            var telemetryStillSuspended =
                _worker.StateMachine.State == SystemState.Suspended;
            var noAcceptedResumeYet =
                _gateG1AcceptedResumeCount == 0 &&
                !_gateG1HardwareTestResumeObserved;

            // Only the non-blocking pre-restore boundary work is held to the
            // PBT_APMSUSPEND budget. Restore itself may legitimately span S3;
            // the durable lease plus sleep-excluding watchdog clock make that
            // interruption safe. It still must be fully completed and journal-
            // free before the first resume is accepted into telemetry recovery.
            _gateG1HardwareTestPreSleepVerified =
                _gateGSuspendWasCustom &&
                _gateGSuspendBackendAckVerified &&
                _gateGTelemetrySuspendedBeforeRestore &&
                telemetryTransitionFresh &&
                telemetryStillSuspended &&
                telemetryMs <= GateGSuspendProofBudgetMs &&
                localFirmwareAckVerified &&
                watchdogReleaseVerified &&
                firmwareTransitionFresh &&
                firmwareAuthorityCurrent &&
                noAcceptedResumeYet &&
                !watchdog.JournalPresent;

            var marker =
                $"{(_gateG1HardwareTestPreSleepVerified ? "PASS" : "FAIL")}|{DateTimeOffset.UtcNow:O}|" +
                $"cycle={_gateGCurrentCycle}/{GateGTargetCycleCount}|" +
                $"source={suspendSource}|resumeSource={resumeSource}|" +
                $"handoffProof=completed-before-resume-acceptance|" +
                $"wasCustom={_gateGSuspendWasCustom}|backendAck={_gateGSuspendBackendAckVerified}|" +
                $"authority={_fanCoordinator.Authority}|ec=255/255|ecProof=production-backend-restore-ack|" +
                $"watchdogRelease={watchdogReleaseVerified}|watchdogState=Ready|" +
                $"journal={(watchdog.JournalPresent ? "PRESENT" : "absent")}|" +
                $"journalProof=watchdog-release-response+post-resume-ready-check|" +
                $"telemetry={_worker.StateMachine.State}|telemetryProof=pre-restore-state-transition|" +
                $"acceptedResumesBeforeProof={_gateG1AcceptedResumeCount}|" +
                $"telemetryMs={FormatInvariantMs(telemetryMs)}|" +
                $"restoreMs={FormatInvariantMs(restoreMs)}|" +
                $"firmwareMs={FormatInvariantMs(firmwareMs)}|" +
                $"preBlockMs={FormatInvariantMs(telemetryMs)}|budgetMs={GateGSuspendProofBudgetMs:0}|" +
                $"watchdogPid={watchdog.ProcessId}|guiPid={Environment.ProcessId}";

            GateG1WatchdogStateReader.WriteDurableMarker(
                GateGPreSleepPath,
                marker);

            AppendEvent(
                _gateG1HardwareTestPreSleepVerified
                    ? $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: SUSPEND HANDOFF VERIFIED before resume acceptance; telemetry was fenced/Suspended in {FormatInvariantMs(telemetryMs)} ms, restore completed with local FF/FF + watchdog Release, watchdog PID {watchdog.ProcessId} is Ready and journal absent. restoreMs={FormatInvariantMs(restoreMs)}, firmwareMs={FormatInvariantMs(firmwareMs)}."
                    : $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: SUSPEND HANDOFF VERIFICATION FAILED; {marker}");

            return _gateG1HardwareTestPreSleepVerified;
        }
        catch (Exception ex)
        {
            _gateG1HardwareTestPreSleepVerified = false;

            try
            {
                GateG1WatchdogStateReader.WriteDurableMarker(
                    GateGPreSleepPath,
                    $"FAIL|{DateTimeOffset.UtcNow:O}|cycle={_gateGCurrentCycle}/{GateGTargetCycleCount}|source={suspendSource}|resumeSource={resumeSource}|verificationException={ex.Message}");
            }
            catch (Exception markerEx)
            {
                AppLog.Write(
                    $"{GateGLabel}: could not write failed suspend-handoff marker: {markerEx}");
            }

            AppendEvent(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: SUSPEND HANDOFF VERIFICATION FAILED before resume acceptance: {ex.Message}");
            return false;
        }
    }

    private static string FormatInvariantMs(double value) =>
        double.IsFinite(value)
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : "inf";

    private async Task ReopenFanAdmissionAfterHealthyAsync()
    {
        // Interim 8C40 fail-closed rule after M0: PBT_APMRESUMEAUTOMATIC can
        // occur during a maintenance wake while SESSION_DISPLAY_STATUS remains
        // Off. Until the M-series display-aware lifecycle gate is integrated,
        // generic S3-era Healthy recovery must never reopen Custom admission on
        // a Modern Standby target. Restarting the app is preferable to silently
        // granting authority during a screen-off maintenance wake.
        if (_targetProfile?.SleepModel ==
            WindowsSleepModel.ModernStandbyS0LowPowerIdle)
        {
            Ui(() => AppendEvent(
                "Modern Standby target: generic S3-era lifecycle recovery will not reopen Custom admission. " +
                "M-series display-aware lifecycle qualification is still required."));
            return;
        }

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
        var targetDescription =
            _targetProfile is null
                ? "No validated HP hardware target is active."
                : $"Validated target: {_targetProfile.DisplayName} ({_targetProfile.Id}).";

        fanCurve.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text =
                targetDescription +
                "\r\nBackend is integrated behind FanControlCoordinator." +
                "\r\nAutomatic fan policy is intentionally OFF; no curve commands are issued by this GUI yet.",
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
            ("Package / hottest core", _cpuTemperature),
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



    private SafetyGateResult EvaluateControlSafety(
        HardwareIdentity hardware,
        SystemState state,
        TelemetrySnapshot? snapshot,
        DateTimeOffset now,
        bool fanWritePathPresent = false)
    {
        var raw = SafetyGate.Evaluate(
            hardware,
            state,
            snapshot,
            now,
            fanWritePathPresent);

        return _thermalEmergencyConfirmation.Apply(
            hardware,
            snapshot,
            raw);
    }

    private SafetyGateResult EvaluateDisplaySafety(
        HardwareIdentity hardware,
        SystemState state,
        TelemetrySnapshot? snapshot,
        DateTimeOffset now,
        bool fanWritePathPresent = false)
    {
        var raw = SafetyGate.EvaluateForDisplay(
            hardware,
            state,
            snapshot,
            now,
            fanWritePathPresent);

        return _thermalEmergencyConfirmation.Preview(
            hardware,
            snapshot,
            raw);
    }

    private async Task EnforceLatestFanSafetyAsync(string reason)
    {
        var result = EvaluateControlSafety(
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
            _cpuTemperature.Text =
                $"{Format(snapshot.CpuTemperatureC, "°C")} / {Format(snapshot.CpuCoreMaxTemperatureC, "°C")}";
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

        if (DisplayAware8C40LifecycleHardwareTest)
        {
            // M6/M7 own the display-aware admission-reopen ordering. M6
            // additionally proves maintenance-resume deferral while display is
            // Off; M7 proves actual hibernation in its parent harness.
            await AdvanceM6ModernStandbyHardwareTestAsync();
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

    private async Task AdvanceM6ModernStandbyHardwareTestAsync()
    {
        if (_m6Completed ||
            Interlocked.CompareExchange(
                ref _m6AdvanceGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            if (!_m6Armed)
            {
                if (_m6DisplayOffObserved)
                {
                    throw new InvalidOperationException(
                        "M6 observed a lifecycle display-off boundary before the watchdog-owned 30/30 READY state was armed.");
                }

                await ArmM6ModernStandbyHardwareTestAsync();
                return;
            }

            if (!_m6DisplayOnObserved)
            {
                return;
            }

            if (!_m6PrimaryDisplayOffObserved ||
                !_m6PreSleepRestoreVerified)
            {
                throw new InvalidOperationException(
                    "M6 post-resume continuation requires a verified proactive SESSION_DISPLAY_STATUS Off handoff.");
            }

            if (!_m6PbtSuspendObserved)
            {
                throw new InvalidOperationException(
                    "M6 did not observe PBT_APMSUSPEND for the armed Modern Standby cycle.");
            }

            if ((_m6ModernStandbyHardwareTest ||
                 _m9dProductionLifecycleHardwareTest) &&
                !_m6ResumeAutomaticObservedWhileDisplayOff &&
                !_m6ResumeSuspendObservedWhileDisplayOff)
            {
                throw new InvalidOperationException(
                    "M6 did not exercise a PBT resume notification while SESSION_DISPLAY_STATUS remained Off; maintenance/user-wake deferral was not physically proven.");
            }

            if (_m6AcceptedUserResumeCount != 1)
            {
                throw new InvalidOperationException(
                    $"M6 requires exactly one user-facing display-On telemetry resume; observed {_m6AcceptedUserResumeCount}.");
            }

            if (_worker.StateMachine.State != SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"M6 post-resume continuation requires Healthy telemetry; state={_worker.StateMachine.State}.");
            }

            var watchdog =
                M6WatchdogStateReader.Read();

            M6WatchdogStateReader.RequireReady(
                watchdog,
                _m6WatchdogPid,
                _m6WatchdogStartUtcTicks);

            if (watchdog.JournalPresent)
            {
                throw new InvalidOperationException(
                    $"M6 post-resume watchdog is not clean; journal remains at '{watchdog.JournalPath}'.");
            }

            var firmwareProof =
                ReadStableM6FirmwareAutoProof();

            if (!firmwareProof.Verified ||
                _fanCoordinator.Authority !=
                    FanAuthority.Firmware)
            {
                throw new InvalidOperationException(
                    $"M6 post-resume firmware baseline invalid: authority={_fanCoordinator.Authority}, EC={firmwareProof.Cpu}/{firmwareProof.Gpu}, detail={firmwareProof.Detail}.");
            }

            var recoveryTimestamp =
                _lastSnapshot?.Timestamp ??
                throw new InvalidOperationException(
                    "M6 Healthy state has no validated post-resume telemetry snapshot.");

            if (!_m6DisplayOnBoundaryUtc.HasValue ||
                recoveryTimestamp <=
                    _m6DisplayOnBoundaryUtc.Value)
            {
                throw new InvalidOperationException(
                    $"M6 Healthy snapshot {recoveryTimestamp:O} is not newer than display-On boundary {_m6DisplayOnBoundaryUtc:O}.");
            }

            var reopened =
                await _fanCoordinator
                    .AllowCustomAdmissionAfterRecoveryAsync(
                        recoveryTimestamp,
                        "M6: user-visible display On + fresh five-snapshot Healthy recovery + watchdog Ready/journal absent.",
                        CancellationToken.None);

            if (!reopened)
            {
                throw new InvalidOperationException(
                    "M6 lifecycle fence refused to reopen after validated display-aware recovery.");
            }

            AppendEvent(
                $"M6: admission reopened only after display-On boundary and Healthy snapshot {recoveryTimestamp:O}; watchdog PID {_m6WatchdogPid} remained unchanged.");

            var reentered =
                await TryEnterM6CustomAuthorityAsync(
                    "controlled post-resume re-entry");

            if (!reentered ||
                _fanCoordinator.Authority !=
                    FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "M6 could not reacquire watchdog-protected Custom authority after validated user-visible recovery.");
            }

            await ApplyM6CommandWithFreshSafetyAsync(
                new FanCommand(
                    SuspendHardwareTestLevel,
                    SuspendHardwareTestLevel,
                    "M6 controlled post-resume 30/30 re-entry"),
                "controlled post-resume 30/30 re-entry");

            var processStartTicks =
                GetCurrentProcessStartUtcTicks();

            var watchdogOwned =
                M6WatchdogStateReader.Read();

            M6WatchdogStateReader.RequireReady(
                watchdogOwned,
                _m6WatchdogPid,
                _m6WatchdogStartUtcTicks);

            M6WatchdogStateReader.RequireOwned30(
                watchdogOwned,
                Environment.ProcessId,
                processStartTicks);

            M6WatchdogStateReader.WriteDurableMarker(
                DisplayAwareReentryPath,
                $"REENTRY|{DateTimeOffset.Now:O}|" +
                $"authority={_fanCoordinator.Authority}|" +
                $"cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|" +
                $"ack=backend-ec+tachs+watchdog-owned|" +
                $"watchdogPid={_m6WatchdogPid}|" +
                $"watchdogStartTicks={_m6WatchdogStartUtcTicks}|" +
                $"guiPid={Environment.ProcessId}|" +
                $"guiStartTicks={processStartTicks}");

            await _fanCoordinator.RestoreFirmwareAsync(
                "M6 controlled post-resume re-entry complete; return authority to HP firmware.",
                CancellationToken.None);

            var finalRestore =
                _fanCoordinator.LastRestoreEvidence;

            var watchdogFinal =
                M6WatchdogStateReader.Read();

            M6WatchdogStateReader.RequireReady(
                watchdogFinal,
                _m6WatchdogPid,
                _m6WatchdogStartUtcTicks);

            var finalEc =
                ReadStableM6FirmwareAutoProof();

            if (_fanCoordinator.Authority !=
                    FanAuthority.Firmware ||
                finalRestore is not
                {
                    LocalFirmwareAckVerified: true,
                    WatchdogLeaseRequired: true,
                    WatchdogReleaseVerified: true
                } ||
                watchdogFinal.JournalPresent ||
                !finalEc.Verified)
            {
                throw new InvalidOperationException(
                    $"M6 final handoff invalid: authority={_fanCoordinator.Authority}, localAck={finalRestore?.LocalFirmwareAckVerified ?? false}, watchdogRelease={finalRestore?.WatchdogReleaseVerified ?? false}, journal={(watchdogFinal.JournalPresent ? "PRESENT" : "absent")}, EC={finalEc.Cpu}/{finalEc.Gpu}, detail={finalEc.Detail}.");
            }

            CompleteM6ModernStandbyHardwareTest(
                success: true,
                exitCode: 0,
                message:
                    _m9dProductionLifecycleHardwareTest
                        ? $"M9D normal production factory/public backend path: SESSION_DISPLAY_STATUS Off proactively restored Firmware + stable FF/FF before Modern Standby; PBT resume while display Off was deferred; SESSION_DISPLAY_STATUS On was the only accepted resume; five-snapshot telemetry recovered Healthy; watchdog PID {_m6WatchdogPid} stayed stable; controlled post-resume 30/30 re-entry and final watchdog Release succeeded; journal absent; final EC FF/FF."
                        : _m7HibernationHardwareTest
                            ? $"SESSION_DISPLAY_STATUS Off proactively restored Firmware + stable FF/FF before Hibernation; SESSION_DISPLAY_STATUS On was the only accepted telemetry resume boundary; five-snapshot telemetry recovered Healthy; watchdog PID {_m6WatchdogPid} stayed stable; controlled post-resume 30/30 re-entry and final watchdog Release succeeded; journal absent; final EC FF/FF."
                            : $"SESSION_DISPLAY_STATUS Off proactively restored Firmware + stable FF/FF before Modern Standby; PBT resume while display Off was deferred; SESSION_DISPLAY_STATUS On was the only accepted resume; five-snapshot telemetry recovered Healthy; watchdog PID {_m6WatchdogPid} stayed stable; controlled post-resume 30/30 re-entry and final watchdog Release succeeded; journal absent; final EC FF/FF.");
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    "M6 hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"M6: cleanup restore also failed: {restoreEx}");
            }

            CompleteM6ModernStandbyHardwareTest(
                success: false,
                exitCode: 132,
                message:
                    $"FAIL: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(
                ref _m6AdvanceGate,
                0);
        }
    }

    private async Task ArmM6ModernStandbyHardwareTestAsync()
    {
        if (_m6SessionDisplayOff)
        {
            throw new InvalidOperationException(
                "M6 refuses to arm Custom authority while SESSION_DISPLAY_STATUS is Off.");
        }

        var snapshot =
            _lastSnapshot ??
            throw new InvalidOperationException(
                "M6 has no telemetry snapshot for initial admission.");

        EnsureSuspendHardwareTestLightLoad(
            snapshot);

        var watchdog =
            M6WatchdogStateReader.Read();

        M6WatchdogStateReader.RequireReady(
            watchdog);

        if (watchdog.JournalPresent)
        {
            throw new InvalidOperationException(
                $"M6 initial baseline requires no durable journal; found '{watchdog.JournalPath}'.");
        }

        _m6WatchdogPid =
            watchdog.ProcessId;
        _m6WatchdogStartUtcTicks =
            watchdog.ProcessStartUtcTicks;

        var firmwareProof =
            ReadStableM6FirmwareAutoProof();

        if (!firmwareProof.Verified)
        {
            throw new InvalidOperationException(
                $"M6 initial firmware baseline is not stable FF/FF: {firmwareProof.Detail}; last={firmwareProof.Cpu}/{firmwareProof.Gpu}.");
        }

        var entered =
            await TryEnterM6CustomAuthorityAsync(
                "initial admission");

        if (!entered ||
            _fanCoordinator.Authority !=
                FanAuthority.Custom)
        {
            throw new InvalidOperationException(
                "M6 coordinator did not grant watchdog-protected Custom authority.");
        }

        await ApplyM6CommandWithFreshSafetyAsync(
            new FanCommand(
                SuspendHardwareTestLevel,
                SuspendHardwareTestLevel,
                "M6 initial watchdog-protected Modern Standby lifecycle qualification"),
            "initial 30/30 command");

        var processStartTicks =
            GetCurrentProcessStartUtcTicks();

        var ownedWatchdog =
            M6WatchdogStateReader.Read();

        M6WatchdogStateReader.RequireReady(
            ownedWatchdog,
            _m6WatchdogPid,
            _m6WatchdogStartUtcTicks);

        M6WatchdogStateReader.RequireOwned30(
            ownedWatchdog,
            Environment.ProcessId,
            processStartTicks);

        _m6BackendAckVerified = true;
        _m6Armed = true;

        M6WatchdogStateReader.WriteDurableMarker(
            DisplayAwareReadyPath,
            $"READY|{DateTimeOffset.Now:O}|" +
            $"authority={_fanCoordinator.Authority}|" +
            $"cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|" +
            $"ack=backend-ec+tachs+watchdog-owned|" +
            $"journal=Owned30|" +
            $"watchdogPid={_m6WatchdogPid}|" +
            $"watchdogStartTicks={_m6WatchdogStartUtcTicks}|" +
            $"guiPid={Environment.ProcessId}|" +
            $"guiStartTicks={processStartTicks}|" +
            $"transitionMode={DisplayAwareLifecycleTransitionMode}|" +
            $"resumePolicy=session-display-on-only");

        AppendEvent(
            _m7HibernationHardwareTest
                ? $"M7: READY at 30/30 with durable OWNED lease. Watchdog PID={_m6WatchdogPid}; GUI PID={Environment.ProcessId}. Parent harness will request Windows hibernation after explicit operator confirmation; SESSION_DISPLAY_STATUS Off must release firmware authority first."
                : $"M6: READY at 30/30 with durable OWNED lease. Watchdog PID={_m6WatchdogPid}; GUI PID={Environment.ProcessId}. Use Windows Start -> Power -> Sleep. SESSION_DISPLAY_STATUS Off must release firmware authority before the Modern Standby transition.");
    }

    private async Task<bool> TryEnterM6CustomAuthorityAsync(
        string phase)
    {
        const int maxAttempts = 5;

        for (var attempt = 1;
             attempt <= maxAttempts;
             attempt++)
        {
            if (_worker.StateMachine.State !=
                SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"M6 {phase} requires Healthy telemetry; state={_worker.StateMachine.State}.");
            }

            var snapshot =
                _lastSnapshot ??
                throw new InvalidOperationException(
                    $"M6 {phase} has no telemetry snapshot.");

            EnsureSuspendHardwareTestLightLoad(
                snapshot);

            var safety =
                EvaluateControlSafety(
                    _hardwareIdentity,
                    _worker.StateMachine.State,
                    snapshot,
                    DateTimeOffset.UtcNow,
                    fanWritePathPresent:
                        _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    $"SafetyGate refused M6 {phase}: " +
                    string.Join(" | ", safety.Reasons));
            }

            try
            {
                var entered =
                    await _fanCoordinator.TryEnterCustomAsync(
                        safety,
                        CancellationToken.None);

                if (entered &&
                    _fanCoordinator.Authority ==
                        FanAuthority.Custom)
                {
                    return true;
                }

                if (_fanCoordinator
                    .IsSafetyEvaluationCurrent(safety))
                {
                    return false;
                }

                AppendEvent(
                    $"M6 {phase}: SafetyGate sequence {safety.EvaluationSequence} was superseded; retry {attempt}/{maxAttempts}.");

                await Task.Yield();
            }
            catch (FanControlOwnershipConflictException ex)
                when (ex.Message.Contains(
                    "EC setpoint=",
                    StringComparison.Ordinal))
            {
                var firmwareProof =
                    ReadStableM6FirmwareAutoProof(
                        maxSamples: 4);

                if (!firmwareProof.Verified)
                {
                    throw;
                }

                AppendEvent(
                    $"M6 {phase}: transient no-write ownership sample recovered after stable read-only FF/FF confirmation; retry {attempt}/{maxAttempts}. No compensating restore/write issued.");

                await Task.Delay(
                    50);
            }
        }

        return false;
    }

    private async Task ApplyM6CommandWithFreshSafetyAsync(
        FanCommand command,
        string phase)
    {
        const int maxAttempts = 5;

        for (var attempt = 1;
             attempt <= maxAttempts;
             attempt++)
        {
            if (_worker.StateMachine.State !=
                SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"M6 {phase} requires Healthy telemetry before fan command; state={_worker.StateMachine.State}.");
            }

            var snapshot =
                _lastSnapshot ??
                throw new InvalidOperationException(
                    $"M6 {phase} has no telemetry snapshot.");

            EnsureSuspendHardwareTestLightLoad(
                snapshot);

            var safety =
                EvaluateControlSafety(
                    _hardwareIdentity,
                    _worker.StateMachine.State,
                    snapshot,
                    DateTimeOffset.UtcNow,
                    fanWritePathPresent:
                        _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    $"SafetyGate refused M6 {phase}: " +
                    string.Join(" | ", safety.Reasons));
            }

            try
            {
                await _fanCoordinator.ApplyAsync(
                    command,
                    safety,
                    CancellationToken.None);

                return;
            }
            catch (FanControlStaleSafetyException)
                when (_fanCoordinator.Authority ==
                      FanAuthority.Custom)
            {
                AppendEvent(
                    $"M6 {phase}: command SafetyGate sequence {safety.EvaluationSequence} was superseded; retry {attempt}/{maxAttempts}.");

                await Task.Yield();
            }
        }

        throw new InvalidOperationException(
            $"M6 {phase} could not obtain a current SafetyGate evaluation after {maxAttempts} bounded retries.");
    }

    private static long GetCurrentProcessStartUtcTicks()
    {
        using var process =
            Process.GetCurrentProcess();

        return process.StartTime
            .ToUniversalTime()
            .Ticks;
    }

    private void CompleteM6ModernStandbyHardwareTest(
        bool success,
        int exitCode,
        string message)
    {
        if (_m6Completed)
        {
            return;
        }

        _m6Completed = true;
        TryDeleteFile(
            DisplayAwareReadyPath);

        var result =
            $"{(success ? "PASS" : "FAIL")}|" +
            $"{DateTimeOffset.Now:O}|" +
            $"watchdogPid={_m6WatchdogPid}|" +
            $"watchdogStartTicks={_m6WatchdogStartUtcTicks}|" +
            $"guiPid={Environment.ProcessId}|" +
            $"transitionMode={DisplayAwareLifecycleTransitionMode}|" +
            $"primaryDisplayOff={_m6PrimaryDisplayOffObserved}|" +
            $"pbtSuspend={_m6PbtSuspendObserved}|" +
            $"resumeAutomaticWhileOff={_m6ResumeAutomaticObservedWhileDisplayOff}|" +
            $"resumeSuspendWhileOff={_m6ResumeSuspendObservedWhileDisplayOff}|" +
            $"displayOn={_m6DisplayOnObserved}|" +
            $"acceptedUserResumes={_m6AcceptedUserResumeCount}|" +
            message;

        try
        {
            M6WatchdogStateReader.WriteDurableMarker(
                DisplayAwareResultPath,
                result);
        }
        catch (Exception ex)
        {
            AppLog.Write(
                $"M6: could not write result marker: {ex}");
        }

        AppendEvent(
            $"M6 RESULT: {message}");

        Environment.ExitCode =
            exitCode;

        Ui(() =>
        {
            _allowExit = true;
            Close();
        });
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

            var safety = EvaluateControlSafety(
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
                new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

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

            var commandSafety = EvaluateControlSafety(
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

            var safety = EvaluateControlSafety(
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
                new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

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

            var commandSafety = EvaluateControlSafety(
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

            var safety = EvaluateControlSafety(
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
                new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

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

            var commandSafety = EvaluateControlSafety(
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

            var safety = EvaluateControlSafety(
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
                new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

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

            var commandSafety = EvaluateControlSafety(
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

                var safety = EvaluateControlSafety(
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
                    new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

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

                var commandSafety = EvaluateControlSafety(
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
                new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

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
                await ArmGateGHardwareTestCycleAsync();
                return;
            }

            if (!_gateG1HardwareTestResumeObserved)
            {
                return;
            }

            if (!_gateG1HardwareTestSuspendObserved)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} did not observe PBT_APMSUSPEND while the watchdog-owned 30/30 cycle was armed.");
            }

            if (!_gateG1HardwareTestPreSleepVerified)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} pre-sleep handoff was not verified inside PBT_APMSUSPEND.");
            }

            if (_gateG1AcceptedResumeCount != 1)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} requires exactly one accepted resume event; observed {_gateG1AcceptedResumeCount}.");
            }

            if (_worker.StateMachine.State != SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} post-resume continuation requires Healthy telemetry; state={_worker.StateMachine.State}.");
            }

            var watchdogAfterResume =
                GateG1WatchdogStateReader.Read();

            GateG1WatchdogStateReader.RequireReady(
                watchdogAfterResume,
                _gateG1WatchdogPid);

            if (watchdogAfterResume.JournalPresent)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} post-resume watchdog is not clean; journal remains at '{watchdogAfterResume.JournalPath}'.");
            }

            var afterResume =
                new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

            if (afterResume.CpuSetpoint != byte.MaxValue ||
                afterResume.GpuSetpoint != byte.MaxValue ||
                _fanCoordinator.Authority != FanAuthority.Firmware)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} post-resume firmware baseline invalid: authority={_fanCoordinator.Authority}, EC={afterResume}.");
            }

            var recoveryTimestamp = _lastSnapshot?.Timestamp ??
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} Healthy state has no validated post-resume telemetry snapshot.");

            var reopened =
                await _fanCoordinator.AllowCustomAdmissionAfterRecoveryAsync(
                    recoveryTimestamp,
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: Healthy post-boundary telemetry and watchdog Ready/journal-absent verified.",
                    CancellationToken.None);

            if (!reopened)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} lifecycle fence refused to reopen after validated recovery.");
            }

            AppendEvent(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: admission fence reopened only after Healthy recovery snapshot {recoveryTimestamp:O}, watchdog PID {_gateG1WatchdogPid} Ready and journal absent.");

            if (_worker.StateMachine.State != SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} telemetry degraded after fence reopen and before controlled re-entry.");
            }

            var reentered =
                await TryEnterGateGCustomAuthorityAsync(
                    $"cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} controlled post-resume re-entry");

            if (!reentered ||
                _fanCoordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} could not reacquire watchdog-protected Custom authority after validated recovery; the current SafetyGate evaluation was not merely superseded.");
            }

            await ApplyGateGCommandWithFreshSafetyAsync(
                new FanCommand(
                    SuspendHardwareTestLevel,
                    SuspendHardwareTestLevel,
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} controlled post-resume re-entry validation"),
                $"cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} controlled post-resume 30/30 command");

            GateG1WatchdogStateReader.WriteDurableMarker(
                GateGReentryPath,
                $"REENTRY|{DateTimeOffset.Now:O}|cycle={_gateGCurrentCycle}/{GateGTargetCycleCount}|" +
                $"authority={_fanCoordinator.Authority}|" +
                $"cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|" +
                $"ack=backend-ec+tachs+watchdog-owned|watchdogPid={_gateG1WatchdogPid}|guiPid={Environment.ProcessId}");

            AppendEvent(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: controlled post-resume Custom 30/30 re-entry completed with backend EC+dual-tach ACK and a new watchdog OWNED lease.");

            await _fanCoordinator.RestoreFirmwareAsync(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} controlled post-resume re-entry complete.",
                CancellationToken.None);

            var finalRestoreEvidence =
                _fanCoordinator.LastRestoreEvidence;

            var watchdogFinal =
                GateG1WatchdogStateReader.Read();

            GateG1WatchdogStateReader.RequireReady(
                watchdogFinal,
                _gateG1WatchdogPid);

            var finalEc =
                new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

            var finalReleaseVerified =
                finalRestoreEvidence.HasValue &&
                finalRestoreEvidence.Value.LocalFirmwareAckVerified &&
                finalRestoreEvidence.Value.WatchdogLeaseRequired &&
                finalRestoreEvidence.Value.WatchdogReleaseVerified;

            if (_fanCoordinator.Authority != FanAuthority.Firmware ||
                finalEc.CpuSetpoint != byte.MaxValue ||
                finalEc.GpuSetpoint != byte.MaxValue ||
                !finalReleaseVerified ||
                watchdogFinal.JournalPresent)
            {
                var restoreDetail =
                    finalRestoreEvidence?.Detail ??
                    "no backend restore evidence";

                throw new InvalidOperationException(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} final handoff invalid: " +
                    $"authority={_fanCoordinator.Authority}, EC={finalEc}, " +
                    $"watchdogReleaseVerified={finalReleaseVerified}, " +
                    $"journal={(watchdogFinal.JournalPresent ? "PRESENT" : "absent")}, " +
                    $"restoreDetail={restoreDetail}.");
            }

            var cycleMessage =
                $"PASS: cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} initial OWNED 30/30 fenced telemetry before blocking suspend IO and completed the validated Firmware + EC FF/FF + watchdog-release/journal-absent handoff before resume acceptance; exactly one resume was accepted; telemetry recovered to Healthy; watchdog PID {_gateG1WatchdogPid} remained stable; one controlled post-resume 30/30 re-entry succeeded; final authority=Firmware, EC={finalEc}, journal absent.";

            var finalFirmwareAt =
                _fanCoordinator.LastFirmwareAuthorityAtUtc ??
                DateTimeOffset.UtcNow;

            if (_gateG2HardwareTest)
            {
                WriteGateG2CycleResult(
                    success: true,
                    cycleMessage);
            }

            if (_gateG2HardwareTest &&
                _gateGCurrentCycle < GateGTargetCycleCount)
            {
                AppendEvent(
                    $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: PASS; preserving the same GUI PID {Environment.ProcessId} and watchdog PID {_gateG1WatchdogPid} for the next cycle.");

                _gateGCurrentCycle++;
                ResetGateGHardwareTestCycleState();

                // The previous cycle's re-entry/restore performs real EC/WMI
                // traffic while the telemetry worker continues independently.
                // Do not immediately reacquire Custom from a snapshot captured
                // before that restore. Require two distinct complete, safe,
                // light-load telemetry snapshots produced after the final
                // Firmware transition. This keeps a normal transient/incomplete
                // post-control snapshot from winning the race immediately after
                // the next Custom reservation. Safety is not relaxed: any
                // genuinely unsafe snapshot still prevents the next cycle.
                await WaitForGateGInterCycleTelemetryStabilityAsync(
                    finalFirmwareAt);

                // Stay in the same GUI process and acquire the next cycle only
                // after the post-restore telemetry stream has independently
                // demonstrated stability. The parent harness performs no EC
                // probe while this new Custom ownership is active.
                await ArmGateGHardwareTestCycleAsync();
                return;
            }

            CompleteGateGHardwareTest(
                success: true,
                exitCode: 0,
                message: _gateG2HardwareTest
                    ? $"PASS: {GateGTargetCycleCount}/{GateGTargetCycleCount} consecutive same-process lifecycle cycles completed; watchdog PID {_gateG1WatchdogPid} remained stable; every cycle ended Firmware + FF/FF + journal absent."
                    : cycleMessage);
        }
        catch (Exception ex)
        {
            try
            {
                await _fanCoordinator.RestoreFirmwareAsync(
                    $"{GateGLabel} hardware-test failure cleanup.",
                    CancellationToken.None);
            }
            catch (Exception restoreEx)
            {
                AppLog.Write(
                    $"{GateGLabel}: cleanup restore also failed: {restoreEx}");
            }

            if (_gateG2HardwareTest)
            {
                WriteGateG2CycleResult(
                    success: false,
                    $"FAIL: cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: {ex.Message}");
            }

            CompleteGateGHardwareTest(
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

    private async Task<TelemetrySnapshot> WaitForGateGInterCycleTelemetryStabilityAsync(
        DateTimeOffset finalFirmwareAt)
    {
        var deadline =
            DateTimeOffset.UtcNow +
            GateGInterCycleStabilityTimeout;
        DateTimeOffset? lastObservedTimestamp = null;
        var consecutiveSafeSnapshots = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = _lastSnapshot;

            if (snapshot is not null &&
                (!lastObservedTimestamp.HasValue ||
                 snapshot.Timestamp > lastObservedTimestamp.Value))
            {
                lastObservedTimestamp = snapshot.Timestamp;

                var displaySafety =
                    EvaluateDisplaySafety(
                        _hardwareIdentity,
                        _worker.StateMachine.State,
                        snapshot,
                        DateTimeOffset.UtcNow,
                        fanWritePathPresent:
                            _fanCoordinator.BackendCanWrite);

                var lightLoad =
                    snapshot.CpuTemperatureC <= SuspendHardwareTestMaxCpuTemperatureC &&
                    snapshot.GpuTemperatureC <= SuspendHardwareTestMaxGpuTemperatureC &&
                    snapshot.CpuPackagePowerW <= SuspendHardwareTestMaxCpuPowerW &&
                    snapshot.GpuPowerW <= SuspendHardwareTestMaxGpuPowerW;

                var qualifies =
                    snapshot.Timestamp > finalFirmwareAt &&
                    snapshot.IsComplete &&
                    _worker.StateMachine.State == SystemState.Healthy &&
                    displaySafety.CustomControlPermitted &&
                    lightLoad &&
                    _fanCoordinator.Authority == FanAuthority.Firmware;

                consecutiveSafeSnapshots =
                    qualifies
                        ? consecutiveSafeSnapshots + 1
                        : 0;

                if (consecutiveSafeSnapshots >=
                    GateGInterCycleStableSnapshotsRequired)
                {
                    AppendEvent(
                        $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: inter-cycle telemetry stabilized after {GateGInterCycleStableSnapshotsRequired} distinct complete post-restore snapshots; latest={snapshot.Timestamp:O}; authority=Firmware.");
                    return snapshot;
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} did not produce {GateGInterCycleStableSnapshotsRequired} consecutive complete/safe/light-load telemetry snapshots after the previous Firmware restore within {GateGInterCycleStabilityTimeout.TotalSeconds:0} s.");
    }

    private async Task<bool> TryEnterGateGCustomAuthorityAsync(
        string phase)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_worker.StateMachine.State != SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} {phase} requires Healthy telemetry before Custom admission; state={_worker.StateMachine.State}.");
            }

            var snapshot = _lastSnapshot ??
                throw new InvalidOperationException(
                    $"{GateGLabel} {phase} has no telemetry snapshot for Custom admission.");

            EnsureSuspendHardwareTestLightLoad(snapshot);

            var safety = EvaluateControlSafety(
                _hardwareIdentity,
                _worker.StateMachine.State,
                snapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    $"SafetyGate refused {GateGLabel} {phase} custom authority: " +
                    string.Join(" | ", safety.Reasons));
            }

            var entered = await _fanCoordinator.TryEnterCustomAsync(
                safety,
                CancellationToken.None);

            if (entered &&
                _fanCoordinator.Authority == FanAuthority.Custom)
            {
                return true;
            }

            // Telemetry publishes a fresh SafetyGate evaluation roughly once per
            // second. That newer healthy evaluation can legitimately supersede
            // this caller between Evaluate() and TryEnterCustomAsync(). Such a
            // refusal is no-write and must be retried from the newest snapshot;
            // a refusal while this evaluation is still current is a real gate
            // denial and must remain fail-closed.
            if (_fanCoordinator.IsSafetyEvaluationCurrent(safety))
            {
                return false;
            }

            AppendEvent(
                $"{GateGLabel} {phase}: Custom admission safety sequence {safety.EvaluationSequence} was superseded before entry; retry {attempt}/{maxAttempts} from the newest telemetry snapshot.");

            await Task.Yield();
        }

        return false;
    }

    private async Task ApplyGateGCommandWithFreshSafetyAsync(
        FanCommand command,
        string phase)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_worker.StateMachine.State != SystemState.Healthy)
            {
                throw new InvalidOperationException(
                    $"{GateGLabel} {phase} requires Healthy telemetry before fan command; state={_worker.StateMachine.State}.");
            }

            var snapshot = _lastSnapshot ??
                throw new InvalidOperationException(
                    $"{GateGLabel} {phase} has no telemetry snapshot for fan command.");

            EnsureSuspendHardwareTestLightLoad(snapshot);

            var safety = EvaluateControlSafety(
                _hardwareIdentity,
                _worker.StateMachine.State,
                snapshot,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    $"SafetyGate refused {GateGLabel} {phase} fan command: " +
                    string.Join(" | ", safety.Reasons));
            }

            try
            {
                await _fanCoordinator.ApplyAsync(
                    command,
                    safety,
                    CancellationToken.None);
                return;
            }
            catch (FanControlStaleSafetyException)
                when (_fanCoordinator.Authority == FanAuthority.Custom)
            {
                AppendEvent(
                    $"{GateGLabel} {phase}: command safety sequence {safety.EvaluationSequence} was superseded before dispatch; retry {attempt}/{maxAttempts} from the newest telemetry snapshot.");

                await Task.Yield();
            }
        }

        throw new InvalidOperationException(
            $"{GateGLabel} {phase} could not obtain a current SafetyGate evaluation after {maxAttempts} bounded retries.");
    }

    private async Task ArmGateGHardwareTestCycleAsync()
    {
        var snapshot = _lastSnapshot ??
            throw new InvalidOperationException(
                $"No telemetry snapshot is available for {GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} admission.");

        EnsureSuspendHardwareTestLightLoad(snapshot);

        var watchdog =
            GateG1WatchdogStateReader.Read();

        if (_gateG1WatchdogPid == 0)
        {
            GateG1WatchdogStateReader.RequireReady(watchdog);
            _gateG1WatchdogPid = watchdog.ProcessId;
        }
        else
        {
            GateG1WatchdogStateReader.RequireReady(
                watchdog,
                _gateG1WatchdogPid);
        }

        if (watchdog.JournalPresent)
        {
            throw new InvalidOperationException(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} baseline requires no durable lease journal; found '{watchdog.JournalPath}'.");
        }

        var before =
            new Hp88F8EcControlStateProbe(_modulesDirectory).ReadSetpoint();

        if (before.CpuSetpoint != byte.MaxValue ||
            before.GpuSetpoint != byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} requires firmware-owned FF/FF before admission; read {before.CpuSetpoint}/{before.GpuSetpoint}.");
        }

        var initialPhase =
            $"cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} initial admission";

        var entered =
            await TryEnterGateGCustomAuthorityAsync(
                initialPhase);

        if (!entered ||
            _fanCoordinator.Authority != FanAuthority.Custom)
        {
            throw new InvalidOperationException(
                $"Coordinator did not grant watchdog-protected Custom authority for {GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}; the current SafetyGate evaluation was not merely superseded.");
        }

        await ApplyGateGCommandWithFreshSafetyAsync(
            new FanCommand(
                SuspendHardwareTestLevel,
                SuspendHardwareTestLevel,
                $"explicit {GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} full-watchdog suspend lifecycle validation"),
            $"cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} initial 30/30 command");

        _gateG1HardwareTestBackendAckVerified = true;
        _gateG1HardwareTestArmedAt = DateTimeOffset.UtcNow;
        _gateG1HardwareTestArmed = true;

        GateG1WatchdogStateReader.WriteDurableMarker(
            GateGReadyPath,
            $"READY|{DateTimeOffset.Now:O}|cycle={_gateGCurrentCycle}/{GateGTargetCycleCount}|" +
            $"authority={_fanCoordinator.Authority}|" +
            $"cpu={SuspendHardwareTestLevel}|gpu={SuspendHardwareTestLevel}|" +
            $"ack=backend-ec+tachs+watchdog-owned|watchdogPid={_gateG1WatchdogPid}|guiPid={Environment.ProcessId}");

        AppendEvent(
            $"{GateGLabel} cycle {_gateGCurrentCycle}/{GateGTargetCycleCount}: READY at {SuspendHardwareTestLevel}/{SuspendHardwareTestLevel}; production backend completed durable WriteIntent -> WMI -> EC+dual-tach ACK -> Commit/OWNED. Watchdog PID={_gateG1WatchdogPid}; GUI PID={Environment.ProcessId}. Awaiting external Windows suspend request.");
    }

    private void ResetGateGHardwareTestCycleState()
    {
        _gateG1HardwareTestArmed = false;
        _gateG1HardwareTestSuspendObserved = false;
        _gateG1HardwareTestResumeObserved = false;
        _gateG1HardwareTestPreSleepVerified = false;
        _gateG1HardwareTestBackendAckVerified = false;
        _gateG1HardwareTestArmedAt = null;
        _gateG1AcceptedResumeCount = 0;
        Volatile.Write(
            ref _gateGResumeProofState,
            (int)GateGResumeProofState.Pending);
        _gateGSuspendBoundaryUtc = null;
        _gateGSuspendSource = null;
        _gateGSuspendWasCustom = false;
        _gateGSuspendBackendAckVerified = false;
        _gateGTelemetrySuspendedBeforeRestore = false;
        _gateGTelemetrySuspendMarkedAtUtc = null;
    }

    private void WriteGateG2CycleResult(
        bool success,
        string message)
    {
        if (!_gateG2HardwareTest)
        {
            return;
        }

        var result =
            $"{(success ? "PASS" : "FAIL")}|{DateTimeOffset.Now:O}|" +
            $"cycle={_gateGCurrentCycle}/{GateGTargetCycleCount}|" +
            $"watchdogPid={_gateG1WatchdogPid}|guiPid={Environment.ProcessId}|{message}";

        try
        {
            GateG1WatchdogStateReader.WriteDurableMarker(
                GateGCycleResultPath,
                result);
        }
        catch (Exception ex)
        {
            AppLog.Write(
                $"{GateGLabel}: could not write cycle {_gateGCurrentCycle}/{GateGTargetCycleCount} result marker: {ex}");
        }
    }

    private void CompleteGateGHardwareTest(
        bool success,
        int exitCode,
        string message)
    {
        if (_gateG1HardwareTestCompleted)
        {
            return;
        }

        _gateG1HardwareTestCompleted = true;

        if (_gateG1HardwareTest)
        {
            TryDeleteFile(GateG1HardwareTestReadyPath);
        }

        var result =
            $"{(success ? "PASS" : "FAIL")}|{DateTimeOffset.Now:O}|" +
            $"cycles={_gateGCurrentCycle}/{GateGTargetCycleCount}|" +
            $"watchdogPid={_gateG1WatchdogPid}|guiPid={Environment.ProcessId}|{message}";

        try
        {
            GateG1WatchdogStateReader.WriteDurableMarker(
                GateGFinalResultPath,
                result);
        }
        catch (Exception ex)
        {
            AppLog.Write(
                $"{GateGLabel}: could not write final result marker: {ex}");
        }

        AppendEvent($"{GateGLabel} RESULT: {message}");
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
        var result = EvaluateDisplaySafety(
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
                $"CPU: {FormatCompact(_lastSnapshot.CpuControlTemperatureC, "C")} | {FormatCompact(_lastSnapshot.CpuFanRpm, "RPM", 0)}";
            _trayGpuItem.Text =
                $"GPU: {FormatCompact(_lastSnapshot.GpuTemperatureC, "C")} | {FormatCompact(_lastSnapshot.GpuFanRpm, "RPM", 0)}";
        }

        _trayAuthorityItem.Text = $"Fan authority: {_fanCoordinator.Authority}";

        var tooltip = _lastSnapshot is null
            ? $"VFC {state}"
            : $"VFC {state} | CPU {FormatCompact(_lastSnapshot.CpuControlTemperatureC, "C")} GPU {FormatCompact(_lastSnapshot.GpuTemperatureC, "C")}";

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

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(
        IntPtr hRecipient,
        int flags);

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
        int flags);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(
        IntPtr handle);
}