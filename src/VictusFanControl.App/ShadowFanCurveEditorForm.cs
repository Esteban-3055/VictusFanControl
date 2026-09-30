using System.Text;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>
/// Explicit hardware-free visual curve editor. All live-looking values are
/// synthetic simulation signals.
/// </summary>
internal sealed class ShadowFanCurveEditorForm : Form
{
    private readonly FanCurveEditorModel _model;
    private readonly ShadowFanCurveEditorControl _editor;
    private readonly Label _filteredValue;
    private readonly Label _rawValue;
    private readonly Label _levelValue;
    private readonly Label _trendValue;
    private readonly System.Windows.Forms.Timer _timer;

    private int _simulationTick;
    private readonly Queue<double> _history = new();

    public ShadowFanCurveEditorForm()
    {
        Text =
            "VictusFanControl - Editor de curva (SHADOW / simulacion)";
        Width = 1020;
        Height = 650;
        StartPosition = FormStartPosition.CenterScreen;

        _model =
            new FanCurveEditorModel(
                minimumTemperatureC: 35,
                maximumTemperatureC: 100,
                minimumLevel: 10,
                maximumLevel: 50,
                points:
                [
                    new(40, 10),
                    new(55, 15),
                    new(65, 24),
                    new(75, 34),
                    new(85, 44),
                    new(95, 50)
                ]);

        _editor =
            new ShadowFanCurveEditorControl(_model)
            {
                Dock = DockStyle.Fill
            };

        _filteredValue = ValueLabel();
        _rawValue = ValueLabel();
        _levelValue = ValueLabel();
        _trendValue = ValueLabel();

        var banner =
            new Label
            {
                Text =
                    "SHADOW / SIMULACION SOLAMENTE - no adquiere Custom, no escribe ventiladores y no sustituye SafetyGate.",
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(10),
                Font =
                    new Font(
                        Font.FontFamily,
                        10,
                        FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

        var side = BuildSidePanel();

        var split =
            new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 760,
                FixedPanel = FixedPanel.Panel2
            };

        split.Panel1.Controls.Add(_editor);
        split.Panel2.Controls.Add(side);

        Controls.Add(split);
        Controls.Add(banner);

        _editor.CurveChanged +=
            (_, _) => RefreshDisplayedLevel();

        _timer =
            new System.Windows.Forms.Timer
            {
                Interval = 500
            };

        _timer.Tick +=
            (_, _) => AdvanceSimulation();

        Shown +=
            (_, _) => _timer.Start();

        FormClosed +=
            (_, _) => _timer.Stop();
    }

    private System.Windows.Forms.Control BuildSidePanel()
    {
        var panel =
            new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 8,
                AutoSize = true
            };

        panel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 55));
        panel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 45));

        AddRow(panel, 0, "CPU filtrada (mediana 5):", _filteredValue);
        AddRow(panel, 1, "Maximo instantaneo:", _rawValue);
        AddRow(panel, 2, "Nivel por curva:", _levelValue);
        AddRow(panel, 3, "Tendencia simulada:", _trendValue);

        var note =
            new Label
            {
                Text =
                    "Arrastra los puntos. El punto azul representa la temperatura filtrada. La linea roja discontinua representa el maximo termico instantaneo. La simulacion es deliberadamente independiente del hardware.",
                AutoSize = true,
                MaximumSize = new Size(220, 0)
            };

        panel.Controls.Add(note, 0, 4);
        panel.SetColumnSpan(note, 2);

        var copy =
            new Button
            {
                Text = "Copiar puntos de curva",
                AutoSize = true
            };

        copy.Click +=
            (_, _) =>
            {
                var builder = new StringBuilder();

                foreach (var point in _model.Points)
                {
                    builder.AppendLine(
                        point.Input.ToString("0.###") +
                        " C -> level " +
                        point.Level.ToString("0.###"));
                }

                Clipboard.SetText(builder.ToString());
            };

        panel.Controls.Add(copy, 0, 5);
        panel.SetColumnSpan(copy, 2);

        return panel;
    }

    private static Label ValueLabel() =>
        new()
        {
            AutoSize = true,
            Font =
                new Font(
                    SystemFonts.DefaultFont,
                    FontStyle.Bold)
        };

    private static void AddRow(
        TableLayoutPanel panel,
        int row,
        string name,
        System.Windows.Forms.Control value)
    {
        panel.Controls.Add(
            new Label
            {
                Text = name,
                AutoSize = true
            },
            0,
            row);

        panel.Controls.Add(value, 1, row);
    }

    private void AdvanceSimulation()
    {
        _simulationTick++;

        var seconds =
            _simulationTick *
            (_timer.Interval / 1000.0);

        var baseTemperature =
            62 +
            (13 * Math.Sin(seconds / 7.0));

        var raw =
            baseTemperature +
            (3 * Math.Sin(seconds * 2.2));

        if (_simulationTick % 34 == 0)
        {
            raw += 13;
        }

        _history.Enqueue(raw);

        while (_history.Count > 5)
        {
            _history.Dequeue();
        }

        double? filtered = null;

        if (_history.Count == 5)
        {
            filtered =
                _history
                    .OrderBy(value => value)
                    .ElementAt(2);
        }

        double? trend = null;

        if (_history.Count >= 2)
        {
            var values = _history.ToArray();

            trend =
                (values[^1] - values[0]) /
                ((_history.Count - 1) *
                 (_timer.Interval / 1000.0));
        }

        _editor.SetTelemetryIndicators(filtered, raw);

        _filteredValue.Text =
            filtered.HasValue
                ? filtered.Value.ToString("0.0") + " C"
                : "llenando ventana 5/5";

        _rawValue.Text =
            raw.ToString("0.0") + " C";

        _trendValue.Text =
            trend.HasValue
                ? trend.Value.ToString("+0.00;-0.00;0.00") +
                  " C/s"
                : "-";

        RefreshDisplayedLevel();
    }

    private void RefreshDisplayedLevel()
    {
        if (_history.Count < 5)
        {
            _levelValue.Text = "-";
            return;
        }

        var filtered =
            _history
                .OrderBy(value => value)
                .ElementAt(2);

        _levelValue.Text =
            _model
                .Interpolate(filtered)
                .ToString("0.0");
    }
}
