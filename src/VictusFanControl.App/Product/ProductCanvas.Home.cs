using System.Drawing.Drawing2D;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    private readonly ProductHomeTelemetry _homeTelemetry = new();
    private readonly ProductFanReadouts _fanReadouts = new();
    private readonly ProductAnimationFramePump _fanAnimationPump;
    private readonly ProductAnimationTiming _fanAnimationTiming=new();
    internal static double AnimationClockMilliseconds => System.Diagnostics.Stopwatch.GetTimestamp()*1000d/System.Diagnostics.Stopwatch.Frequency;
    private ProductFanRotor? _fanRotor;
    private ProductFanSurface? _cpuFanSurface,_gpuFanSurface;
    private Bitmap? _homeFanBackground,_homeFanOverlay;
    private string? _homeFanLayerKey;
    private static readonly RectangleF HomeFanLayerBounds=new(332,167,822,246);
    private double? _fanAnimationLastTick;
    private readonly ProductFanAnimation _cpuFanAnimation = new(), _gpuFanAnimation = new();
    internal bool HomeAnimationRunning => _fanAnimationPump?.IsRunning==true;
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
        if(running) { _fanAnimationLastTick ??= AnimationClockMilliseconds; _fanAnimationPump?.Start(); }
        else PauseFanAnimation();
        if(_cpuFanSurface is not null)_cpuFanSurface.Visible=Page==ProductPage.Home;
        if(_gpuFanSurface is not null)_gpuFanSurface.Visible=Page==ProductPage.Home;
    }
    private void PauseFanAnimation()
    {
        _fanAnimationPump?.Stop(); _fanAnimationLastTick=null;_fanAnimationTiming.Pause();
        // Freeze position and momentum; resume without hidden-time catch-up or a new soft start.
    }
    internal void AdvanceHomeAnimation(double clock,DateTimeOffset now)
    {
        if(!HomeIsVisible) { PauseFanAnimation(); return; }
        var elapsed=_fanAnimationLastTick is {} previous && clock>=previous ? Math.Min(0.1,(clock-previous)/1000d) : 0;
        _fanAnimationLastTick=clock;
        var cpu=CpuFanReading(now);var gpu=GpuFanReading(now);
        _cpuFanAnimation.Advance(elapsed,cpu?.Rpm);_gpuFanAnimation.Advance(elapsed,gpu?.Rpm);
        if(cpu?.Rpm is not >0 && gpu?.Rpm is not >0 &&
            !_cpuFanAnimation.IsMoving && !_gpuFanAnimation.IsMoving) PauseFanAnimation();
        // Each child owns a small opaque back buffer. Do not invalidate the parent canvas.
        if(_cpuFanSurface is not null){_cpuFanSurface.Phase=_cpuFanAnimation.Phase;_cpuFanSurface.Invalidate();_cpuFanSurface.Update();}
        if(_gpuFanSurface is not null){_gpuFanSurface.Phase=_gpuFanAnimation.Phase;_gpuFanSurface.Invalidate();_gpuFanSurface.Update();}
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e);SynchronizeFanAnimation(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e);SynchronizeFanAnimation(); }
    protected override void OnHandleDestroyed(EventArgs e) { PauseFanAnimation();base.OnHandleDestroyed(e); }

    private bool PaintHomeAnimation(Graphics g,Rectangle clip)
    {
        var region=new RectangleF((clip.Left-_offsetX)/_scale,(clip.Top-_offsetY)/_scale,clip.Width/_scale,clip.Height/_scale);
        if(Page!=ProductPage.Home||!new RectangleF(332,167,822,246).Contains(region))return false;
        DrawHomeFans(g);return true;
    }
    internal long HomePaintedFrames => _fanAnimationTiming.PaintedFrames;
    internal object HomeAnimationDiagnostic()=>_fanAnimationTiming.Snapshot(_fanAnimationPump.Scheduler,_fanAnimationPump.CoalescedRequests);
    private void DrawHomeFans(Graphics g)
    {
        var s=FreshSnapshot;var now=DateTimeOffset.UtcNow;
        var cpu=CpuFanReading(now);var gpu=GpuFanReading(now);
        var key=$"{_scale:R}|{Accent.ToArgb()}|{cpu?.Rpm}|{gpu?.Rpm}|{s?.CpuControlTemperatureC}|{s?.CpuLoadPercent}|{s?.GpuTemperatureC}|{s?.GpuLoadPercent}";
        var changed=_homeFanBackground is null||_homeFanLayerKey!=key;
        if(changed)
        {
            _homeFanBackground?.Dispose();_homeFanOverlay?.Dispose();
            var width=(int)Math.Ceiling(HomeFanLayerBounds.Width*_scale);var height=(int)Math.Ceiling(HomeFanLayerBounds.Height*_scale);
            _homeFanBackground=new(width,height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            _homeFanOverlay=new(width,height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);_homeFanLayerKey=key;
            using var bg=Graphics.FromImage(_homeFanBackground);using var overlay=Graphics.FromImage(_homeFanOverlay);
            foreach(var layer in new[]{bg,overlay})
            {layer.Clear(Color.Transparent);layer.SmoothingMode=SmoothingMode.AntiAlias;layer.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                layer.ScaleTransform(_scale,_scale);layer.TranslateTransform(-HomeFanLayerBounds.X,-HomeFanLayerBounds.Y);}
            Card(bg,new(310,150,1337,281));
            HomeFanStatic(bg,overlay,new(335,170,397,239),"CPU",cpu,s?.CpuControlTemperatureC,s?.CpuLoadPercent);
            HomeFanStatic(bg,overlay,new(754,170,397,239),"GPU",gpu,s?.GpuTemperatureC,s?.GpuLoadPercent);
        }
        g.DrawImage(_homeFanBackground!,HomeFanLayerBounds);
        _fanRotor??=new();
        _cpuFanSurface??=CreateFanSurface();_gpuFanSurface??=CreateFanSurface();
        PrepareFanSurface(_cpuFanSurface,533.5f,changed,_cpuFanAnimation.Phase);
        PrepareFanSurface(_gpuFanSurface,952.5f,changed,_gpuFanAnimation.Phase);
        g.DrawImage(_homeFanOverlay!,HomeFanLayerBounds);

    }

    private ProductFanSurface CreateFanSurface()
    {
        var surface=new ProductFanSurface(_fanRotor!);Controls.Add(surface);surface.BringToFront();return surface;
    }
    private void PrepareFanSurface(ProductFanSurface surface,float cx,bool changed,double phase)
    {
        // Pixel registration uses the same cached layer origin as static text/hub.
        var source=new Rectangle((int)Math.Round((cx-90-HomeFanLayerBounds.X)*_scale),
            (int)Math.Round((293-90-HomeFanLayerBounds.Y)*_scale),(int)Math.Ceiling(180*_scale),(int)Math.Ceiling(180*_scale));
        var bounds=new Rectangle((int)Math.Round(_offsetX+HomeFanLayerBounds.X*_scale)+source.X,
            (int)Math.Round(_offsetY+HomeFanLayerBounds.Y*_scale)+source.Y,source.Width,source.Height);
        var resized=surface.Bounds!=bounds;surface.Bounds=bounds;
        if(changed||resized)surface.SetLayers(_homeFanBackground!,_homeFanOverlay!,source,_scale,Accent);
        surface.Phase=phase;surface.Visible=Page==ProductPage.Home;
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
    private void HomeFanStatic(Graphics background,Graphics g,RectangleF r,string title,ProductFanReading? reading,double? temperature,double? load)
    {
        DrawText(background,title+" · Ventilador",r.X,r.Y,23,null,r.Width,true,true);
        var cx=r.X+r.Width/2;var cy=r.Y+123;
        using(var ring=new Pen(Border,2))background.DrawEllipse(ring,cx-88,cy-88,176,176);
        using(var hub=new SolidBrush(Background))g.FillEllipse(hub,cx-55,cy-55,110,110);
        using(var outline=new Pen(Accent,2))g.DrawEllipse(outline,cx-55,cy-55,110,110);
        DrawText(g,reading?.Rpm.ToString("0")??"Esperando",cx-54,cy-23,reading is null?18:30,Ink,108,true,true,height:40);
        DrawText(g,"RPM",cx-48,cy+17,15,Muted,96,centered:true,height:25);
        DrawText(background,Value(temperature,"°C")+"  ·  Uso "+Value(load,"%"),r.X,r.Bottom-23,18,Muted,r.Width,centered:true,height:25);
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
