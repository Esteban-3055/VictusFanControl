using System.Drawing.Drawing2D;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    private readonly ProductHomeTelemetry _homeTelemetry = new();
    private readonly ProductFanReadouts _fanReadouts = new();
    private readonly System.Windows.Forms.Timer _fanAnimationTimer = new() { Interval=16 };
    private long? _fanAnimationLastTick;
    private readonly ProductFanAnimation _cpuFanAnimation = new(), _gpuFanAnimation = new();
    internal bool HomeAnimationRunning => _fanAnimationTimer.Enabled;
    internal (double Cpu,double Gpu) HomeFanPhases => (_cpuFanAnimation.Phase,_gpuFanAnimation.Phase);
    internal (double Cpu,double Gpu) HomeFanAnimationSpeeds => (_cpuFanAnimation.Speed,_gpuFanAnimation.Speed);
    internal double HomeAnimationSecondsPerRevolution => _cpuFanAnimation.SecondsPerRevolution;
    internal void SetHomeAnimationSecondsPerRevolution(double seconds)
    {
        _cpuFanAnimation.TrySetSecondsPerRevolution(seconds);
        _gpuFanAnimation.TrySetSecondsPerRevolution(seconds);
    }
    internal IReadOnlyList<ProductHomeDemandSample> HomeDemandSamples => _homeTelemetry.Samples;
    internal ProductFanReading? CpuFanReading(DateTimeOffset now) => _fanReadouts.Cpu(State,now);
    internal ProductFanReading? GpuFanReading(DateTimeOffset now) => _fanReadouts.Gpu(State,now);
    internal void ObserveHomeDemand(ProductRuntimeState state,DateTimeOffset now) => _homeTelemetry.Observe(state,now);
    internal RectangleF RenderedCurveBounds { get; private set; }
    internal static RectangleF EditorCurveBounds => new(958,298,634,350);

    internal ProductPowerProfile HomeCurveSource => State.Source=="Battery"?ProductPowerProfile.Battery:ProductPowerProfile.Ac;
    internal bool HomeCurveUsesApplied => State.Runtime=="Healthy" && State.FanMode=="Automatic" && State.FanAuthority=="Custom" &&
        !State.LifecycleBlocked && !State.AutomaticPreparing && State.Source is "Ac" or "Battery" &&
        State.AppliedFanProfile==State.Source && State.AppliedAutomaticConfiguration is not null;
    internal FanConfiguration HomeCurveConfiguration => HomeCurveUsesApplied?State.AppliedAutomaticConfiguration!:Profiles.Get(HomeCurveSource).Fan;

    private bool HomeIsVisible => !IsDisposed && IsHandleCreated && Visible && Page == ProductPage.Home &&
        FindForm() is { Visible:true } form && form.WindowState != FormWindowState.Minimized;
    internal void SynchronizeFanAnimation()
    {
        var now=DateTimeOffset.UtcNow;
        var running=HomeIsVisible &&
            (CpuFanReading(now)?.Rpm>0 || GpuFanReading(now)?.Rpm>0 || _cpuFanAnimation.IsMoving || _gpuFanAnimation.IsMoving);
        if(running) { _fanAnimationLastTick ??= Environment.TickCount64; _fanAnimationTimer.Start(); }
        else PauseFanAnimation();
    }
    private void PauseFanAnimation()
    {
        _fanAnimationTimer.Stop(); _fanAnimationLastTick=null;
        // Freeze position and momentum; resume without hidden-time catch-up or a new soft start.
    }
    internal void AdvanceHomeAnimation(long clock,DateTimeOffset now)
    {
        if(!HomeIsVisible) { PauseFanAnimation(); return; }
        var elapsed=_fanAnimationLastTick is {} previous && clock>=previous ? Math.Min(0.1,(clock-previous)/1000d) : 0;
        _fanAnimationLastTick=clock;
        var cpu=CpuFanReading(now);var gpu=GpuFanReading(now);
        _cpuFanAnimation.Advance(elapsed,cpu?.Rpm);_gpuFanAnimation.Advance(elapsed,gpu?.Rpm);
        if(cpu?.Rpm is not >0 && gpu?.Rpm is not >0 &&
            !_cpuFanAnimation.IsMoving && !_gpuFanAnimation.IsMoving) PauseFanAnimation();
        Invalidate(Rectangle.Ceiling(new RectangleF(_offsetX+334*_scale,_offsetY+169*_scale,818*_scale,242*_scale)));
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e);SynchronizeFanAnimation(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e);SynchronizeFanAnimation(); }
    protected override void OnHandleDestroyed(EventArgs e) { PauseFanAnimation();base.OnHandleDestroyed(e); }

    private bool PaintHomeAnimation(Graphics g,Rectangle clip)
    {
        var region=new RectangleF((clip.Left-_offsetX)/_scale,(clip.Top-_offsetY)/_scale,clip.Width/_scale,clip.Height/_scale);
        if(Page!=ProductPage.Home||!new RectangleF(332,167,822,246).Contains(region))return false;
        Card(g,new(310,150,1337,281));DrawHomeFans(g);return true;
    }
    private void DrawHomeFans(Graphics g)
    {
        var s=FreshSnapshot;var now=DateTimeOffset.UtcNow;
        HomeFan(g,new(335,170,397,239),"CPU",CpuFanReading(now),s?.CpuControlTemperatureC,s?.CpuLoadPercent,_cpuFanAnimation.Phase);
        HomeFan(g,new(754,170,397,239),"GPU",GpuFanReading(now),s?.GpuTemperatureC,s?.GpuLoadPercent,_gpuFanAnimation.Phase);
    }

    private void Home(Graphics g)
    {
        DrawText(g,"Inicio",319,87,31,null,800,true);
        DrawText(g,State.LifecycleBlocked?"Sesión interrumpida":State.FanMode=="Automatic"?"Automático · "+SourceText():State.FanMode+" · "+SourceText(),850,93,21,State.LifecycleBlocked?Yellow:Muted,770,centered:false);
        Card(g,new(310,150,1337,281));
        var now=DateTimeOffset.UtcNow;var s=FreshSnapshot;
        DrawHomeFans(g);
        DrawText(g,"Estado del sistema",1200,174,24,null,421,true);
        DrawText(g,s is not null?"Telemetría disponible":State.Runtime=="Healthy"?"Esperando datos vigentes":State.Runtime,1200,219,22,s is not null?Green:Yellow,420);
        DrawText(g,"CPU · "+AppliedCpu(),1200,260,19,DomainColor(State.CpuState,Green),420);
        DrawText(g,"GPU · "+AppliedGpu(),1200,299,19,DomainColor(State.GpuState,Accent),420);
        DrawText(g,"Ventiladores · "+State.FanAuthority,1200,338,19,Muted,420);
        DrawText(g,"Giro según RPM de cada ventilador",1200,377,17,Muted,420);

        Card(g,new(310,451,655,379));Card(g,new(985,451,662,379));
        var source=HomeCurveSource;
        var current=ProductHomeTelemetry.CurrentDemand(State,now);
        var applied=HomeCurveUsesApplied;
        var configuration=HomeCurveConfiguration;
        DrawText(g,(applied?"Curva aplicada":"Curva del perfil")+" · "+(State.Source=="Battery"?"Batería":State.Source=="Ac"?"AC":"fuente sin confirmar"),334,470,24,null,610,true);
        IReadOnlyList<ProductDemandMarker> markers=current is null?[]:[new(current.Percent,State.FanLevel!.Value,true,State.AutomaticDecision!.UnifiedDemand!)];
        DrawCurve(g,new(375,554,530,184),false,markers,configuration,source,markerCaption:current is null?"Sin demanda automática vigente":$"● Solicitud: {State.FanLevel:0} · demanda {current.Percent:0.0} %");
        DrawText(g,applied?"Perfil en uso; Curvas permite editar y aplicar cambios.":"Automático mostrará aquí su curva aplicada.",334,807,15,Muted,610,height:20);
        HomeDemandGraph(g,new(985,451,662,379),current,now);
    }
    private void HomeFan(Graphics g,RectangleF r,string title,ProductFanReading? reading,double? temperature,double? load,double phase)
    {
        DrawText(g,title+" · Ventilador",r.X,r.Y,23,null,r.Width,true,true);
        var cx=r.X+r.Width/2;var cy=r.Y+123;
        using(var ring=new Pen(Border,2))g.DrawEllipse(ring,cx-88,cy-88,176,176);
        var saved=g.Save();g.TranslateTransform(cx,cy);g.RotateTransform((float)phase);
        using(var bladeBrush=new SolidBrush(Accent))
        using(var blade=new GraphicsPath())
        {
            blade.AddBezier(0,-53,-7,-62,-19,-75,-15,-85);
            blade.AddBezier(-15,-85,-5,-81,4,-65,4,-53);blade.CloseFigure();
            for(int i=0;i<28;i++){g.FillPath(bladeBrush,blade);g.RotateTransform(360f/28);}
        }
        g.Restore(saved);
        using(var hub=new SolidBrush(Background))g.FillEllipse(hub,cx-55,cy-55,110,110);
        using(var outline=new Pen(Accent,2))g.DrawEllipse(outline,cx-55,cy-55,110,110);
        DrawText(g,reading?.Rpm.ToString("0")??"Esperando",cx-54,cy-23,reading is null?18:30,Ink,108,true,true,height:40);
        DrawText(g,"RPM",cx-48,cy+17,15,Muted,96,centered:true,height:25);
        DrawText(g,Value(temperature,"°C")+"  ·  Uso "+Value(load,"%"),r.X,r.Bottom-23,18,Muted,r.Width,centered:true,height:25);
    }
    private void HomeDemandGraph(Graphics g,RectangleF card,ProductHomeDemandSample? current,DateTimeOffset now)
    {
        DrawText(g,"Demanda en tiempo real",card.X+24,card.Y+19,24,null,615,true);
        DrawText(g,current is null?"Sin demanda automática vigente":$"{current.Percent:0.0} % · demanda del controlador",card.X+24,card.Y+59,19,current is null?Muted:Accent,615);
        var plot=new RectangleF(card.X+66,card.Y+114,552,187);
        using var grid=new Pen(Border);
        for(int i=0;i<=4;i++){var y=plot.Bottom-i*plot.Height/4;g.DrawLine(grid,plot.Left,y,plot.Right,y);DrawText(g,(i*25).ToString(),plot.Left-42,y-11,16,Muted,40,height:24);}
        for(int i=0;i<=5;i++){var x=plot.Left+i*plot.Width/5;g.DrawLine(grid,x,plot.Top,x,plot.Bottom);DrawText(g,i==5?"Ahora":"−"+(5-i)+" min",x-25,plot.Bottom+12,15,Muted,65,height:25);}
        using var pen=new Pen(Accent,3);
        var points=new List<PointF>();
        void Flush()
        {
            if(points.Count>1)g.DrawLines(pen,points.ToArray());
            else if(points.Count==1){using var dot=new SolidBrush(Accent);g.FillEllipse(dot,points[0].X-2,points[0].Y-2,4,4);}
            points.Clear();
        }
        foreach(var sample in _homeTelemetry.Samples)
        {
            var age=(now-sample.Timestamp).TotalSeconds;if(age<0||age>300){Flush();continue;}
            if(sample.StartsSegment)Flush();
            points.Add(new(plot.Right-(float)(age/300)*plot.Width,plot.Bottom-(float)(sample.Percent/100)*plot.Height));
        }
        Flush();
        if(!_homeTelemetry.Samples.Any(s=>now>=s.Timestamp&&now-s.Timestamp<=TimeSpan.FromMinutes(5)))
            DrawText(g,"Esperando decisiones de Automático…",plot.Left+15,plot.Top+73,18,Muted,525,height:40);
        DrawText(g,"● Demanda total · últimos 5 minutos",card.X+24,card.Bottom-30,16,Accent,615,height:24);
    }
}
