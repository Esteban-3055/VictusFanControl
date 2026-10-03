using System.Drawing.Drawing2D;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>Pure visual editor. Mouse gestures emit draft points only.</summary>
internal sealed class AdaptiveCurveChart : System.Windows.Forms.Control
{
    public IReadOnlyList<AdaptiveFanCurvePoint> Points { get; set; } = Array.Empty<AdaptiveFanCurvePoint>();
    public double MaximumInput { get; set; } = 110;
    public string AxisLabel { get; set; } = "Temperatura CPU (°C)";
    public int SelectedIndex { get; set; } = -1;
    public double? LiveInput { get; set; }
    public double? MedianInput { get; set; }
    public event Action<int>? PointSelected;
    public event Action<IReadOnlyList<AdaptiveFanCurvePoint>>? DraftChanged;
    private bool _dragging;
    private AdaptiveFanCurvePoint[]? _dragOrigin;

    public AdaptiveCurveChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        MinimumSize = new Size(420, 250);
        Height = 290; Dock = DockStyle.Fill;
        AccessibleName = "Editor de curva de ventiladores";
        AccessibleDescription = "Seleccione y arrastre puntos; también puede editarlos con los campos numéricos.";
    }
    private RectangleF Plot => new(52, 26, Math.Max(1, Width - 74), Math.Max(1, Height - 78));
    private PointF Screen(AdaptiveFanCurvePoint p) => new(
        Plot.Left + (float)(p.Input / MaximumInput * Plot.Width),
        Plot.Bottom - (float)((p.Level - 10) / 40 * Plot.Height));
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = Plot;
        using var grid = new Pen(Color.FromArgb(228, 233, 239));
        using var line = new Pen(Color.FromArgb(37, 116, 184), 2.5f);
        using var gray = new SolidBrush(Color.DimGray);
        g.DrawString("Nivel común CPU/GPU", Font, gray, 5, 3);
        for (var level = 10; level <= 50; level += 10)
        {
            var y = Screen(new(0, level)).Y;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(level.ToString(), Font, gray, 20, y - 7);
        }
        for (var tick = 0; tick <= 5; tick++)
        {
            var x = plot.Left + plot.Width * tick / 5;
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            g.DrawString((MaximumInput * tick / 5).ToString("0"), Font, gray, x - 9, plot.Bottom + 5);
        }
        g.DrawString(AxisLabel, Font, gray, plot.Left + 60, plot.Bottom + 27);
        if (Points.Count >= 2)
        {
            // Extend endpoint clamping just like the policy interpolation.
            var path = new List<PointF> { Screen(new(0, Points[0].Level)) };
            path.AddRange(Points.Select(Screen)); path.Add(Screen(new(MaximumInput, Points[^1].Level)));
            g.DrawLines(line, path.ToArray());
        }
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Screen(Points[i]);
            using var brush = new SolidBrush(i == SelectedIndex ? Color.DarkOrange : Color.FromArgb(37, 116, 184));
            g.FillEllipse(brush, p.X - 5, p.Y - 5, 10, 10);
        }
        void Marker(double? value, Color color, bool ring)
        {
            if (!value.HasValue || !double.IsFinite(value.Value) || Points.Count < 2) return;
            var p = Screen(new(Math.Clamp(value.Value, 0, MaximumInput), AdaptiveCurveProfiles.Interpolate(Points, value.Value)));
            using var pen = new Pen(color, 2);
            if (ring) g.DrawEllipse(pen, p.X - 8, p.Y - 8, 16, 16);
            else { using var brush = new SolidBrush(color); g.FillEllipse(brush, p.X - 4, p.Y - 4, 8, 8); }
        }
        Marker(LiveInput, Color.ForestGreen, false);
        Marker(MedianInput, Color.DarkViolet, true);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var index = Enumerable.Range(0, Points.Count).FirstOrDefault(i =>
        {
            var p = Screen(Points[i]); return Math.Abs(p.X - e.X) <= 10 && Math.Abs(p.Y - e.Y) <= 10;
        }, -1);
        if (index < 0) return;
        SelectedIndex = index; PointSelected?.Invoke(index);
        _dragOrigin = Points.ToArray(); _dragging = true; Capture = true; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || SelectedIndex < 0) return;
        var i = SelectedIndex;
        var minimum = i == 0 ? 0 : Math.Floor(Points[i - 1].Input) + 1;
        var maximum = i == Points.Count - 1 ? MaximumInput : Math.Ceiling(Points[i + 1].Input) - 1;
        if (minimum > maximum) return;
        var x = Math.Clamp(Math.Round((e.X - Plot.Left) / Plot.Width * MaximumInput), minimum, maximum);
        var y = Math.Clamp(Math.Round(10 + (Plot.Bottom - e.Y) / Plot.Height * 40),
            i == 0 ? 10 : Math.Ceiling(Points[i - 1].Level),
            i == Points.Count - 1 ? 50 : Math.Floor(Points[i + 1].Level));
        var changed = Points.ToArray(); changed[i] = new(x, y); Points = changed; Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        FinishDrag();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) FinishDrag();
    }
    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false; Capture = false;
        if (_dragOrigin is not null && !_dragOrigin.SequenceEqual(Points)) DraftChanged?.Invoke(Points.ToArray());
        _dragOrigin = null;
    }
}
