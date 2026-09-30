using System.Drawing.Drawing2D;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>
/// Hardware-free draggable temperature/equal-fan-level editor used only by the
/// explicit shadow/simulation form.
/// </summary>
internal sealed class ShadowFanCurveEditorControl : System.Windows.Forms.Control
{
    private const int PointRadius = 7;
    private readonly FanCurveEditorModel _model;

    private int _dragIndex = -1;
    private double? _filteredCpuC;
    private double? _instantaneousCpuMaxC;

    public ShadowFanCurveEditorControl(
        FanCurveEditorModel model)
    {
        _model = model;

        DoubleBuffered = true;
        ResizeRedraw = true;
        MinimumSize = new Size(620, 380);
        BackColor = Color.White;
        Cursor = Cursors.Cross;
    }

    public event EventHandler? CurveChanged;

    public FanCurveEditorModel Model => _model;

    public void SetTelemetryIndicators(
        double? filteredCpuC,
        double? instantaneousCpuMaxC)
    {
        _filteredCpuC = filteredCpuC;
        _instantaneousCpuMaxC = instantaneousCpuMaxC;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var plot = PlotRectangle();

        using var axisPen =
            new Pen(Color.FromArgb(70, 70, 70), 1.5f);
        using var gridPen =
            new Pen(Color.FromArgb(225, 225, 225), 1);

        DrawGrid(e.Graphics, plot, gridPen, axisPen);
        DrawCurve(e.Graphics, plot);
        DrawTelemetry(e.Graphics, plot);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var plot = PlotRectangle();

        for (var index = 0;
             index < _model.Points.Count;
             index++)
        {
            var point = ToPixel(plot, _model.Points[index]);
            var distance =
                Math.Sqrt(
                    Math.Pow(point.X - e.X, 2) +
                    Math.Pow(point.Y - e.Y, 2));

            if (distance <= PointRadius + 5)
            {
                _dragIndex = index;
                Capture = true;
                Cursor = Cursors.Hand;
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragIndex < 0 || e.Button != MouseButtons.Left)
        {
            return;
        }

        var plot = PlotRectangle();

        var temperature =
            _model.MinimumTemperatureC +
            ((e.X - plot.Left) / (double)plot.Width) *
            (_model.MaximumTemperatureC -
             _model.MinimumTemperatureC);

        var level =
            _model.MaximumLevel -
            ((e.Y - plot.Top) / (double)plot.Height) *
            (_model.MaximumLevel -
             _model.MinimumLevel);

        _model.MovePoint(
            _dragIndex,
            temperature,
            level);

        CurveChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        _dragIndex = -1;
        Capture = false;
        Cursor = Cursors.Cross;
    }

    private Rectangle PlotRectangle()
    {
        const int left = 72;
        const int top = 34;
        const int right = 34;
        const int bottom = 58;

        return new Rectangle(
            left,
            top,
            Math.Max(100, ClientSize.Width - left - right),
            Math.Max(100, ClientSize.Height - top - bottom));
    }

    private void DrawGrid(
        Graphics graphics,
        Rectangle plot,
        Pen gridPen,
        Pen axisPen)
    {
        using var textBrush =
            new SolidBrush(Color.FromArgb(65, 65, 65));
        using var font =
            new Font(Font.FontFamily, 9f);

        for (var level = _model.MinimumLevel;
             level <= _model.MaximumLevel;
             level += 10)
        {
            var y = ToPixelY(plot, level);

            graphics.DrawLine(
                gridPen,
                plot.Left,
                y,
                plot.Right,
                y);

            graphics.DrawString(
                level.ToString(),
                font,
                textBrush,
                30,
                y - 8);
        }

        for (var temperature = 40;
             temperature <= 100;
             temperature += 10)
        {
            if (temperature < _model.MinimumTemperatureC ||
                temperature > _model.MaximumTemperatureC)
            {
                continue;
            }

            var x = ToPixelX(plot, temperature);

            graphics.DrawLine(
                gridPen,
                x,
                plot.Top,
                x,
                plot.Bottom);

            graphics.DrawString(
                temperature + " C",
                font,
                textBrush,
                x - 16,
                plot.Bottom + 8);
        }

        graphics.DrawRectangle(axisPen, plot);

        graphics.DrawString(
            "Nivel igualado CPU/GPU",
            font,
            textBrush,
            8,
            8);

        graphics.DrawString(
            "Temperatura CPU",
            font,
            textBrush,
            plot.Left + (plot.Width / 2) - 45,
            plot.Bottom + 34);
    }

    private void DrawCurve(
        Graphics graphics,
        Rectangle plot)
    {
        var pixels =
            _model.Points
                .Select(point => ToPixel(plot, point))
                .ToArray();

        using var curvePen =
            new Pen(Color.FromArgb(38, 92, 160), 3f);

        if (pixels.Length >= 2)
        {
            graphics.DrawLines(curvePen, pixels);
        }

        for (var index = 0;
             index < pixels.Length;
             index++)
        {
            var point = pixels[index];

            using var fill =
                new SolidBrush(
                    index == _dragIndex
                        ? Color.Orange
                        : Color.FromArgb(38, 92, 160));

            graphics.FillEllipse(
                fill,
                point.X - PointRadius,
                point.Y - PointRadius,
                PointRadius * 2,
                PointRadius * 2);

            graphics.DrawString(
                _model.Points[index].Input.ToString("0") +
                "C / " +
                _model.Points[index].Level.ToString("0"),
                Font,
                Brushes.Black,
                point.X + 8,
                point.Y - 22);
        }
    }

    private void DrawTelemetry(
        Graphics graphics,
        Rectangle plot)
    {
        if (_instantaneousCpuMaxC.HasValue)
        {
            var rawX =
                ToPixelX(
                    plot,
                    Math.Clamp(
                        _instantaneousCpuMaxC.Value,
                        _model.MinimumTemperatureC,
                        _model.MaximumTemperatureC));

            using var rawPen =
                new Pen(Color.Firebrick, 2f)
                {
                    DashStyle = DashStyle.Dash
                };

            graphics.DrawLine(
                rawPen,
                rawX,
                plot.Top,
                rawX,
                plot.Bottom);

            graphics.DrawString(
                "max instantaneo " +
                _instantaneousCpuMaxC.Value.ToString("0.0") +
                " C",
                Font,
                Brushes.Firebrick,
                Math.Min(rawX + 5, plot.Right - 130),
                plot.Top + 5);
        }

        if (_filteredCpuC.HasValue)
        {
            var filtered =
                Math.Clamp(
                    _filteredCpuC.Value,
                    _model.MinimumTemperatureC,
                    _model.MaximumTemperatureC);

            var level = _model.Interpolate(filtered);

            var point =
                ToPixel(
                    plot,
                    new AdaptiveFanCurvePoint(
                        filtered,
                        level));

            const int markerRadius = 9;

            graphics.FillEllipse(
                Brushes.DodgerBlue,
                point.X - markerRadius,
                point.Y - markerRadius,
                markerRadius * 2,
                markerRadius * 2);

            graphics.DrawEllipse(
                Pens.White,
                point.X - markerRadius,
                point.Y - markerRadius,
                markerRadius * 2,
                markerRadius * 2);
        }
    }

    private Point ToPixel(
        Rectangle plot,
        AdaptiveFanCurvePoint point) =>
        new(
            ToPixelX(plot, point.Input),
            ToPixelY(plot, point.Level));

    private int ToPixelX(
        Rectangle plot,
        double temperature) =>
        plot.Left +
        (int)Math.Round(
            (temperature - _model.MinimumTemperatureC) /
            (_model.MaximumTemperatureC -
             _model.MinimumTemperatureC) *
            plot.Width);

    private int ToPixelY(
        Rectangle plot,
        double level) =>
        plot.Bottom -
        (int)Math.Round(
            (level - _model.MinimumLevel) /
            (_model.MaximumLevel -
             _model.MinimumLevel) *
            plot.Height);
}
