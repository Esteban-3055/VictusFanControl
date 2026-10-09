using System.Drawing.Drawing2D;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class TelemetryHistoryChart : System.Windows.Forms.Control
{
    private readonly Queue<TelemetrySnapshot> _samples = new();
    internal TelemetryHistoryChart()
    {
        Dock = DockStyle.Fill; MinimumSize = new Size(450,190); Height = 230;
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw,true);
        AccessibleName = "Historial real de ventiladores y temperatura CPU";
    }
    internal void Add(TelemetrySnapshot snapshot)
    {
        if (_samples.LastOrDefault()?.Timestamp >= snapshot.Timestamp) return;
        if (_samples.LastOrDefault() is { } last && (snapshot.Timestamp-last.Timestamp).TotalSeconds > 3) _samples.Clear();
        _samples.Enqueue(snapshot);
        while (_samples.Count > 180) _samples.Dequeue();
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using var ink=new SolidBrush(DashboardTheme.Muted);
        g.DrawString("HISTORIAL  ·  CPU RPM / GPU RPM / CPU °C",Font,ink,16,10);
        var plot=new RectangleF(56,42,Math.Max(1,Width-105),Math.Max(1,Height-88));
        using var grid=new Pen(Color.FromArgb(58,58,67));
        for(var i=0;i<=4;i++)
        {
            var y=plot.Bottom-i*plot.Height/4;g.DrawLine(grid,plot.Left,y,plot.Right,y);
            g.DrawString((i*1500).ToString(),Font,ink,3,y-7);
            g.DrawString((i*25).ToString(),Font,ink,plot.Right+5,y-7);
        }
        var samples=_samples.ToArray();
        if(samples.Length<2){g.DrawString("Esperando muestras nuevas…",Font,ink,plot.Left+24,plot.Top+32);return;}
        var start=samples[0].Timestamp;var seconds=Math.Max(1,(samples[^1].Timestamp-start).TotalSeconds);
        void Draw(Func<TelemetrySnapshot,double?> selector,double max,Color color)
        {
            using var pen=new Pen(color,2);
            PointF? previous=null;
            foreach(var s in samples)
            {
                var v=selector(s);
                if(!v.HasValue || !double.IsFinite(v.Value)){previous=null;continue;}
                var p=new PointF(plot.Left+(float)((s.Timestamp-start).TotalSeconds/seconds)*plot.Width,
                    plot.Bottom-(float)(Math.Clamp(v.Value,0,max)/max)*plot.Height);
                if(previous.HasValue)g.DrawLine(pen,previous.Value,p);previous=p;
            }
        }
        Draw(s=>s.CpuFanRpm,6000,DashboardTheme.Cyan);
        Draw(s=>s.GpuFanRpm,6000,DashboardTheme.Accent);
        Draw(s=>s.CpuControlTemperatureC,100,Color.Orange);
        g.DrawString($"{samples[0].Timestamp.ToLocalTime():HH:mm:ss}                         {samples[^1].Timestamp.ToLocalTime():HH:mm:ss}",Font,ink,plot.Left,plot.Bottom+10);
    }
}
