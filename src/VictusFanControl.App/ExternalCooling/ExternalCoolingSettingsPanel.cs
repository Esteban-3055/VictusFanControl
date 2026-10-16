using System.Drawing.Drawing2D;
using VictusFanControl.ExternalCooling;
using UiControl=System.Windows.Forms.Control;

namespace VictusFanControl.App;

/// <summary>Embedded page editor. Visibility only controls diagnostics rendering, never the network lifetime.</summary>
internal sealed class ExternalCoolingSettingsPanel : UserControl
{
    private readonly ComboBox _mode=new(){Name="external-mode",DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed};
    private readonly TextBox _host=new(){Name="external-host",MaxLength=253,AutoSize=false};
    private readonly NumericUpDown _port=new(){Name="external-port",Minimum=1,Maximum=65535};
    private readonly CheckBox _enabled=new CoolingToggle(){Name="external-enabled",Text="Habilitar conexión"};
    private readonly CheckBox _reconnect=new(){Text="Reconexión automática"};
    private readonly CheckBox _simulator=new(){Text="Simulador local (127.0.0.1)"};
    private readonly TextBox _key=new(){Name="external-key",UseSystemPasswordChar=true,MaxLength=64,AutoSize=false};
    private readonly CheckBox _reveal=new(){Text="Mostrar clave nueva"};
    private readonly CheckBox _details=new(){Name="external-details",Text="Mostrar mensajes enviados y recibidos"};
    private readonly Label _pairing=new(),_notice=new();
    private readonly Label _heading=new(){Text="Base externa / ESP32"},_subtitle=new(){Text="Conexión con base externa de ventilación (ESP32)"};
    private readonly Label _modeLabel=new(){Text="Modo"},_hostLabel=new(){Text="Host o dirección IP"},_portLabel=new(){Text="Puerto TCP"},_keyLabel=new(){Text="Nueva clave Base64"};
    private readonly TextBox _status=new(){Name="external-status",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,WordWrap=true};
    private readonly TextBox _messages=new(){Name="external-messages",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,WordWrap=true};
    private readonly System.Windows.Forms.Timer _timer=new(){Interval=1000};
    private readonly Panel _scroll=new(){Dock=DockStyle.Fill,AutoScroll=true},_actions=new();
    private readonly CoolingCard _editor=new();
    private readonly CoolingChip _chip=new();
    private readonly ExternalCoolingManager _manager;
    private readonly Func<ExternalCoolingSettings,string?,bool,bool,Task> _apply;
    private readonly Func<bool> _paired;
    private readonly Button _save,_connect,_disconnect,_forget,_generate;
    private Font? _bodyFont,_headingFont;
    private bool _ready,_layoutRunning,_busy,_syncMode;
    private int _lastEnabledMode=1;

    internal ExternalCoolingSettingsPanel(ExternalCoolingSettings settings,ExternalCoolingManager manager,Func<bool> paired,
        Func<ExternalCoolingSettings,string?,bool,bool,Task> apply,string? notice=null)
    {
        _manager=manager;_apply=apply;_paired=paired;
        Name="external-cooling-page";AccessibleName="Base externa / ESP32";AccessibleRole=AccessibleRole.Pane;
        AutoScaleMode=AutoScaleMode.None;BackColor=ProductCanvas.Background;TabStop=false;
        _mode.Items.AddRange(["Deshabilitado","Monitorización únicamente","Monitorización + Solicitud"]);
        _mode.DrawItem+=(_,e)=>
        {
            using var brush=new SolidBrush(ProductCanvas.Surface);e.Graphics.FillRectangle(brush,e.Bounds);
            TextRenderer.DrawText(e.Graphics,e.Index>=0?_mode.GetItemText(_mode.Items[e.Index]):"",Font,e.Bounds,ProductCanvas.Ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
        _mode.SelectedIndex=(int)settings.Mode;_host.Text=settings.Host;_port.Value=settings.Port;
        _reconnect.Checked=settings.AutoReconnect;_simulator.Checked=settings.UseSimulator;
        _enabled.Checked=settings.Mode!=ExternalCoolingMode.Disabled;
        if(_enabled.Checked)_lastEnabledMode=_mode.SelectedIndex;
        _mode.SelectedIndexChanged+=(_,_)=>
        {
            if(!_syncMode){_syncMode=true;_enabled.Checked=_mode.SelectedIndex>0;if(_enabled.Checked)_lastEnabledMode=_mode.SelectedIndex;_syncMode=false;}
            RefreshAdmission();
            if(_ready)SetNotice("Modo preparado. Pulsa Guardar o Conectar para aplicarlo a la base externa.");
        };
        _enabled.CheckedChanged+=(_,_)=>{if(!_syncMode)_mode.SelectedIndex=_enabled.Checked?_lastEnabledMode:0;};
        _simulator.CheckedChanged+=(_,_)=>RefreshAdmission();
        _reveal.CheckedChanged+=(_,_)=>_key.UseSystemPasswordChar=!_reveal.Checked;
        _details.CheckedChanged+=(_,_)=>{RefreshStatus();PerformLayout();};
        _save=ActionButton("Guardar",()=>SaveAsync(false),primary:true);
        _connect=ActionButton("Conectar",()=>SaveAsync(true));
        _disconnect=ActionButton("Desconectar",()=>_manager.DisconnectAsync());
        _forget=ActionButton("Olvidar clave",async()=>
        {
            await _apply(ReadSettings(),null,true,false);
            if(!IsDisposed){_key.Clear();SetNotice("Emparejamiento eliminado.");RefreshStatus();}
        });
        _generate=ActionButton("Generar clave",()=>
        {
            _key.Text=ExternalCoolingProtocol.NewPairingKey();
            SetNotice("Clave nueva pendiente de guardar. Introduce la misma clave en el simulador.");
            return Task.CompletedTask;
        });
        _actions.Controls.AddRange([_save,_connect,_disconnect,_forget]);
        _editor.Controls.AddRange([_modeLabel,_mode,_hostLabel,_host,_portLabel,_port,_reconnect,_simulator,_keyLabel,_key,_generate,_reveal,_pairing,_notice,_status,_details,_messages]);
        _scroll.Controls.AddRange([_chip,_heading,_subtitle,_enabled,_editor,_actions]);Controls.Add(_scroll);
        _notice.Text=notice??"Genera o introduce la misma clave en ambos equipos. Vacío conserva la clave guardada. El simulador se ejecuta por separado.";
        Theme(_scroll);_heading.ForeColor=ProductCanvas.Ink;_subtitle.ForeColor=_pairing.ForeColor=ProductCanvas.Muted;
        _heading.BackColor=_subtitle.BackColor=_enabled.BackColor=ProductCanvas.Background;
        _save.BackColor=Color.FromArgb(0,98,198);_save.FlatAppearance.BorderColor=ProductCanvas.Blue;
        _timer.Tick+=(_,_)=>RefreshStatus();
        _ready=true;SetCanvasScale(1);RefreshAdmission();RefreshStatus();
    }
    private Button ActionButton(string text,Func<Task> action,bool primary=false)
    {
        var button=new Button{Text=text,FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false,BackColor=primary?Color.FromArgb(0,98,198):ProductCanvas.Surface,ForeColor=ProductCanvas.Ink};
        button.FlatAppearance.BorderColor=ProductCanvas.Border;button.Click+=(_,_)=>_=RunAsync(action);return button;
    }
    private static void Theme(UiControl parent)
    {
        parent.BackColor=ProductCanvas.Background;parent.ForeColor=ProductCanvas.Ink;
        foreach(UiControl child in parent.Controls)
        {
            Theme(child);
            if(child is TextBox or ComboBox or NumericUpDown or Button or CoolingCard)child.BackColor=ProductCanvas.Surface;
            if(child is Label or CheckBox)child.BackColor=ProductCanvas.Surface;
        }
    }
    internal void SetCanvasScale(float scale)
    {
        var pixels=Math.Max(13,20*scale);
        if(_bodyFont is not null&&Math.Abs(_bodyFont.Size-pixels)<.05)return;
        var previousBody=_bodyFont;var previousHeading=_headingFont;
        _bodyFont=new Font("Segoe UI",pixels,FontStyle.Regular,GraphicsUnit.Pixel);
        _headingFont=new Font("Segoe UI",pixels*1.4f,FontStyle.Bold,GraphicsUnit.Pixel);
        Font=_bodyFont;_heading.Font=_headingFont;previousBody?.Dispose();previousHeading?.Dispose();PerformLayout();
        _mode.ItemHeight=Font.Height+8;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if(!_ready||_layoutRunning||Width<=0)return;
        _layoutRunning=true;
        try
        {
            int gap=Math.Max(8,Font.Height/2),pad=gap+4,w=Math.Max(200,_scroll.ClientSize.Width-2);
            var offset=_scroll.AutoScrollPosition;
            int header=Font.Height*3+gap*2;
            _chip.Bounds=new(offset.X+pad,offset.Y+gap,Font.Height*2,Font.Height*2);
            int enableWidth=Math.Min(w/3,TextRenderer.MeasureText(_enabled.Text,Font).Width+64);
            _enabled.Bounds=new(offset.X+w-enableWidth-pad,offset.Y+gap,enableWidth,Font.Height*2);
            _heading.Bounds=new(offset.X+pad+Font.Height*2+gap,offset.Y+gap,Math.Max(120,w-enableWidth-pad*3-Font.Height*2-gap),Font.Height*2);
            _subtitle.Bounds=new(offset.X+pad,offset.Y+Font.Height*2+gap+3,w-pad*2,Font.Height+gap);
            bool narrow=w<760;
            int buttonH=Font.Height+20,actionW=narrow?w:Math.Clamp((int)(w*.22),150,280);
            int actionH=narrow?buttonH*2+gap:buttonH*4+gap*3;
            _actions.Bounds=new(offset.X+(narrow?0:w-actionW),offset.Y+header,actionW,actionH);
            var actionButtons=new[]{_save,_connect,_disconnect,_forget};
            for(int i=0;i<actionButtons.Length;i++)
                actionButtons[i].Bounds=narrow?new((i%2)*(actionW+gap)/2,(i/2)*(buttonH+gap),(actionW-gap)/2,buttonH):new(0,i*(buttonH+gap),actionW,buttonH);
            int bodyW=narrow?w:w-actionW-gap,bodyY=header+(narrow?actionH+gap:0);
            int inner=Math.Max(150,bodyW-pad*2),fieldH=Math.Max(Font.Height+14,_mode.PreferredHeight);
            _mode.DropDownWidth=Math.Max(_mode.Width,TextRenderer.MeasureText("Monitorización + Solicitud",Font).Width+32);
            int y=pad,labelH=Font.Height+5;
            void Field(Label label,UiControl control,int x,int width)
            {label.SetBounds(x,y,width,labelH);control.SetBounds(x,y+labelH,width,fieldH);}
            if(inner>=490)
            {
                int a=(int)((inner-2*gap)*.36),b=(int)((inner-2*gap)*.43),c=inner-2*gap-a-b;
                Field(_modeLabel,_mode,pad,a);Field(_hostLabel,_host,pad+a+gap,b);Field(_portLabel,_port,pad+a+b+2*gap,c);
                y+=labelH+fieldH+gap;
            }
            else
            {
                Field(_modeLabel,_mode,pad,inner);y+=labelH+fieldH+gap;
                int portW=Math.Max(90,inner/4);
                Field(_hostLabel,_host,pad,inner-portW-gap);Field(_portLabel,_port,pad+inner-portW,portW);y+=labelH+fieldH+gap;
            }
            int checkW=(inner-gap)/2,checkH=Font.Height*2;
            _reconnect.SetBounds(pad,y,checkW,checkH);_simulator.SetBounds(pad+checkW+gap,y,inner-checkW-gap,checkH);y+=checkH+gap;
            int generateW=Math.Min(inner/2,Math.Max(TextRenderer.MeasureText(_generate.Text,Font).Width+32,TextRenderer.MeasureText(_reveal.Text,Font).Width+28));
            _keyLabel.SetBounds(pad,y,inner,labelH);y+=labelH;
            _key.SetBounds(pad,y,inner-generateW-gap,fieldH);_generate.SetBounds(pad+inner-generateW,y,generateW,fieldH);y+=fieldH+gap/2;
            _pairing.SetBounds(pad,y,inner-generateW-gap,checkH);
            _reveal.SetBounds(pad+inner-generateW,y,generateW,checkH);y+=checkH+gap;
            int noticeH=Math.Max(Font.Height*2,TextRenderer.MeasureText(_notice.Text,Font,new Size(inner,int.MaxValue),TextFormatFlags.WordBreak).Height+4);
            _notice.SetBounds(pad,y,inner,noticeH);y+=noticeH+gap;
            _status.SetBounds(pad,y,inner,Font.Height*5+16);y+=_status.Height+gap;
            _details.SetBounds(pad,y,inner,checkH);y+=checkH;
            _messages.Visible=_details.Checked;
            if(_details.Checked){_messages.SetBounds(pad,y,inner,Font.Height*10+20);y+=_messages.Height+gap;}
            _editor.Bounds=new(offset.X,offset.Y+bodyY,bodyW,y+pad);
            _scroll.AutoScrollMinSize=new(0,Math.Max(header+actionH,bodyY+y+pad)+gap);
        }
        finally{_layoutRunning=false;}
    }
    private ExternalCoolingSettings ReadSettings()=>new(){Mode=(ExternalCoolingMode)_mode.SelectedIndex,Host=_host.Text.Trim(),Port=(int)_port.Value,AutoReconnect=_reconnect.Checked,UseSimulator=_simulator.Checked};
    private void RefreshAdmission()
    {
        if(!_ready)return;
        _editor.Enabled=_actions.Enabled=_enabled.Enabled=!_busy;
        _host.Enabled=!_busy&&!_simulator.Checked&&_mode.SelectedIndex!=0;
        _connect.Enabled=!_busy&&_mode.SelectedIndex!=0;
    }
    private void SetNotice(string text){if(IsDisposed)return;_notice.Text=text;PerformLayout();}
    private async Task SaveAsync(bool connect)
    {
        var settings=ReadSettings();settings.Validate();
        await _apply(settings,string.IsNullOrWhiteSpace(_key.Text)?null:_key.Text,false,connect);
        if(IsDisposed)return;
        _key.Clear();SetNotice(connect?"Preferencias guardadas; conexión solicitada.":"Preferencias guardadas.");RefreshStatus();
    }
    private async Task RunAsync(Func<Task> action)
    {
        if(_busy||IsDisposed)return;
        _busy=true;RefreshAdmission();
        try{await action();}
        catch(Exception){SetNotice("No se pudo completar. Revisa host, puerto y clave Base64 de 32 bytes.");}
        finally{_busy=false;if(!IsDisposed)RefreshAdmission();}
    }
    private void RefreshStatus()
    {
        if(!_ready||IsDisposed)return;var d=_manager.Diagnostics;
        _pairing.Text=_paired()?"Clave protegida para este usuario de Windows":"Sin clave guardada";
        var age=d.LastValidReceivedUtc is{} at?$"{Math.Max(0,(DateTimeOffset.UtcNow-at).TotalSeconds):0} s":"Sin comunicación válida";
        _status.Text=$"Estado: {d.State} · {d.Detail}\r\nÚltima recepción válida: {age}\r\nEnviados: {d.Sent} · Recibidos: {d.Received} · Fallos: {d.Failures} · Sesiones: {d.Connections}\r\n"+
            $"Último fallo: {d.LastFailureKind?.ToString()??"—"} · {d.LastFailureUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")??"—"}\r\n"+
            $"ESP32: {d.Device?.Mode??"—"} · {d.Device?.Communication??"—"} · Ambiente: {d.Device?.AmbientTempC?.ToString("0.0")??"—"} °C · PWM: {d.Device?.AppliedPwmPct?.ToString("0.0")??"—"}%";
        if(_details.Checked)_messages.Text=$"Último mensaje enviado (datos):\r\n{d.LastSent??"—"}\r\n\r\nÚltimo recibido (campos operativos):\r\n{d.LastReceived??"—"}";
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);if(!_ready)return;
        if(Visible){RefreshStatus();_timer.Start();}
        else{_timer.Stop();_reveal.Checked=false;}
    }
    internal void SetPageSelected(bool selected)
    {
        Visible=selected;
        // Adding an already-visible UserControl need not raise VisibleChanged.
        if(selected&&Visible){RefreshStatus();_timer.Start();}
        else{_timer.Stop();_reveal.Checked=false;}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){_timer.Dispose();_key.Clear();}
        base.Dispose(disposing);
        if(disposing){_bodyFont?.Dispose();_headingFont?.Dispose();}
    }
    private sealed class CoolingToggle : CheckBox
    {
        public CoolingToggle(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            int h=Math.Min(26,Font.Height+4),w=h*2,y=(Height-h)/2;
            using var path=new GraphicsPath();path.AddArc(0,y,h,h,90,180);path.AddArc(w-h,y,h,h,270,180);path.CloseFigure();
            using var track=new SolidBrush(Checked&&Enabled?ProductCanvas.Blue:ProductCanvas.Border);e.Graphics.FillPath(track,path);
            using var thumb=new SolidBrush(Enabled?ProductCanvas.Ink:ProductCanvas.Muted);e.Graphics.FillEllipse(thumb,Checked?w-h+3:3,y+3,h-6,h-6);
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(w+10,0,Math.Max(1,Width-w-10),Height),Enabled?ForeColor:ProductCanvas.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Focused)ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle);
        }
    }
    private sealed class CoolingCard : Panel
    {
        public CoolingCard(){DoubleBuffered=true;}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var r=new RectangleF(1,1,Math.Max(1,Width-3),Math.Max(1,Height-3));
            using var path=new GraphicsPath();const float d=18;
            if(r.Width<d||r.Height<d)return;
            path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();
            using var pen=new Pen(ProductCanvas.Border);e.Graphics.DrawPath(pen,path);
        }
    }
    private sealed class CoolingChip : UiControl
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float size=Math.Min(Width,Height)-4,x=2,y=2;
            using var pen=new Pen(ProductCanvas.Ink,Math.Max(1.5f,size/16));
            e.Graphics.DrawRectangle(pen,x+size*.2f,y+size*.2f,size*.6f,size*.6f);
            for(int i=1;i<=3;i++)
            {
                e.Graphics.DrawLine(pen,x,y+size*i/4,x+size*.2f,y+size*i/4);e.Graphics.DrawLine(pen,x+size*.8f,y+size*i/4,x+size,y+size*i/4);
                e.Graphics.DrawLine(pen,x+size*i/4,y,x+size*i/4,y+size*.2f);e.Graphics.DrawLine(pen,x+size*i/4,y+size*.8f,x+size*i/4,y+size);
            }
        }
    }
}
