using System.Drawing;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class MainForm : Form
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeCritical = 0x0006;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private readonly TelemetryWorker _worker;
    private readonly NotifyIcon _trayIcon;

    private readonly Label _stateValue = new();
    private readonly Label _stateReason = new();
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
    private bool _allowExit;
    private bool _closeHintShown;

    public MainForm(string modulesDirectory)
    {
        Text = "VictusFanControl v0.3-dev — READ-ONLY";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 520);
        Size = new Size(850, 620);

        _worker = new TelemetryWorker(modulesDirectory);
        _worker.SnapshotAvailable += WorkerOnSnapshotAvailable;
        _worker.DiagnosticsAvailable += WorkerOnDiagnosticsAvailable;
        _worker.EventLogged += WorkerOnEventLogged;
        _worker.StateMachine.StateChanged += StateMachineOnStateChanged;

        _trayIcon = CreateTrayIcon();

        Controls.Add(BuildUi());

        Shown += (_, _) =>
        {
            AppendEvent($"Modules: {modulesDirectory}");
            _worker.Start();
        };

        FormClosing += OnFormClosingToTray;
        FormClosed += async (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            await _worker.DisposeAsync();
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

    private Control BuildUi()
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
            Text = "Fan control is intentionally disabled.\r\nThe continuous curve will be implemented after safety validation.",
            AutoSize = false
        });

        tabs.TabPages.Add(overview);
        tabs.TabPages.Add(fanCurve);
        tabs.TabPages.Add(diagnostics);
        return tabs;
    }

    private Control BuildOverview()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var statePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 14)
        };
        statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _stateValue.Text = "Starting";
        _stateValue.AutoSize = true;
        _stateValue.Font = new Font(Font, FontStyle.Bold);
        _stateReason.Text = "Waiting for telemetry validation.";
        _stateReason.AutoSize = true;
        _stateReason.Margin = new Padding(18, 3, 0, 0);

        statePanel.Controls.Add(new Label { Text = "System state:", AutoSize = true }, 0, 0);
        statePanel.Controls.Add(_stateValue, 1, 0);
        statePanel.Controls.Add(new Label { Text = "Reason:", AutoSize = true }, 0, 1);
        statePanel.Controls.Add(_stateReason, 1, 1);

        root.Controls.Add(statePanel);
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

        var note = new Label
        {
            AutoSize = true,
            Margin = new Padding(3, 18, 3, 3),
            Text = "READ-ONLY build: HP firmware remains in control of both fans."
        };
        root.Controls.Add(note);

        return root;
    }

    private Control BuildDiagnostics()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 270
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
        return split;
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
        var open = new ToolStripMenuItem("Open VictusFanControl");
        var exit = new ToolStripMenuItem("Exit");

        open.Click += (_, _) => RestoreFromTray();
        exit.Click += (_, _) =>
        {
            _allowExit = true;
            Close();
        };

        menu.Items.Add(_trayStateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "VictusFanControl — Starting",
            ContextMenuStrip = menu,
            Visible = true
        };

        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void WorkerOnSnapshotAvailable(object? sender, TelemetrySnapshot snapshot)
    {
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
        });
    }

    private void WorkerOnDiagnosticsAvailable(object? sender, string text) =>
        Ui(() => _diagnostics.Text = text);

    private void WorkerOnEventLogged(object? sender, string text) =>
        Ui(() => AppendEvent(text));

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

            _trayIcon.Text = $"VictusFanControl — {e.Current}";
        });
    }

    private void OnFormClosingToTray(object? sender, FormClosingEventArgs e)
    {
        if (_allowExit || e.CloseReason == CloseReason.WindowsShutDown)
        {
            return;
        }

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

    private void AppendEvent(string text)
    {
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
        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 14, FontStyle.Bold)
    };

    private static string Format(double? value, string suffix, int decimals = 1) =>
        value.HasValue ? $"{value.Value.ToString($"F{decimals}")} {suffix}" : "n/a";
}
