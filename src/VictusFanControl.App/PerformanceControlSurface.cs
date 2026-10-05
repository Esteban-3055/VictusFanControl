using System.Drawing;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal sealed class PerformanceControlSurface :
    UserControl
{
    private readonly Action<string> _eventSink;
    private readonly bool _targetSupported;

    private readonly TrackBar _acPl1 =
        CreatePl1Slider();

    private readonly TrackBar _acPl2 =
        CreatePl2Slider();

    private readonly TrackBar _batteryPl1 =
        CreatePl1Slider();

    private readonly TrackBar _batteryPl2 =
        CreatePl2Slider();

    private readonly Label _acPl1Value =
        ValueLabel();

    private readonly Label _acPl2Value =
        ValueLabel();

    private readonly Label _batteryPl1Value =
        ValueLabel();

    private readonly Label _batteryPl2Value =
        ValueLabel();

    private readonly Label _status =
        new()
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0)
        };

    private readonly Button _save =
        new()
        {
            Text = "Guardar límites",
            AutoSize = true
        };

    private readonly Button _defaults =
        new()
        {
            Text = "Restaurar predeterminados",
            AutoSize = true
        };

    private bool _syncing;
    private bool _busy;
    private readonly PerformanceGuardianClient? _client;
    private readonly Func<bool> _canApply;
    private readonly CheckBox _cpuEnabled = new() { Text = "Limitar CPU", Checked = true, AutoSize = true };
    private readonly CheckBox _gpuEnabled = new() { Text = "Limitar GPU (AC 210–1850 / batería 210–1200 MHz)", Checked = true, AutoSize = true };
    private readonly Button _apply = new() { Text = "Aplicar CPU / GPU", AutoSize = true };
    private readonly Button _release = new() { Text = "Liberar límites", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    internal event Action? SessionStatusChanged;
    internal bool LimitsActive => _client?.LimitsActive == true;
    internal PerformanceGuiSessionConfiguration? AppliedConfiguration => _client?.AppliedConfiguration;
    internal string? GuardianReportPath => _client?.GuardianReportPath;
    internal PerformanceGuardianResponse? LastStatus => _client?.LastStatus;

    internal Task CloseSessionAsync() => _client?.CloseAsync() ?? Task.CompletedTask;

    internal PerformanceControlSurface(
        string? targetProfileId,
        Action<string> eventSink,
        PerformanceGuardianClient? client = null,
        Func<bool>? canApply = null)
    {
        _client = client;
        _canApply = canApply ?? (() => false);
        _eventSink =
            eventSink ??
            throw new ArgumentNullException(
                nameof(eventSink));

        _targetSupported =
            string.Equals(
                targetProfileId,
                CpuPowerProductDefaults.TargetProfileId,
                StringComparison.Ordinal);

        Dock = DockStyle.Fill;
        AutoScroll = true;

        var settings =
            PerformanceUiSettingsStore.Load();

        SetValues(
            settings);

        WireEvents();

        Controls.Add(
            BuildSurface(
                targetProfileId));

        ApplyTargetGate();
        _timer.Tick += async (_, _) => await PollAsync();
        if (_client is not null) _timer.Start();

        _status.Text =
            _targetSupported
                ? "Sesión desactivada. Guarda los valores y pulsa Aplicar antes de iniciar Automatic."
                : "CPU/GPU no están disponibles para este perfil de hardware.";
    }

    private System.Windows.Forms.Control BuildSurface(
        string? targetProfileId)
    {
        var root =
            new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 1,
                RowCount = 7,
                AutoScroll = true
            };

        root.RowStyles.Add(
            new RowStyle(
                SizeType.AutoSize));

        root.Controls.Add(
            new Label
            {
                Text = "Límites CPU / GPU · controles provisionales",
                AutoSize = true,
                Font = new Font(
                    Font,
                    FontStyle.Bold)
            },
            0,
            0);

        root.Controls.Add(
            new Label
            {
                Text =
                    $"Perfil: {targetProfileId ?? "sin perfil validado"}. " +
                    "Los límites se expresan en watts y PL2 nunca puede ser menor que PL1.",
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Margin = new Padding(3, 8, 3, 8)
            },
            0,
            1);

        root.Controls.Add(
            BuildPresetGroup(
                "AC / cargador",
                _acPl1,
                _acPl1Value,
                _acPl2,
                _acPl2Value),
            0,
            2);

        root.Controls.Add(
            BuildPresetGroup(
                "Batería",
                _batteryPl1,
                _batteryPl1Value,
                _batteryPl2,
                _batteryPl2Value),
            0,
            3);

        var buttons =
            new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                FlowDirection =
                    FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(3, 12, 3, 6)
            };

        buttons.Controls.Add(
            _save);

        buttons.Controls.Add(_defaults);
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_release);

        root.Controls.Add(
            buttons,
            0,
            4);

        var domains = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown };
        domains.Controls.Add(_cpuEnabled);
        domains.Controls.Add(_gpuEnabled);
        buttons.WrapContents = true;
        buttons.Controls.Add(domains);
        root.Controls.Add(
            _status,
            0,
            5);

        root.Controls.Add(
            new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Margin = new Padding(3, 12, 3, 3),
                Text =
                    "Aplica con ventiladores en Firmware. CPU: PL1 sostenido y PL2 turbo; GPU: rango fijo por fuente. " +
                    "Liberar o salir restaura la CPU si conserva su propiedad y solicita Reset de GPU. Las protecciones térmicas siguen activas."
            },
            0,
            6);

        return root;
    }

    private static GroupBox BuildPresetGroup(
        string title,
        TrackBar pl1,
        Label pl1Value,
        TrackBar pl2,
        Label pl2Value)
    {
        var group =
            new GroupBox
            {
                Text = title,
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(12),
                Margin = new Padding(3, 8, 3, 8)
            };

        var table =
            new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 2
            };

        table.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));

        table.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100));

        table.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.AutoSize));

        table.Controls.Add(
            new Label
            {
                Text = "PL1 sostenido:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            },
            0,
            0);

        table.Controls.Add(
            pl1,
            1,
            0);

        table.Controls.Add(
            pl1Value,
            2,
            0);

        table.Controls.Add(
            new Label
            {
                Text = "PL2 turbo:",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            },
            0,
            1);

        table.Controls.Add(
            pl2,
            1,
            1);

        table.Controls.Add(
            pl2Value,
            2,
            1);

        group.Controls.Add(
            table);

        return group;
    }

    private void WireEvents()
    {
        _acPl1.ValueChanged +=
            (_, _) =>
                OnPl1Changed(
                    _acPl1,
                    _acPl2);

        _acPl2.ValueChanged +=
            (_, _) =>
                OnPl2Changed(
                    _acPl1,
                    _acPl2);

        _batteryPl1.ValueChanged +=
            (_, _) =>
                OnPl1Changed(
                    _batteryPl1,
                    _batteryPl2);

        _batteryPl2.ValueChanged +=
            (_, _) =>
                OnPl2Changed(
                    _batteryPl1,
                    _batteryPl2);

        _apply.Click += async (_, _) => await ApplyAsync();
        _release.Click += async (_, _) => await ReleaseAsync();
        _save.Click +=
            (_, _) =>
                GuardPreferenceAction(SaveCurrent);

        _defaults.Click +=
            (_, _) =>
                GuardPreferenceAction(RestoreDefaults);
    }

    private void OnPl1Changed(
        TrackBar pl1,
        TrackBar pl2)
    {
        if (_syncing)
            return;

        _syncing = true;

        try
        {
            if (pl2.Value <
                pl1.Value)
            {
                pl2.Value =
                    pl1.Value;
            }

            UpdateValueLabels();
            MarkDirty();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnPl2Changed(
        TrackBar pl1,
        TrackBar pl2)
    {
        if (_syncing)
            return;

        _syncing = true;

        try
        {
            if (pl1.Value >
                pl2.Value)
            {
                pl1.Value =
                    Math.Min(
                        pl2.Value,
                        pl1.Maximum);
            }

            UpdateValueLabels();
            MarkDirty();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SaveCurrent()
    {
        if (!_targetSupported)
            return;

        var settings =
            Current();

        PerformanceUiSettingsStore.Save(
            settings);

        _status.Text =
            $"Guardado: AC {settings.AcPl1Watts}/{settings.AcPl2Watts} W, " +
            $"Batería {settings.BatteryPl1Watts}/{settings.BatteryPl2Watts} W. " +
            "No se realizó ninguna escritura de hardware.";

        _eventSink(
            $"Performance CPU preferences saved: AC={settings.AcPl1Watts}/{settings.AcPl2Watts} W; Battery={settings.BatteryPl1Watts}/{settings.BatteryPl2Watts} W; hardwareWrites=0.");
    }

    private void RestoreDefaults()
    {
        if (!_targetSupported)
            return;

        var defaults =
            PerformanceUiSettingsStore.Default();

        SetValues(
            defaults);

        PerformanceUiSettingsStore.Save(
            defaults);

        _status.Text =
            "Predeterminados restaurados y guardados: AC 35/60 W, Batería 8/15 W. No se realizó ninguna escritura de hardware.";

        _eventSink(
            "Performance CPU preferences restored to product defaults AC=35/60 W; Battery=8/15 W; hardwareWrites=0.");
    }

    private void MarkDirty()
    {
        if (!_targetSupported)
            return;

        _status.Text =
            "Cambios sin guardar. La edición de sliders no modifica el hardware.";
    }

    private PerformanceUiSettingsDocument Current() =>
        new()
        {
            AcPl1Watts = _acPl1.Value,
            AcPl2Watts = _acPl2.Value,
            BatteryPl1Watts = _batteryPl1.Value,
            BatteryPl2Watts = _batteryPl2.Value
        };

    private void SetValues(
        PerformanceUiSettingsDocument settings)
    {
        _syncing = true;

        try
        {
            _acPl1.Value =
                settings.AcPl1Watts;

            _acPl2.Value =
                settings.AcPl2Watts;

            _batteryPl1.Value =
                settings.BatteryPl1Watts;

            _batteryPl2.Value =
                settings.BatteryPl2Watts;

            UpdateValueLabels();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateValueLabels()
    {
        _acPl1Value.Text =
            $"{_acPl1.Value} W";

        _acPl2Value.Text =
            $"{_acPl2.Value} W";

        _batteryPl1Value.Text =
            $"{_batteryPl1.Value} W";

        _batteryPl2Value.Text =
            $"{_batteryPl2.Value} W";
    }

    private void ApplyTargetGate()
    {
        foreach (var slider in new[]
                 {
                     _acPl1,
                     _acPl2,
                     _batteryPl1,
                     _batteryPl2
                 })
        {
            slider.Enabled =
                _targetSupported;
        }

        var edit = _targetSupported && !_busy && _client?.HasProcess != true;
        foreach (var slider in new[] { _acPl1, _acPl2, _batteryPl1, _batteryPl2 }) slider.Enabled = edit;
        _save.Enabled = _defaults.Enabled = _cpuEnabled.Enabled = _gpuEnabled.Enabled = edit;
        _apply.Enabled = edit && _client is not null && _canApply();
        _release.Enabled = !_busy && _client?.HasProcess == true && _canApply();
    }

    private void GuardPreferenceAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { _status.Text = "No se pudo guardar: " + ex.Message; _eventSink(_status.Text); }
    }

    private async Task ApplyAsync()
    {
        if (_busy || !_targetSupported || _client is null || !_canApply()) return;
        _busy = true; ApplyTargetGate();
        try
        {
            var settings = Current();
            PerformanceUiSettingsStore.Save(settings);
            var configuration = new PerformanceGuiSessionConfiguration
            {
                CpuEnabled = _cpuEnabled.Checked, GpuEnabled = _gpuEnabled.Checked,
                AcPl1Watts = settings.AcPl1Watts, AcPl2Watts = settings.AcPl2Watts,
                BatteryPl1Watts = settings.BatteryPl1Watts, BatteryPl2Watts = settings.BatteryPl2Watts
            };
            var status = await _client.EnableAsync(configuration);
            ShowStatus(status);
            _eventSink("Performance GUI session: " + System.Text.Json.JsonSerializer.Serialize(new { configuration, status }));
        }
        catch (Exception ex) { _status.Text = "Aplicación rechazada: " + ex.Message; _eventSink(_status.Text); }
        finally { _busy = false; ApplyTargetGate(); }
    }

    private async Task ReleaseAsync()
    {
        if (_busy || !_canApply()) return;
        _busy = true; ApplyTargetGate();
        try { await CloseSessionAsync(); _status.Text = "Sesión liberada; Guardian terminó correctamente."; _eventSink(_status.Text); }
        catch (Exception ex) { _status.Text = "Liberación sin confirmar: " + ex.Message; _eventSink(_status.Text); }
        finally { _busy = false; ApplyTargetGate(); }
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_client?.HasProcess == true)
            {
                var status = await _client.StatusAsync();
                if (status is not null) ShowStatus(status);
                SessionStatusChanged?.Invoke();
            }
        }
        catch (Exception ex) { _status.Text = "Estado sin confirmar: " + ex.Message; SessionStatusChanged?.Invoke(); }
        finally { _busy = false; ApplyTargetGate(); }
    }

    private void ShowStatus(PerformanceGuardianResponse s) => _status.Text =
        $"Fuente: {s.PowerSource}. CPU: {s.CpuState ?? "desactivada"}. GPU: {s.GpuState ?? "desactivada"}. " +
        (s.GpuEnabled ? "NVML aceptó el rango; la lectura directa del rango instalado no está disponible. " : "") +
        $"{s.RuntimeFailure} {s.CpuStatus} {s.GpuStatus}";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private static TrackBar CreatePl1Slider() =>
        new()
        {
            Minimum =
                CpuPowerProductDefaults.MinimumPl1Watts,
            Maximum =
                CpuPowerProductDefaults.MaximumConfigurablePl1Watts,
            TickFrequency = 4,
            SmallChange = 1,
            LargeChange = 4,
            AutoSize = true,
            Dock = DockStyle.Fill
        };

    private static TrackBar CreatePl2Slider() =>
        new()
        {
            Minimum =
                CpuPowerProductDefaults.MinimumPl2Watts,
            Maximum =
                CpuPowerProductDefaults.MaximumConfigurablePl2Watts,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 5,
            AutoSize = true,
            Dock = DockStyle.Fill
        };

    private static Label ValueLabel() =>
        new()
        {
            AutoSize = true,
            MinimumSize = new Size(52, 0),
            TextAlign =
                ContentAlignment.MiddleRight,
            Anchor =
                AnchorStyles.Right
        };
}
