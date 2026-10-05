using System.Drawing.Drawing2D;
using System.Drawing.Text;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Performance;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal enum ProductPage { Home, Fans, Performance, Profiles, Curves, Monitoring, Settings }
internal sealed record ProductHit(string Id, RectangleF Bounds, string Label, bool Enabled, bool Slider = false, int Min = 0, int Max = 0);

/// <summary>Owner-drawn product surface in reference coordinates. All gestures edit drafts or emit semantic commands.</summary>
internal sealed class ProductCanvas : System.Windows.Forms.Control
{
    internal static readonly Color Background = Color.FromArgb(9,19,27), Surface = Color.FromArgb(15,26,35), Border = Color.FromArgb(43,62,77);
    internal static readonly Color Ink = Color.FromArgb(230,240,255), Muted = Color.FromArgb(167,192,218), Blue = Color.FromArgb(0,157,255), Green = Color.FromArgb(0,237,111), Red = Color.FromArgb(255,63,64), Yellow = Color.FromArgb(246,212,31);
    internal ProductProfiles Profiles { get; set; } = new();
    internal ProductRuntimeState State { get; set; } = new();
    internal ProductPowerProfile Editing { get; set; }
    internal ProductPage Page { get; set; }
    internal AdaptiveCurveAxis Axis { get; set; }
    internal int FanTab { get; set; }
    internal int PerformanceTab { get; set; }
    internal int MonitorTab { get; set; }
    internal int ManualLevel { get; set; } = 30;
    internal bool SimulationVisible {get;set;}
    internal bool SimulationRunning {get;set;}=true;
    internal ProductSimulationInputs SimulationInputs {get;set;}=new();
    internal ProductCurveSimulation Simulation {get;set;}=new(new ProductProfiles().Ac.Fan);
    internal int SelectedNode { get; set; } = -1;
    internal bool Dirty { get; set; }
    internal bool Busy { get; set; }
    internal bool StartupEnabled { get; set; }
    internal bool StartupKnown { get; set; }
    internal string Notice { get; set; } = "";
    internal event Action<string>? Command;
    internal event Action<string,int>? ValueEdited;
    internal event Action<int,double,int>? NodeEdited;
    private readonly List<ProductHit> _hits = [];
    private readonly List<TelemetrySnapshot> _history = [];
    private RectangleF _plot;
    private float _scale = 1, _offsetX, _offsetY;
    private ProductHit? _dragSlider;
    private int _dragNode = -1;
    private (ProductPage,ProductPowerProfile,AdaptiveCurveAxis,int,int,bool)? _dragContext;
    private (ProductPage,ProductPowerProfile,AdaptiveCurveAxis,int,int,bool) DragContext => (Page,Editing,Axis,FanTab,PerformanceTab,SimulationVisible);
    private string? _keyboardId;
    private readonly Dictionary<int,Font> _fonts = [];
    internal IReadOnlyList<ProductHit> Hits => _hits;
    internal ProductCanvas()
    {
        DoubleBuffered = true; Dock = DockStyle.Fill; TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AccessibleName = "VictusFanControl"; AccessibleRole = AccessibleRole.Pane;
        BackColor = Background; MinimumSize = new(1000,560);
    }
    internal void AddSnapshot(TelemetrySnapshot s)
    {
        if (s.Timestamp > DateTimeOffset.UtcNow || _history.LastOrDefault()?.Timestamp >= s.Timestamp) return;
        _history.Add(s); var cutoff = s.Timestamp.AddMinutes(-5); _history.RemoveAll(x=>x.Timestamp < cutoff);
        if (_history.Count > 600) _history.RemoveRange(0,_history.Count-600);
    }
    private Font F(int size) { if (!_fonts.TryGetValue(size,out var font)) _fonts[size] = font = new("Segoe UI",size,FontStyle.Regular,GraphicsUnit.Pixel); return font; }
    private void DrawText(Graphics g,string text,float x,float y,int size=23,Color? color=null,float width=1200,bool bold=false,bool centered=false)
    {
        using var brush = new SolidBrush(color ?? Ink);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit, Alignment = centered?StringAlignment.Center:StringAlignment.Near };
        if (bold) { using var font = new Font(F(size),FontStyle.Bold); g.DrawString(text,font,brush,new RectangleF(x,y,width,size*2.7f),format); }
        else g.DrawString(text,F(size),brush,new RectangleF(x,y,width,size*2.7f),format);
    }
    private static GraphicsPath Rounded(RectangleF r,float radius)
    {
        var p=new GraphicsPath(); var d=radius*2; p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    private void Card(Graphics g,RectangleF r,bool selected=false,int radius=16)
    {
        using var path=Rounded(r,radius); using var fill=new LinearGradientBrush(r,selected?Color.FromArgb(8,53,99):Surface,selected?Color.FromArgb(7,39,72):Color.FromArgb(10,20,28),40);
        g.FillPath(fill,path);using var line=new Pen(selected?Blue:Border,selected?3:1.2f);g.DrawPath(line,path);
    }
    private void Hit(string id,RectangleF rect,string label,bool enabled=true,bool slider=false,int min=0,int max=0) => _hits.Add(new(id,rect,label,enabled,slider,min,max));
    private void Button(Graphics g,string id,string label,RectangleF r,bool primary=false,bool enabled=true)
    {
        enabled &= !Busy || id is "firmware" or "window-minimize" or "window-maximize" or "window-close"; Card(g,r,primary && enabled,10);
        if(primary && enabled){using var b=new SolidBrush(Color.FromArgb(0,111,244));using var p=Rounded(r,10);g.FillPath(b,p);}
        int size=23;while(size>17&&g.MeasureString(label,F(size)).Width>r.Width-24)size--;
        using(var brush=new SolidBrush(enabled?Ink:Color.FromArgb(92,115,138)))
        using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
            g.DrawString(label,F(size),brush,new RectangleF(r.X+10,r.Y,r.Width-20,r.Height),format);
        Hit(id,r,label,enabled);
    }
    private void Bar(Graphics g,RectangleF r,double? value,double max,Color color)
    {
        using var track=new SolidBrush(Color.FromArgb(31,50,66));using var p=Rounded(r,Math.Min(8,r.Height/2));g.FillPath(track,p);
        if(value is > 0){var fill=new RectangleF(r.X,r.Y,(float)(r.Width*Math.Clamp(value.Value/max,0,1)),r.Height); if(fill.Width>=r.Height){using var b=new SolidBrush(color);using var q=Rounded(fill,r.Height/2);g.FillPath(b,q);}}
    }
    private void Metric(Graphics g,string name,string value,RectangleF r,Color color,double? number=null,double max=100)
    { Card(g,r);DrawText(g,name,r.X+24,r.Y+16,22,Muted,r.Width-35);DrawText(g,value,r.X+24,r.Y+50,38,color,r.Width-35,true);Bar(g,new(r.X+24,r.Bottom-32,r.Width-48,14),number,max,color); }
    private void Icon(Graphics g,string type,float x,float y,float size,Color? color=null)
    {
        using var pen=new Pen(color ?? Muted,Math.Max(2,size/16));using var brush=new SolidBrush(color??Muted);
        if(type=="fan")
        {
            var state=g.Save();g.TranslateTransform(x+size/2,y+size/2);
            for(int i=0;i<4;i++){using var blade=new GraphicsPath();blade.AddBezier(0,-size*.08f,-size*.36f,-size*.06f,-size*.49f,-size*.39f,-size*.23f,-size*.46f);blade.AddBezier(-size*.23f,-size*.46f,size*.10f,-size*.54f,size*.12f,-size*.25f,0,-size*.08f);blade.CloseFigure();g.FillPath(brush,blade);g.RotateTransform(90);}
            g.FillEllipse(brush,-size*.1f,-size*.1f,size*.2f,size*.2f);g.Restore(state);
        }
        else if(type=="home") {g.DrawLines(pen,new PointF[]{new(x,y+size*.45f),new(x+size/2,y),new(x+size,y+size*.45f)});g.DrawRectangle(pen,x+size*.16f,y+size*.4f,size*.68f,size*.56f);}
        else if(type=="chart") {for(int i=0;i<4;i++)g.DrawLine(pen,x+i*size/4,y+size,x+i*size/4,y+size*(.8f-i*.2f));g.DrawLine(pen,x,y+size,x+size,y+size);}
        else if(type=="curve")g.DrawLines(pen,new PointF[]{new(x,y+size),new(x+size*.2f,y+size*.4f),new(x+size*.65f,y+size*.35f),new(x+size,y)});
        else if(type=="pulse")g.DrawLines(pen,new PointF[]{new(x,y+size/2),new(x+size*.2f,y+size/2),new(x+size*.35f,y),new(x+size*.5f,y+size),new(x+size*.7f,y+size*.4f),new(x+size,y+size*.4f)});
        else if(type=="plug") {g.DrawLine(pen,x+size*.3f,y,x+size*.3f,y+size*.3f);g.DrawLine(pen,x+size*.65f,y,x+size*.65f,y+size*.3f);g.FillRectangle(brush,x+size*.2f,y+size*.3f,size*.55f,size*.4f);g.DrawLine(pen,x+size*.48f,y+size*.7f,x+size*.48f,y+size);}
        else if(type=="cpu") {g.DrawRectangle(pen,x+size*.2f,y+size*.2f,size*.6f,size*.6f);for(int i=1;i<=3;i++){g.DrawLine(pen,x,y+size*i/4,x+size*.2f,y+size*i/4);g.DrawLine(pen,x+size*.8f,y+size*i/4,x+size,y+size*i/4);g.DrawLine(pen,x+size*i/4,y,x+size*i/4,y+size*.2f);g.DrawLine(pen,x+size*i/4,y+size*.8f,x+size*i/4,y+size);}}
        else if(type=="gpu") {g.DrawRectangle(pen,x,y+size*.15f,size*.85f,size*.6f);g.DrawEllipse(pen,x+size*.2f,y+size*.25f,size*.35f,size*.35f);g.DrawLine(pen,x+size*.85f,y+size*.25f,x+size,y+size*.25f);}
        else if(type=="settings") {var teeth=Enumerable.Range(0,32).Select(i=>{var angle=i*Math.PI/16;var radius=size*(i%4 is 1 or 2?.5:.38);return new PointF(x+size/2+(float)Math.Cos(angle)*(float)radius,y+size/2+(float)Math.Sin(angle)*(float)radius);}).ToArray();g.DrawPolygon(pen,teeth);g.DrawEllipse(pen,x+size*.35f,y+size*.35f,size*.3f,size*.3f);}
        else {g.DrawRectangle(pen,x,y,size*.8f,size);g.DrawLine(pen,x+size*.15f,y+size*.3f,x+size*.65f,y+size*.3f);g.DrawLine(pen,x+size*.15f,y+size*.55f,x+size*.5f,y+size*.55f);}
    }
    private static string Value(double? value,string unit,int decimals=0) => value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString("F"+decimals)+" "+unit : "— "+unit;
    private string ProfileName => Editing == ProductPowerProfile.Ac ? "AC" : "Batería";
    private ProductProfile Profile => Profiles.Get(Editing);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
        _scale=Math.Min(Width/1672f,Height/941f);_offsetX=(Width-1672*_scale)/2;_offsetY=(Height-941*_scale)/2;
        g.TranslateTransform(_offsetX,_offsetY);g.ScaleTransform(_scale,_scale);_hits.Clear();_plot=RectangleF.Empty;
        using(var b=new SolidBrush(Background))g.FillRectangle(b,0,0,1672,941);
        using(var p=new Pen(Border)) {g.DrawLine(p,0,64,1672,64);g.DrawLine(p,279,64,279,882);g.DrawLine(p,0,882,1672,882);}
        Icon(g,"fan",24,16,36,Blue);DrawText(g,"VictusFanControl",76,17,25,null,220,true);DrawText(g,"│",288,16,26,Muted,25);DrawText(g,State.Hardware,328,18,23,Muted,1070);
        Button(g,"window-minimize","−",new(1450,8,54,45));Button(g,"window-maximize","□",new(1524,8,54,45));Button(g,"window-close","×",new(1598,8,54,45));
        string[] names=["Inicio","Ventiladores","Rendimiento","Perfiles","Curvas","Monitorización","Configuración"];
        string[] icons=["home","fan","chart","profiles","curve","pulse","settings"];
        for(int i=0;i<names.Length;i++)
        {
            var rect=new RectangleF(8,85+i*76,262,70);if((int)Page==i){Card(g,rect,true,12);using var b=new SolidBrush(Blue);g.FillRectangle(b,8,rect.Y+5,5,60);}
            Icon(g,icons[i],36,rect.Y+20,35,(int)Page==i?Ink:Muted);DrawText(g,names[i],100,rect.Y+22,22,(int)Page==i?Ink:Muted,168);
            Hit("page-"+i,rect,names[i]);
        }
        if(Page is ProductPage.Performance or ProductPage.Profiles or ProductPage.Curves)
        {
            Button(g,"profile-ac","AC",new(1298,78,140,48),Editing==ProductPowerProfile.Ac);
            Button(g,"profile-battery","Batería",new(1450,78,190,48),Editing==ProductPowerProfile.Battery);
            DrawText(g,"Editando: "+ProfileName+"   ·   Fuente real: "+SourceText(),310,84,20,Muted,955);
        }
        switch(Page)
        {
            case ProductPage.Home: Home(g);break;
            case ProductPage.Fans: Fans(g);break;
            case ProductPage.Performance: Performance(g);break;
            case ProductPage.Profiles: ProfilePage(g);break;
            case ProductPage.Curves: Curves(g);break;
            case ProductPage.Monitoring: Monitoring(g);break;
            case ProductPage.Settings: Settings(g);break;
        }
        using(var b=new SolidBrush(FreshSnapshot is not null?Green:Yellow))g.FillEllipse(b,24,901,20,20);
        DrawText(g,"VictusFanControl v0.4.0  │  "+State.Target+"  │  "+State.FanMode+" · "+State.FanAuthority,60,901,19,Muted,1120);
        DrawText(g,State.LifecycleBlocked?"Sesión bloqueada por interrupción":FreshSnapshot is not null?"Telemetría validada":State.Runtime=="Healthy"?"Sin datos actuales":"Telemetría: "+State.Runtime,1210,901,18,State.LifecycleBlocked?Yellow:Muted,430);
        if(Busy || !string.IsNullOrWhiteSpace(Notice)) {DrawText(g,Busy?"Operación en curso…":Notice,305,849,18,Yellow,1330);}
        if(KeyboardHit is { } focused&&Focused){using var p=new Pen(Ink,2){DashStyle=DashStyle.Dot};g.DrawRectangle(p,focused.Bounds.X,focused.Bounds.Y,focused.Bounds.Width,focused.Bounds.Height);}
    }
    private TelemetrySnapshot? FreshSnapshot => State.Runtime == "Healthy" && State.Snapshot is { } snapshot &&
        DateTimeOffset.UtcNow >= snapshot.Timestamp && DateTimeOffset.UtcNow - snapshot.Timestamp <= VictusFanControl.Safety.SafetyGate.MaximumTelemetryAge ? snapshot : null;
    internal TelemetrySnapshot? CurrentSnapshot => FreshSnapshot;
    internal static Color DomainColor(string state,Color active) => state switch
    {"Active" or "ActiveUnverified"=>active,"Failed" or "Faulted"=>Red,"Applying" or "Recovering"=>Yellow,_=>Muted};
    private string SourceText() => State.Source switch { "Ac"=>"Conectada (AC)","Battery"=>"Batería",_=>"Desconocida" };
    private void Tabs(Graphics g,string prefix,string[] names,int selected)
    {
        for(int i=0;i<names.Length;i++){var x=300+i*265;DrawText(g,names[i],x+18,92,22,selected==i?Ink:Muted,248);Hit(prefix+i,new(x,76,265,61),names[i]);if(selected==i){using var b=new SolidBrush(Blue);g.FillRectangle(b,x,132,265,4);}}
    }
    private void Home(Graphics g)
    {
        Card(g,new(299,87,1352,329));DrawText(g,"Estado general",319,105,29,null,1000,true);
        string[] titles=["Ventiladores","CPU RAPL","GPU NVML","Fuente de energía"];
        string[] values=[State.FanMode,State.CpuState,State.GpuState,SourceText()];string[] icons=["fan","cpu","gpu","plug"];
        for(int i=0;i<4;i++){float x=319+i*329;Card(g,new(x,157,310,238));DrawText(g,titles[i],x+10,178,24,null,290,true,true);Icon(g,icons[i],x+123,230,62,i==1?DomainColor(State.CpuState,Green):i==2?DomainColor(State.GpuState,Blue):Muted);DrawText(g,values[i],x+10,311,25,i==1?DomainColor(State.CpuState,Green):i==2?DomainColor(State.GpuState,Blue):i==3?State.Source=="Unknown"?Yellow:Green:Ink,290,true,true);
            var small=i switch {0=>State.FanLevel.HasValue?"Nivel: "+State.FanLevel:"Autoridad: "+State.FanAuthority,1=>AppliedCpu(),2=>AppliedGpu(),_=>State.Source=="Unknown"?"Sin fuente confirmada":"Detectada por Windows"};DrawText(g,small,x+30,350,20,i==1?Green:Blue,275);}
        Card(g,new(299,435,1352,216));DrawText(g,"Temperaturas y uso",319,448,28,null,1000,true);var s=FreshSnapshot;
        Metric(g,"CPU",Value(s?.CpuControlTemperatureC,"°C"),new(319,494,310,136),Green,s?.CpuControlTemperatureC);
        Metric(g,"GPU",Value(s?.GpuTemperatureC,"°C"),new(648,494,310,136),Green,s?.GpuTemperatureC);
        Metric(g,"CPU Uso",Value(s?.CpuLoadPercent,"%"),new(977,494,310,136),Blue,s?.CpuLoadPercent);
        Metric(g,"GPU Uso",Value(s?.GpuLoadPercent,"%"),new(1306,494,326,136),Blue,s?.GpuLoadPercent);
        Card(g,new(299,671,1352,162));DrawText(g,"Ventiladores (RPM)",319,683,28,null,1100,true);
        Rpm(g,new(319,726,638,87),"CPU Fan",s?.CpuFanRpm);Rpm(g,new(977,726,655,87),"GPU Fan",s?.GpuFanRpm);
    }
    private void Rpm(Graphics g,RectangleF r,string title,double? rpm)
    {Card(g,r);Icon(g,"fan",r.X+30,r.Y+16,55);DrawText(g,title,r.X+124,r.Y+9,21,Muted,r.Width-140);DrawText(g,Value(rpm,"RPM"),r.X+124,r.Y+36,32,Blue,r.Width-140,true);Bar(g,new(r.X+124,r.Bottom-14,r.Width-155,8),rpm,6000,Blue);}
    internal string AppliedCpu()
    {
        if(State.CpuState=="Disabled")return "Sin límite aplicado";
        var c=State.AppliedPerformance;if(c is null||!c.CpuEnabled||State.CpuState!="Active")return "Sin confirmación actual";
        return State.AppliedPerformanceSource=="Battery"?$"{c.BatteryPl1Watts} / {c.BatteryPl2Watts} W":State.AppliedPerformanceSource=="Ac"?$"{c.AcPl1Watts} / {c.AcPl2Watts} W":"Fuente desconocida";
    }
    internal string AppliedGpu()
    {
        if(State.GpuState=="Disabled")return "Sin límite aplicado";
        var c=State.AppliedPerformance;if(c is null||!c.GpuEnabled||State.GpuState!="ActiveUnverified")return "Sin confirmación actual";
        return State.AppliedPerformanceSource=="Battery"?$"210–{c.BatteryGpuMaximumMHz} MHz":State.AppliedPerformanceSource=="Ac"?$"210–{c.AcGpuMaximumMHz} MHz":"Fuente desconocida";
    }
    private void Fans(Graphics g)
    {
        Tabs(g,"fan-tab-",["Control de ventiladores","Estado y telemetría","Curvas","Reglas y seguridad"],FanTab);
        if(FanTab==1){TelemetryPage(g);return;}if(FanTab==2){Curves(g,true);return;}if(FanTab==3){SafetyPage(g);return;}
        Card(g,new(311,161,1338,268));DrawText(g,"Modo de control",333,181,26,null,1100,true);
        string[] modes=["Firmware","Manual","Automático"],captions=["Control del sistema (BIOS)","Nivel fijo para CPU y GPU","Curvas del perfil de fuente real"];
        string[] icons=["fan","profiles","curve"];
        for(int i=0;i<3;i++){var r=new RectangleF(334+i*433,224,411,182);bool selected=State.FanMode==(i==2?"Automatic":modes[i]);Card(g,r,selected);
            Icon(g,icons[i],r.X+175,r.Y+22,48,selected?Blue:Muted);DrawText(g,modes[i],r.X+35,r.Y+90,27,null,r.Width-60,true);DrawText(g,captions[i],r.X+35,r.Y+128,20,Muted,r.Width-60);
            bool enabled=i==0||i==1&&State.ManualAuthorized&&!State.LifecycleBlocked||i==2&&State.AutomaticAuthorized&&!State.LifecycleBlocked;
            Hit("fan-mode-"+i,r,modes[i],enabled&&(!Busy||i==0));if(!enabled)DrawText(g,"Aplicación bloqueada",r.X+235,r.Y+18,15,Yellow,160);}
        Card(g,new(312,450,583,378));DrawText(g,"Control manual",334,468,26,null,520,true);
        Slider(g,"manual", "Nivel CPU / GPU",new(339,535,510,115),ManualLevel,10,50,"");
        DrawText(g,"Editar el nivel no escribe en el hardware.",339,663,20,Muted,520);
        Button(g,"manual-apply","Aplicar nivel",new(339,734,226,70),true,State.ManualAuthorized&&State.FanMode=="Manual"&&State.Runtime=="Healthy"&&!State.LifecycleBlocked);
        Button(g,"firmware","Volver a Firmware",new(581,734,287,70));
        Card(g,new(915,450,734,378));DrawText(g,"Curva del perfil · "+ProfileName,937,468,25,null,685,true);
        DrawText(g,"Editando: "+ProfileName+" · Aplicado: "+(State.AppliedFanProfile??"Ninguno"),945,513,19,Muted,675);
        DrawCurve(g,new(972,590,625,145),false);
    }
    private void Slider(Graphics g,string id,string name,RectangleF r,int value,int min,int max,string unit,bool compact=false)
    {
        DrawText(g,name,r.X,r.Y,compact?20:24,null,r.Width-300);Card(g,new(r.Right-220,r.Y-9,155,compact?34:56),false,9);DrawText(g,value+" "+unit,r.Right-208,r.Y+1,compact?21:unit=="MHz"?22:27,null,130,true);
        var track=new RectangleF(r.X,r.Y+(compact?41:73),r.Width,compact?10:14);Bar(g,track,value-min,max-min,Blue);
        var px=track.Left+(float)(value-min)/(max-min)*track.Width;using var b=new SolidBrush(Ink);g.FillEllipse(b,px-(compact?13:17),track.Y-(compact?8:10),compact?26:34,compact?26:34);
        DrawText(g,min.ToString(),r.X,r.Y+(compact?62:99),compact?14:18,Muted,100);DrawText(g,max+" "+unit,r.Right-110,r.Y+(compact?62:99),compact?14:18,Muted,110);
        Hit(id,new(r.X,r.Y+(compact?31:58),r.Width,compact?34:42),name,!Busy,true,min,max);
        Button(g,id+"-minus","−",new(r.Right-282,r.Y-9,50,compact?34:56));Button(g,id+"-plus","+",new(r.Right-61,r.Y-9,50,compact?34:56));
    }
    private void Performance(Graphics g)
    {
        DrawText(g,"Rendimiento · "+ProfileName,320,137,29,null,1200,true);
        string[] tabs=["CPU RAPL","GPU NVML","Fuente de energía","Guardián"];
        for(int i=0;i<4;i++){Button(g,"perf-tab-"+i,tabs[i],new(320+i*270,183,252,52),PerformanceTab==i);}
        if(PerformanceTab==0)
        {
            Card(g,new(320,253,807,570));DrawText(g,"Límites de CPU (Intel RAPL)",350,276,29,null,740,true);
            Button(g,"cpu-toggle",Profiles.CpuEnabled?"✓  Control de CPU seleccionado":"○  Control de CPU desactivado",new(350,337,735,60),Profiles.CpuEnabled);
            Slider(g,"pl1","PL1 · Potencia sostenida",new(350,453,735,120),Profile.CpuPl1Watts,CpuPowerProductDefaults.MinimumPl1Watts,CpuPowerProductDefaults.MaximumConfigurablePl1Watts,"W");
            Slider(g,"pl2","PL2 · Potencia turbo",new(350,637,735,120),Profile.CpuPl2Watts,Math.Max(Profile.CpuPl1Watts,CpuPowerProductDefaults.MinimumPl2Watts),CpuPowerProductDefaults.MaximumConfigurablePl2Watts,"W");
        }
        else if(PerformanceTab==1)
        {
            Card(g,new(320,253,807,570));DrawText(g,"Límite GPU (NVIDIA NVML)",350,276,29,null,740,true);
            Button(g,"gpu-toggle",Profiles.GpuEnabled?"✓  Control de GPU seleccionado":"○  Control de GPU desactivado",new(350,337,735,60),Profiles.GpuEnabled);
            Slider(g,"gpu","Graphics clock máximo",new(350,470,735,120),Profile.GpuMaximumMHz,210,GpuProductPreferences.Maximum(Editing),"MHz");
            DrawText(g,"Clock mínimo: 210 MHz · límite configurado, no lectura del rango",350,635,21,Muted,735);
            DrawText(g,"ActiveUnverified significa Set aceptado. El rango locked completo no es observable en este driver.",350,687,21,Muted,735);
            DrawText(g,"Los valores personalizados permanecen cerrados hasta su calificación física.",350,768,19,Yellow,735);
        }
        else
        {
            Card(g,new(320,253,807,570));DrawText(g,PerformanceTab==2?"Fuente real y perfil aplicado":"Autoridad independiente",350,276,29,null,740,true);
            string[] lines=PerformanceTab==2?["Fuente real: "+SourceText(),"Perfil que editas: "+ProfileName,"Perfil CPU/GPU aplicado: "+(State.AppliedPerformance is null?"Ninguno":State.AppliedPerformanceSource),"CPU: "+AppliedCpu(),"GPU: "+AppliedGpu(),"La pestaña AC/Batería solo cambia la edición."]:
                ["Performance Guardian: "+State.GuardianState,"CPU: "+State.CpuState,"GPU: "+State.GpuState,"Fan authority: "+State.FanAuthority,"Journal y recuperación pertenecen al backend.","Fan Control y CPU/GPU no forman una transacción atómica."];
            for(int i=0;i<lines.Length;i++)DrawText(g,lines[i],350,359+i*64,24,i<3?Ink:Muted,735);
        }
        Card(g,new(1148,253,500,570));DrawText(g,"Estado y aplicación",1178,276,28,null,438,true);
        DrawText(g,"CPU: "+State.CpuState,1178,341,23,DomainColor(State.CpuState,Green),438);DrawText(g,"GPU: "+State.GpuState,1178,388,23,DomainColor(State.GpuState,Blue),438);
        DrawText(g,"Aplicado: "+AppliedCpu(),1178,438,22,Muted,438);DrawText(g,AppliedGpu(),1178,480,22,Muted,438);
        DrawText(g,"Editar y guardar no aplican hardware. Aplicar usa la fuente real y los valores en edición de ambos perfiles.",1178,537,22,Muted,438);
        Button(g,"save","Guardar configuración",new(1178,655,438,48),false);
        Button(g,"performance-apply","Aplicar CPU / GPU",new(1178,714,438,48),true,State.PerformanceSupported&&State.CanApplyPerformance&&(Profiles.CpuEnabled||Profiles.GpuEnabled));
        Button(g,"performance-release","Liberar CPU / GPU",new(1178,773,438,48),false,State.PerformanceProcessPresent);
    }
    private void ProfilePage(Graphics g)
    {
        DrawText(g,"Perfiles",320,140,30,null,800,true);DrawText(g,"Dos configuraciones independientes para ventilación y rendimiento",320,186,23,Muted,1240);
        Card(g,new(310,235,535,591));DrawText(g,"Fuente del perfil",335,259,27,null,485,true);
        for(int i=0;i<2;i++){var source=(ProductPowerProfile)i;var p=Profiles.Get(source);var rect=new RectangleF(334,327+i*163,488,137);Card(g,rect,Editing==source);
            Icon(g,"plug",rect.X+23,rect.Y+30,52,source==ProductPowerProfile.Ac?Blue:Muted);DrawText(g,source==ProductPowerProfile.Ac?"AC":"Batería",rect.X+111,rect.Y+21,28,null,340,true);
            DrawText(g,$"CPU: {p.CpuPl1Watts}/{p.CpuPl2Watts} W",rect.X+111,rect.Y+65,22,Muted,340);DrawText(g,$"GPU: 210–{p.GpuMaximumMHz} MHz",rect.X+111,rect.Y+98,20,Muted,340);
            Hit(i==0?"profile-ac":"profile-battery",rect,i==0?"Editar AC":"Editar Batería");}
        DrawText(g,"Seleccionar aquí no cambia la alimentación ni crea autoridad.",335,682,24,Muted,475);
        Card(g,new(867,235,782,591));DrawText(g,"Detalles · "+ProfileName,891,259,28,null,727,true);
        DrawText(g,"Curva multivariable",891,338,25,null,727,true);DrawText(g,"Temperatura, potencia y carga CPU/GPU. Se conserva el motor de demanda, suavizado y protección.",891,385,23,Muted,727);
        DrawText(g,$"CPU PL1 / PL2: {Profile.CpuPl1Watts} / {Profile.CpuPl2Watts} W",891,480,25,Green,727);
        DrawText(g,$"GPU máximo: {Profile.GpuMaximumMHz} MHz",891,535,25,Blue,727);
        Button(g,"edit-curve","Editar curva",new(891,617,341,67),true);Button(g,"edit-performance","Editar rendimiento",new(1250,617,371,67));
        Button(g,"save","Guardar ambos perfiles",new(891,730,424,65),true);Button(g,"discard","Descartar cambios",new(1331,730,290,65),false,Dirty);
    }
    private void Curves(Graphics g,bool insideFans=false)
    {
        if(!insideFans)DrawText(g,"Curvas de ventilador · "+ProfileName,319,137,29,null,770,true);
        Button(g,"curve-editor","Editor gráfico",new(1160,138,230,34),!SimulationVisible);Button(g,"curve-simulator","Simulador",new(1405,138,243,34),SimulationVisible);
        if(SimulationVisible){SimulationPage(g);return;}
        Card(g,new(308,179,539,652));Card(g,new(867,179,782,652));
        DrawText(g,"Seleccionar variable",331,198,25,null,490,true);
        string[] axes=["Temperatura CPU","Temperatura GPU","Potencia CPU","Potencia GPU","Carga CPU","Carga GPU"];
        for(int i=0;i<6;i++)Button(g,"axis-"+i,axes[i],new(331+(i%2)*249,251+(i/2)*52,238,43),(int)Axis==i);
        DrawText(g,"Puntos de la curva",331,419,25,null,365,true);var points=AdaptiveCurveProfiles.Curve(Profile.Fan.BuildPolicy(),Axis);
        var count=Math.Min(points.Count,7);var start=Math.Clamp(SelectedNode-3,0,Math.Max(0,points.Count-count));
        Button(g,"node-previous","‹",new(731,418,42,35),false,SelectedNode>0);Button(g,"node-next","›",new(781,418,42,35),false,SelectedNode<points.Count-1);
        var table=new RectangleF(333,461,489,288);Card(g,table,false,8);
        DrawText(g,"#",345,469,20,Muted,43);DrawText(g,"Entrada ("+Unit()+")",404,469,20,Muted,192);DrawText(g,"Nivel",650,469,20,Muted,145);
        using(var grid=new Pen(Border)){g.DrawLine(grid,390,table.Top,390,table.Bottom);g.DrawLine(grid,621,table.Top,621,table.Bottom);for(int row=1;row<8;row++)g.DrawLine(grid,333,461+row*36,822,461+row*36);}
        for(int i=0;i<count;i++){var index=start+i;var rect=new RectangleF(334,497+i*36,487,35);if(index==SelectedNode)Card(g,rect,true,6);
            DrawText(g,(index+1).ToString(),345,rect.Y+4,21,Muted,43);DrawText(g,points[index].Input.ToString("0"),445,rect.Y+4,21,Muted,168);DrawText(g,points[index].Level.ToString("0"),669,rect.Y+4,21,Muted,130);Hit("node-"+index,rect,"Punto "+(index+1));}
        Button(g,"node-add","+  Añadir punto",new(331,767,226,44),true,points.Count<64);Button(g,"node-remove","−  Quitar",new(567,767,116,44),false,SelectedNode>=0&&points.Count>2);Button(g,"curve-reset","Restablecer",new(693,767,131,44));
        DrawText(g,"Editor gráfico · arrastra los puntos",889,201,27,null,735,true);DrawCurve(g,new(958,298,634,350),true);
        var selected=SelectedNode>=0&&SelectedNode<points.Count?points[SelectedNode]:null;
        DrawText(g,selected is null?"Selecciona un nodo; flechas ajustan entrada/nivel.":$"Punto {SelectedNode+1}: {selected.Input:0} {Unit()} · nivel {selected.Level:0}",892,728,18,Muted,737);
        Button(g,"save","Guardar curvas",new(894,763,325,48),true);Button(g,"curve-defaults","Valores iniciales del perfil",new(1235,763,389,48));
    }
    private void SimulationPage(Graphics g)
    {
        Card(g,new(308,179,539,652));Card(g,new(867,179,782,652));
        DrawText(g,"Entradas sintéticas",331,198,26,null,490,true);
        string[] names=["CPU demanda","GPU temp.","CPU potencia","GPU potencia","CPU carga","GPU carga"];
        for(int i=0;i<6;i++)Slider(g,"sim-input-"+i,names[i],new(331,250+i*92,490,90),SimulationInputs.Value(i),0,(int)AdaptiveCurveProfiles.MaximumInput((AdaptiveCurveAxis)i),i<2?"°C":i<4?"W":"%",true);
        DrawText(g,"Simulación sin hardware · "+ProfileName,889,201,27,null,735,true);
        DrawText(g,"CPU de demanda: "+(Profile.Fan.Tuning.CpuTemperatureSource switch{CpuDemandTemperatureSource.CoreAverage=>"media de núcleos",CpuDemandTemperatureSource.PerformanceCoreAverage=>"media de P-Cores",CpuDemandTemperatureSource.HottestPerformanceCoresAverage=>"P-Cores más calientes",_=>"paquete/núcleo más caliente"}),889,238,18,Muted,730);
        var d=Simulation.Current;
        Metric(g,"Demanda cruda",Value(d?.RawDemandLevel,"",1),new(890,280,230,131),Red);
        Metric(g,"Filtrada (EMA)",Value(d?.SmoothedDemandLevel,"",1),new(1136,280,230,131),Blue);
        Metric(g,"Nivel calculado",d?.EqualFanLevel?.ToString()??"—",new(1382,280,240,131),Green);
        DrawText(g,$"Tiempo virtual: {Simulation.ElapsedSeconds} s · {(SimulationRunning?"en marcha":"pausado")} · carga: {d?.ObservedLoadSeconds??0:0} s",889,426,20,Muted,730);
        var plot=new RectangleF(955,490,637,158);using var grid=new Pen(Border);
        for(int i=0;i<=4;i++){var y=plot.Bottom-i*plot.Height/4;g.DrawLine(grid,plot.Left,y,plot.Right,y);DrawText(g,(10+i*10).ToString(),plot.Left-42,y-12,17,Muted,40);}
        void Series(Func<AdaptiveFanInertiaDecision,double?> select,Color color)
        {using var line=new Pen(color,2);var points=Simulation.History.Select(p=>new PointF(plot.Right-(Simulation.ElapsedSeconds-p.Seconds)/600f*plot.Width,plot.Bottom-(float)((select(p.Decision)!.Value-10)/40)*plot.Height)).ToArray();if(points.Length>1)g.DrawLines(line,points);}
        Series(d=>d.RawDemandLevel,Red);Series(d=>d.SmoothedDemandLevel,Blue);Series(d=>d.EqualFanLevel,Green);
        DrawText(g,"Últimos 600 s virtuales · cruda / EMA / nivel",889,662,19,Muted,730);
        DrawText(g,d?.ThermalOverride==true?"Override de demanda térmica":d?.SustainedLoadCooling==true?"Descenso de carga prolongada":"Respuesta con inercia y confirmación",889,697,20,d?.ThermalOverride==true?Yellow:Muted,730);
        Button(g,"sim-run",SimulationRunning?"Pausar":"Reanudar",new(889,738,160,44),SimulationRunning);Button(g,"sim-60","+ 60 s",new(1064,738,170,44));Button(g,"sim-1200","+ 20 min",new(1249,738,195,44));Button(g,"sim-reset","Reiniciar",new(1459,738,165,44));
        DrawText(g,"Modelo de demanda; no valida SafetyGate, RPM ni respuesta física.",889,798,17,Yellow,730);
    }
    private string Unit() => Axis is AdaptiveCurveAxis.CpuTemperature or AdaptiveCurveAxis.GpuTemperature?"°C":Axis is AdaptiveCurveAxis.CpuPower or AdaptiveCurveAxis.GpuPower?"W":"%";
    private void DrawCurve(Graphics g,RectangleF plot,bool editable)
    {
        _plot=editable?plot:RectangleF.Empty;double xmax=AdaptiveCurveProfiles.MaximumInput(Axis);
        using var grid=new Pen(Border);for(int i=0;i<=4;i++){float y=plot.Bottom-i*plot.Height/4;g.DrawLine(grid,plot.Left,y,plot.Right,y);DrawText(g,(10+i*10).ToString(),plot.Left-42,y-14,19,Muted,40);}
        for(int i=0;i<=5;i++){float x=plot.Left+i*plot.Width/5;g.DrawLine(grid,x,plot.Top,x,plot.Bottom);DrawText(g,(i*xmax/5).ToString("0"),x-13,plot.Bottom+12,19,Muted,66);}
        PointF Position(AdaptiveFanCurvePoint p)=>new(plot.Left+(float)(p.Input/xmax)*plot.Width,plot.Bottom-(float)((p.Level-10)/40)*plot.Height);
        var c=Profile.Fan.BuildPolicy();var other=Axis switch{AdaptiveCurveAxis.CpuTemperature=>AdaptiveCurveAxis.GpuTemperature,AdaptiveCurveAxis.GpuTemperature=>AdaptiveCurveAxis.CpuTemperature,AdaptiveCurveAxis.CpuPower=>AdaptiveCurveAxis.GpuPower,AdaptiveCurveAxis.GpuPower=>AdaptiveCurveAxis.CpuPower,AdaptiveCurveAxis.CpuLoad=>AdaptiveCurveAxis.GpuLoad,_=>AdaptiveCurveAxis.CpuLoad};
        foreach(var axis in new[]{other,Axis})
        {
            var ps=AdaptiveCurveProfiles.Curve(c,axis);var color=axis is AdaptiveCurveAxis.CpuTemperature or AdaptiveCurveAxis.CpuPower or AdaptiveCurveAxis.CpuLoad?Red:Blue;
            using var line=new Pen(color,axis==Axis?3:2){DashStyle=axis==Axis?DashStyle.Solid:DashStyle.Dash};
            var visible=ps.Where(p=>p.Input<=xmax).Select(Position).ToArray();if(visible.Length>1)g.DrawLines(line,visible);
            foreach(var point in visible){using var b=new SolidBrush(Ink);g.FillEllipse(b,point.X-6,point.Y-6,12,12);g.DrawEllipse(line,point.X-7,point.Y-7,14,14);}
            if(editable&&axis==Axis&&SelectedNode>=0&&SelectedNode<ps.Count){var selected=Position(ps[SelectedNode]);using var ring=new Pen(Yellow,3);g.DrawEllipse(ring,selected.X-12,selected.Y-12,24,24);}
        }
        DrawText(g,"Entrada ("+Unit()+")",plot.Left+plot.Width*.32f,plot.Bottom+46,20,Muted,300);
        DrawText(g,"Nivel común · 10–50",plot.Left,plot.Top-41,19,Muted,285);
        if(editable){var cpu=Axis is AdaptiveCurveAxis.CpuTemperature or AdaptiveCurveAxis.CpuPower or AdaptiveCurveAxis.CpuLoad;
            DrawText(g,cpu?"● CPU · editable":"● CPU · referencia",plot.Left+290,plot.Top-41,16,Red,165);
            DrawText(g,cpu?"● GPU · referencia":"● GPU · editable",plot.Left+465,plot.Top-41,16,Blue,169);}
    }
    private void TelemetryPage(Graphics g)
    {
        var s=FreshSnapshot;Card(g,new(310,157,1337,199));DrawText(g,"Temperaturas",332,174,27,null,1200,true);
        Metric(g,"CPU (Paquete)",Value(s?.CpuTemperatureC,"°C"),new(332,216,416,135),Green,s?.CpuTemperatureC);
        Metric(g,"CPU (Núcleo más caliente)",Value(s?.CpuCoreMaxTemperatureC,"°C"),new(766,216,416,135),Green,s?.CpuCoreMaxTemperatureC);
        Metric(g,"GPU",Value(s?.GpuTemperatureC,"°C"),new(1200,216,424,135),Green,s?.GpuTemperatureC);
        Card(g,new(310,376,1337,207));DrawText(g,"Uso y potencia",332,394,27,null,1200,true);
        Metric(g,"CPU Uso",Value(s?.CpuLoadPercent,"%"),new(332,444,310,135),Blue,s?.CpuLoadPercent);Metric(g,"GPU Uso",Value(s?.GpuLoadPercent,"%"),new(659,444,310,135),Blue,s?.GpuLoadPercent);
        Metric(g,"CPU Potencia",Value(s?.CpuPackagePowerW,"W"),new(985,444,310,135),Yellow,s?.CpuPackagePowerW,115);Metric(g,"GPU Potencia",Value(s?.GpuPowerW,"W"),new(1311,444,313,135),Yellow,s?.GpuPowerW,140);
        Card(g,new(310,604,1337,217));DrawText(g,"Ventiladores y estado",332,623,27,null,1200,true);Rpm(g,new(332,676,625,119),"CPU Fan",s?.CpuFanRpm);Rpm(g,new(976,676,648,119),"GPU Fan",s?.GpuFanRpm);
    }
    private void SafetyPage(Graphics g)
    {
        Card(g,new(310,157,1337,666));DrawText(g,"Reglas de seguridad",332,177,30,null,1250,true);
        DrawText(g,"Protecciones del backend · solo lectura",332,225,23,Muted,1240);
        string[] titles=["Respuesta térmica de demanda","SafetyGate independiente","Límites reales de ventiladores","Telemetría fresca y completa","Guardian y recuperación"];
        string[] details=["La curva y el filtrado conservan los overrides térmicos del motor vigente.","Temperaturas crudas conservan las protecciones CPU/GPU; editar la curva no las altera.","Manual WMI 10–50; automático preparado 30–50. Nivel no equivale a porcentaje ni a RPM exactas.","Una pérdida de admisión cancela comandos y solicita liberación.","Recovery no concede autoridad automáticamente. Los journals no se borran desde la GUI."];
        for(int i=0;i<5;i++){var r=new RectangleF(331,282+i*103,1293,87);Card(g,r);Icon(g,i==4?"profiles":"fan",r.X+24,r.Y+24,37);DrawText(g,titles[i],r.X+99,r.Y+12,23,null,1100,true);DrawText(g,details[i],r.X+99,r.Y+47,19,Muted,1100);}
    }
    private void Monitoring(Graphics g)
    {
        DrawText(g,"Monitorización (tiempo real)",310,88,31,null,1320,true);
        string[] labels=["Temperaturas","Uso y potencia","RPM","Niveles"];
        for(int i=0;i<4;i++)Button(g,"monitor-tab-"+i,labels[i],new(310+i*238,144,225,49),MonitorTab==i);
        Card(g,new(310,212,938,606));DrawText(g,"Historial real · últimos 5 minutos",334,229,25,null,865,true);
        if(MonitorTab==0){History(g,new(390,308,804,177),s=>s.CpuControlTemperatureC,s=>s.GpuTemperatureC,110,"Temperatura (°C)");History(g,new(390,597,804,144),s=>s.CpuLoadPercent,s=>s.GpuLoadPercent,100,"Uso (%)");}
        else if(MonitorTab==1){History(g,new(390,308,804,177),s=>s.CpuLoadPercent,s=>s.GpuLoadPercent,100,"Uso (%)");History(g,new(390,597,804,144),s=>s.CpuPackagePowerW,s=>s.GpuPowerW,150,"Potencia (W)");}
        else if(MonitorTab==2)History(g,new(390,325,804,375),s=>s.CpuFanRpm,s=>s.GpuFanRpm,6000,"RPM");
        else{DrawText(g,"Nivel aceptado: "+(State.FanLevel?.ToString()??"—"),350,352,33,Blue,830);DrawText(g,"Autoridad: "+State.FanAuthority,350,417,27,null,830);DrawText(g,"Las RPM son realimentación; no prueban el setpoint ni ownership.",350,492,25,Muted,830);}
        Card(g,new(1270,144,378,674));DrawText(g,"Valores actuales",1290,165,27,null,340,true);var s=FreshSnapshot;
        string[] names=["CPU Temp","GPU Temp","CPU Uso","GPU Uso","CPU Potencia","GPU Potencia","CPU Fan","GPU Fan"];
        string[] vals=[Value(s?.CpuControlTemperatureC,"°C"),Value(s?.GpuTemperatureC,"°C"),Value(s?.CpuLoadPercent,"%"),Value(s?.GpuLoadPercent,"%"),Value(s?.CpuPackagePowerW,"W"),Value(s?.GpuPowerW,"W"),Value(s?.CpuFanRpm,"RPM"),Value(s?.GpuFanRpm,"RPM")];
        for(int i=0;i<8;i++){Card(g,new(1288,215+i*72,342,61),false,11);DrawText(g,names[i],1304,229+i*72,19,Muted,160);DrawText(g,vals[i],1454,224+i*72,26,i%2==0?Red:Blue,167,true);}
    }
    private void History(Graphics g,RectangleF plot,Func<TelemetrySnapshot,double?> cpu,Func<TelemetrySnapshot,double?> gpu,double max,string title)
    {
        DrawText(g,title,plot.X-56,plot.Y-43,23,null,800,true);using var grid=new Pen(Border){DashStyle=DashStyle.Dash};
        for(int i=0;i<=5;i++){float y=plot.Bottom-i*plot.Height/5;g.DrawLine(grid,plot.Left,y,plot.Right,y);DrawText(g,(i*max/5).ToString("0"),plot.Left-49,y-13,18,Muted,48);}
        for(int i=0;i<=5;i++){float x=plot.Left+i*plot.Width/5;g.DrawLine(grid,x,plot.Top,x,plot.Bottom);DrawText(g,(-300+i*60).ToString(),x-22,plot.Bottom+12,18,Muted,55);}
        var now=DateTimeOffset.UtcNow;var cpuSeries=HistorySeries(cpu,now);var gpuSeries=HistorySeries(gpu,now);
        if(cpuSeries.Count==0&&gpuSeries.Count==0)DrawText(g,"Esperando muestras reales…",plot.Left+25,plot.Top+40,23,Muted,700);
        void Draw(IReadOnlyList<List<(double SecondsAgo,double Value)>> series,Color color)
        {using var pen=new Pen(color,3);foreach(var segment in series){var points=segment.Select(v=>new PointF(plot.Right-(float)(v.SecondsAgo/300)*plot.Width,plot.Bottom-(float)(Math.Clamp(v.Value,0,max)/max)*plot.Height)).ToArray();if(points.Length>1)g.DrawLines(pen,points);else if(points.Length==1){using var dot=new SolidBrush(color);g.FillEllipse(dot,points[0].X-2,points[0].Y-2,4,4);}}}
        Draw(cpuSeries,Red);Draw(gpuSeries,Blue);DrawText(g,"● CPU      ● GPU",plot.Left+280,plot.Bottom+39,19,Muted,400);
    }
    internal IReadOnlyList<List<(double SecondsAgo,double Value)>> HistorySeries(Func<TelemetrySnapshot,double?> select,DateTimeOffset now)
    {
        var segments=new List<List<(double SecondsAgo,double Value)>>();List<(double SecondsAgo,double Value)>? current=null;DateTimeOffset? previous=null;
        foreach(var sample in _history)
        {
            var age=(now-sample.Timestamp).TotalSeconds;var value=select(sample);
            if(age<0||age>300||!value.HasValue||!double.IsFinite(value.Value)){current=null;previous=null;continue;}
            if(current is null||previous is null||(sample.Timestamp-previous.Value).TotalSeconds>3){current=[];segments.Add(current);}
            current.Add((age,value.Value));previous=sample.Timestamp;
        }
        return segments;
    }
    private void Settings(Graphics g)
    {
        DrawText(g,"Configuración",320,88,31,null,1300,true);
        Card(g,new(310,153,1337,233));DrawText(g,"Inicio de la aplicación",334,178,28,null,1250,true);
        Button(g,"startup-toggle",StartupEnabled?"✓  Iniciar con Windows":"○  Iniciar con Windows",new(334,241,590,55),StartupEnabled,StartupKnown);
        Button(g,"minimized-toggle",Profiles.StartMinimized?"✓  Minimizar al iniciar":"○  Minimizar al iniciar",new(956,241,665,55),Profiles.StartMinimized);
        DrawText(g,"El inicio nunca aplica ventiladores ni límites CPU/GPU automáticamente.",334,326,23,Muted,1250);
        Card(g,new(310,406,1337,210));DrawText(g,"Interfaz y perfiles",334,431,28,null,1250,true);DrawText(g,"Tema oscuro · Español · perfiles AC/Batería",334,491,23,Muted,495);
        Button(g,"discard","Descartar cambios",new(846,478,310,68),false,Dirty);
        Button(g,"save","Guardar preferencias",new(1176,478,442,68),true);DrawText(g,Dirty?"Hay cambios sin guardar":"Preferencias guardadas",334,551,18,Dirty?Yellow:Green,475);
        Button(g,"profiles-import","Importar perfiles",new(846,555,310,40));Button(g,"profiles-export","Exportar perfiles",new(1176,555,442,40));
        Card(g,new(310,623,1337,201));DrawText(g,"Registros y estado",334,647,28,null,1250,true);
        DrawText(g,State.Failure??State.Message,334,704,21,State.Failure is null?Muted:Yellow,880);
        Button(g,"open-logs","Abrir carpeta de logs",new(1240,690,379,43));
        Button(g,"diagnostics-export","Exportar diagnóstico",new(1240,745,379,43));
        DrawText(g,"Detalle técnico: ventiladores "+State.FanAuthority+" · CPU "+State.CpuState+" · GPU "+State.GpuState,334,773,18,Muted,880);
    }
    private PointF Virtual(Point point)=>new((point.X-_offsetX)/_scale,(point.Y-_offsetY)/_scale);
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;Focus();var p=Virtual(e.Location);
        if(!_plot.IsEmpty&&!Busy)
        {
            var points=AdaptiveCurveProfiles.Curve(Profile.Fan.BuildPolicy(),Axis);var xmax=AdaptiveCurveProfiles.MaximumInput(Axis);
            for(int i=0;i<points.Count;i++){var px=_plot.Left+(float)(points[i].Input/xmax)*_plot.Width;var py=_plot.Bottom-(float)((points[i].Level-10)/40)*_plot.Height;if(Math.Abs(p.X-px)<17&&Math.Abs(p.Y-py)<17){_dragContext=DragContext;_dragNode=i;SelectedNode=i;_keyboardId="node-"+i;Capture=true;Invalidate();return;}}
        }
        var hit=_hits.LastOrDefault(h=>h.Bounds.Contains(p));if(hit is null||!CanInteract(hit))return;
        _keyboardId=hit.Id;
        if(hit.Slider){_dragContext=DragContext;_dragSlider=hit;Capture=true;EditSlider(hit,p);}
        else Command?.Invoke(hit.Id);
    }
    internal void PointerDown(Point point)=>OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
    internal void PointerMove(Point point)=>OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,point.X,point.Y,0));
    internal void PointerUp(Point point)=>OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
    internal bool IsHeaderDrag(Point point){var p=Virtual(point);return p.X>=0&&p.X<1430&&p.Y>=0&&p.Y<64;}
    private void EditSlider(ProductHit hit,PointF point) => ValueEdited?.Invoke(hit.Id,Math.Clamp((int)Math.Round(hit.Min+(point.X-hit.Bounds.Left)/hit.Bounds.Width*(hit.Max-hit.Min)),hit.Min,hit.Max));
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);var p=Virtual(e.Location);
        if(_dragContext.HasValue&&(_dragContext.Value!=DragContext||Busy)){_dragSlider=null;_dragNode=-1;_dragContext=null;Capture=false;return;}
        if(_dragSlider is not null){EditSlider(_dragSlider,p);return;}
        if(_dragNode>=0){var x=Math.Round(Math.Clamp((p.X-_plot.Left)/_plot.Width,0,1)*AdaptiveCurveProfiles.MaximumInput(Axis));var level=(int)Math.Round(10+Math.Clamp((_plot.Bottom-p.Y)/_plot.Height,0,1)*40);NodeEdited?.Invoke(_dragNode,x,level);return;}
        Cursor=_hits.Any(h=>CanInteract(h)&&h.Bounds.Contains(p))?Cursors.Hand:Cursors.Default;
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);_dragSlider=null;_dragNode=-1;_dragContext=null;Capture=false;}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){_dragSlider=null;_dragNode=-1;_dragContext=null;}}
    private bool CanInteract(ProductHit hit) => hit.Enabled&&(!Busy||hit.Id is "firmware" or "fan-mode-0" or "window-minimize" or "window-maximize" or "window-close");
    private ProductHit? KeyboardHit=>_hits.LastOrDefault(h=>h.Id==_keyboardId&&CanInteract(h));
    private int SliderValue(string id) => id.StartsWith("sim-input-")?SimulationInputs.Value(int.Parse(id[10..])):id switch{"manual"=>ManualLevel,"pl1"=>Profile.CpuPl1Watts,"pl2"=>Profile.CpuPl2Watts,"gpu"=>Profile.GpuMaximumMHz,_=>0};
    internal void HandleKey(Keys keyData)=>OnKeyDown(new KeyEventArgs(keyData));
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Tab or Keys.PageDown or Keys.PageUp or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if((keyData&Keys.KeyCode)==Keys.Tab){HandleKey(keyData);return true;}
        return base.ProcessDialogKey(keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if(e.KeyCode==Keys.Tab)
        {
            var enabled=_hits.Where(CanInteract).ToArray();if(enabled.Length==0)return;
            var current=KeyboardHit;var index=Array.IndexOf(enabled,current);
            index=index<0?(e.Shift?enabled.Length-1:0):(index+(e.Shift?-1:1)+enabled.Length)%enabled.Length;
            _keyboardId=enabled[index].Id;Invalidate();e.Handled=true;return;
        }
        if((e.KeyCode is Keys.Enter or Keys.Space)&&KeyboardHit is { } action)
        {if(!action.Slider)Command?.Invoke(action.Id);e.Handled=true;return;}
        if(!Busy&&!SimulationVisible&&(Page==ProductPage.Curves||Page==ProductPage.Fans&&FanTab==2)&&e.KeyCode is Keys.PageDown or Keys.PageUp or Keys.Home or Keys.End)
        {
            var count=AdaptiveCurveProfiles.Curve(Profile.Fan.BuildPolicy(),Axis).Count;
            var index=e.KeyCode==Keys.Home?0:e.KeyCode==Keys.End?count-1:Math.Clamp(SelectedNode+(e.KeyCode==Keys.PageDown?1:-1),0,count-1);
            _keyboardId="node-"+index;Command?.Invoke(_keyboardId);e.Handled=true;return;
        }
        if(Busy||e.KeyCode is not(Keys.Left or Keys.Right or Keys.Up or Keys.Down))return;
        if(KeyboardHit is { Slider:true } slider)
        {ValueEdited?.Invoke(slider.Id,Math.Clamp(SliderValue(slider.Id)+(e.KeyCode is Keys.Right or Keys.Up?1:-1),slider.Min,slider.Max));e.Handled=true;return;}
        if(!SimulationVisible&&(Page==ProductPage.Curves||Page==ProductPage.Fans&&FanTab==2)&&(KeyboardHit is null||_keyboardId=="node-"+SelectedNode))
        {
            var ps=AdaptiveCurveProfiles.Curve(Profile.Fan.BuildPolicy(),Axis);
            if(SelectedNode>=0&&SelectedNode<ps.Count){var p=ps[SelectedNode];NodeEdited?.Invoke(SelectedNode,p.Input+(e.KeyCode==Keys.Right?1:e.KeyCode==Keys.Left?-1:0),(int)p.Level+(e.KeyCode==Keys.Up?1:e.KeyCode==Keys.Down?-1:0));e.Handled=true;}
        }
    }
    protected override AccessibleObject CreateAccessibilityInstance()=>new ProductAccessible(this);
    private sealed class ProductAccessible(ProductCanvas owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount()=>owner._hits.Count;
        public override AccessibleObject? GetChild(int index)=>index>=0&&index<owner._hits.Count?new HitAccessible(owner,owner._hits[index]):null;
    }
    private sealed class HitAccessible(ProductCanvas owner,ProductHit hit) : AccessibleObject
    {
        // Resolve every action against the live surface: a screen reader can retain a child across repaints.
        private ProductHit? Current=>owner.IsDisposed?null:owner._hits.LastOrDefault(h=>h.Id==hit.Id);
        public override string? Name {get=>Current?.Label??hit.Label;set{}}
        public override AccessibleRole Role=>hit.Slider?AccessibleRole.Slider:AccessibleRole.PushButton;
        public override AccessibleStates State=>Current is { } current&&owner.CanInteract(current)?AccessibleStates.Focusable|(owner.Focused&&owner._keyboardId==current.Id?AccessibleStates.Focused:AccessibleStates.None):AccessibleStates.Unavailable;
        public override string? DefaultAction=>hit.Slider?"Ajustar":"Activar";
        public override string? Value
        {
            get=>Current is { Slider:true } current?owner.SliderValue(current.Id).ToString(System.Globalization.CultureInfo.InvariantCulture):null;
            set
            {
                if(Current is not { Slider:true } current||!owner.CanInteract(current))throw new InvalidOperationException("Control no disponible.");
                if(!int.TryParse(value,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var number))throw new ArgumentException("Se requiere un valor entero.",nameof(value));
                owner.ValueEdited?.Invoke(current.Id,Math.Clamp(number,current.Min,current.Max));
            }
        }
        public override Rectangle Bounds=>Current is not { } current?Rectangle.Empty:owner.RectangleToScreen(new((int)(owner._offsetX+current.Bounds.X*owner._scale),(int)(owner._offsetY+current.Bounds.Y*owner._scale),(int)(current.Bounds.Width*owner._scale),(int)(current.Bounds.Height*owner._scale)));
        public override void Select(AccessibleSelection flags)
        {
            if((flags&AccessibleSelection.TakeFocus)!=0&&Current is { } current&&owner.CanInteract(current)){owner.Focus();owner._keyboardId=current.Id;owner.Invalidate();}
        }
        public override void DoDefaultAction(){if(Current is { Slider:false } current&&owner.CanInteract(current))owner.Command?.Invoke(current.Id);}
    }
    protected override void Dispose(bool disposing){if(disposing)foreach(var f in _fonts.Values)f.Dispose();base.Dispose(disposing);}
}
