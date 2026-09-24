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

    private readonly TelemetryWorker _worker;
    private readonly FanControlCoordinator _fanCoordinator;
    private readonly string _fanBackendStartupDetail;
    private readonly HardwareIdentity _hardwareIdentity;
    private readonly string _modulesDirectory;
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

    private TelemetrySnapshot? _lastSnapshot;

    public MainForm(string modulesDirectory)
    {
        Text = "VictusFanControl v0.3-dev — backend integrated / automatic policy OFF";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 560);
        Size = new Size(900, 680);

        _modulesDirectory = modulesDirectory;
        _hardwareIdentity = HardwareIdentityReader.ReadCurrent();

        IFanControlBackend backend;
        try
        {
            backend = new Hp88F8FanControlBackend(modulesDirectory);
            _fanBackendStartupDetail = backend.CanWrite
                ? "HP 88F8 write/restore backend initialized."
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
                    _worker.NotifySuspend("WM_POWERBROADCAST/PBT_APMSUSPEND");
                    break;

                case PbtApmResumeAutomatic:
                    _worker.NotifyResume("WM_POWERBROADCAST/PBT_APMRESUMEAUTOMATIC");
                    break;

                case PbtApmResumeSuspend:
                    _worker.NotifyResume("WM_POWERBROADCAST/PBT_APMRESUMESUSPEND");
                    break;

                case PbtApmResumeCritical:
                    _worker.NotifyResume("WM_POWERBROADCAST/PBT_APMRESUMECRITICAL");
                    break;
            }
        }

        base.WndProc(ref m);
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


    private void FanCoordinatorOnAuthorityChanged(
        object? sender,
        FanAuthorityChangedEventArgs e)
    {
        Ui(() =>
        {
            AppendEvent(
                $"Fan authority: {e.Previous} -> {e.Current}. {e.Reason}");
            UpdateSafetyStatus();
            UpdateTray();
        });
    }

    private void WorkerOnSnapshotAvailable(object? sender, TelemetrySnapshot snapshot)
    {
        _lastSnapshot = snapshot;

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

    private void UpdateSafetyStatus()
    {
        var result = SafetyGate.Evaluate(
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
