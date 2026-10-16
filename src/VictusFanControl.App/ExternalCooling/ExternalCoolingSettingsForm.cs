using VictusFanControl.ExternalCooling;
using UiControl=System.Windows.Forms.Control;

namespace VictusFanControl.App;

internal sealed class ExternalCoolingSettingsForm : Form
{
    private readonly ComboBox _mode=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    private readonly TextBox _host=new(){Dock=DockStyle.Fill,MaxLength=253};
    private readonly NumericUpDown _port=new(){Minimum=1,Maximum=65535,Dock=DockStyle.Fill};
    private readonly CheckBox _reconnect=new(){Text="Reconexión automática",AutoSize=true};
    private readonly CheckBox _simulator=new(){Text="Simulador local (127.0.0.1)",AutoSize=true};
    private readonly TextBox _key=new(){UseSystemPasswordChar=true,Dock=DockStyle.Fill,MaxLength=64};
    private readonly Label _pairing=new(){AutoSize=true};
    private readonly TextBox _status=new(){Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,WordWrap=true};
    private readonly Label _notice=new(){AutoSize=true,MaximumSize=new(700,0)};
    private readonly System.Windows.Forms.Timer _timer=new(){Interval=1000};
    private readonly ExternalCoolingManager _manager;
    private readonly Func<ExternalCoolingSettings,string?,bool,bool,Task> _apply;
    private readonly Func<bool> _paired;
    private readonly FlowLayoutPanel _buttons=new(){AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
    internal ExternalCoolingSettingsForm(ExternalCoolingSettings settings,ExternalCoolingManager manager,Func<bool> paired,
        Func<ExternalCoolingSettings,string?,bool,bool,Task> apply,string? notice=null)
    {
        _manager=manager;_apply=apply;_paired=paired;
        Text="Base externa / ESP32";BackColor=ProductCanvas.Background;ForeColor=ProductCanvas.Ink;
        AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Segoe UI",10);ClientSize=new(790,700);MinimumSize=new(650,520);
        StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
        _mode.Items.AddRange(["Deshabilitado","Monitorización únicamente","Monitorización y transmisión de Solicitud"]);
        _mode.SelectedIndex=(int)settings.Mode;_host.Text=settings.Host;_port.Value=settings.Port;
        _reconnect.Checked=settings.AutoReconnect;_simulator.Checked=settings.UseSimulator;
        void HostAvailability()=>_host.Enabled=!_simulator.Checked&&_mode.SelectedIndex!=(int)ExternalCoolingMode.Disabled;
        HostAvailability();_simulator.CheckedChanged+=(_,_)=>HostAvailability();_mode.SelectedIndexChanged+=(_,_)=>HostAvailability();
        var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};
        var root=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=2,RowCount=11,Padding=new(18),AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};
        var labelWidth=Math.Max(185,TextRenderer.MeasureText("Host o dirección IP",Font).Width+20);
        root.ColumnStyles.Add(new(SizeType.Absolute,labelWidth));root.ColumnStyles.Add(new(SizeType.Percent,100));
        void Row(int row,string label,UiControl control)
        {root.RowStyles.Add(new(SizeType.AutoSize));root.Controls.Add(new Label{Text=label,AutoSize=true,Margin=new(3,8,3,8)},0,row);root.Controls.Add(control,1,row);control.Margin=new(3,6,3,6);}
        Row(0,"Modo",_mode);Row(1,"Host o dirección IP",_host);Row(2,"Puerto TCP",_port);Row(3,"Conexión",_reconnect);Row(4,"Pruebas",_simulator);
        Row(5,"Nueva clave Base64",_key);
        var pairingOptions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
        var generate=Button("Generar clave",()=>{_key.Text=ExternalCoolingProtocol.NewPairingKey();_notice.Text="Clave nueva pendiente de guardar. Compártela con el simulador por un canal privado.";});
        var reveal=new CheckBox{Text="Mostrar clave nueva",AutoSize=true};reveal.CheckedChanged+=(_,_)=>_key.UseSystemPasswordChar=!reveal.Checked;
        pairingOptions.Controls.Add(generate);pairingOptions.Controls.Add(reveal);Row(6,"Emparejamiento",pairingOptions);Row(7,"Guardado",_pairing);
        void ActionButton(string label,Func<Task> action)=>_buttons.Controls.Add(Button(label,()=>_=RunAsync(action)));
        ActionButton("Guardar",()=>SaveAsync(false));ActionButton("Conectar",()=>SaveAsync(true));ActionButton("Desconectar",()=>_manager.DisconnectAsync());
        ActionButton("Olvidar clave",async()=>{await _apply(ReadSettings(),null,true,false);_key.Clear();RefreshStatus();});
        root.RowStyles.Add(new(SizeType.AutoSize));root.Controls.Add(_buttons,0,8);root.SetColumnSpan(_buttons,2);
        _notice.Text=notice??"Genera o introduce la misma clave en ambos equipos. Vacío conserva la clave guardada. El simulador se ejecuta por separado.";
        root.RowStyles.Add(new(SizeType.AutoSize));root.Controls.Add(_notice,0,9);root.SetColumnSpan(_notice,2);
        root.RowStyles.Add(new(SizeType.AutoSize));root.Controls.Add(_status,0,10);root.SetColumnSpan(_status,2);_status.MinimumSize=new(300,230);
        var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Padding=new(12,4,12,4)};
        footer.Controls.Add(Button("Cerrar",Close));scroll.Controls.Add(root);Controls.Add(scroll);Controls.Add(footer);
        ApplyTheme(this);_timer.Tick+=(_,_)=>RefreshStatus();Shown+=(_,_)=>{RefreshStatus();_timer.Start();};
    }
    private static Button Button(string text,Action click)
    {var button=new Button{Text=text,AutoSize=true,FlatStyle=FlatStyle.Flat,Padding=new(8,3,8,3),Margin=new(3,5,8,5)};button.Click+=(_,_)=>click();return button;}
    private static void ApplyTheme(UiControl parent)
    {
        foreach(UiControl child in parent.Controls)
        {child.ForeColor=ProductCanvas.Ink;child.BackColor=child is TextBox or ComboBox or NumericUpDown?ProductCanvas.Surface:ProductCanvas.Background;ApplyTheme(child);}
    }
    private ExternalCoolingSettings ReadSettings()=>new(){Mode=(ExternalCoolingMode)_mode.SelectedIndex,Host=_host.Text.Trim(),Port=(int)_port.Value,AutoReconnect=_reconnect.Checked,UseSimulator=_simulator.Checked};
    private async Task SaveAsync(bool connect)
    {var settings=ReadSettings();settings.Validate();await _apply(settings,string.IsNullOrWhiteSpace(_key.Text)?null:_key.Text,false,connect);_key.Clear();_notice.Text="Preferencias guardadas.";RefreshStatus();}
    private async Task RunAsync(Func<Task> action)
    {
        _buttons.Enabled=false;
        try{await action();}
        catch(Exception){_notice.Text="No se pudo completar. Revisa host, puerto y clave Base64 de 32 bytes. El control interno continúa independiente.";}
        finally{if(!IsDisposed)_buttons.Enabled=true;}
    }
    private void RefreshStatus()
    {
        if(IsDisposed)return;var d=_manager.Diagnostics;
        _pairing.Text=_paired()?"Clave protegida para este usuario de Windows":"Sin clave guardada";
        var age=d.LastValidReceivedUtc is{} at?$"{Math.Max(0,(DateTimeOffset.UtcNow-at).TotalSeconds):0} s":"Sin comunicación válida";
        _status.Text=$"Estado: {d.State} · {d.Detail}\r\nÚltima recepción válida: {age}\r\nEnviados: {d.Sent} · Recibidos: {d.Received} · Fallos: {d.Failures} · Sesiones: {d.Connections}\r\n"+
            $"Último fallo: {d.LastFailureKind?.ToString()??"—"} · {d.LastFailureUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")??"—"}\r\n"+
            $"ESP32: {d.Device?.Mode??"—"} · {d.Device?.Communication??"—"} · Ambiente: {d.Device?.AmbientTempC?.ToString("0.0")??"—"} °C · PWM: {d.Device?.AppliedPwmPct?.ToString("0.0")??"—"}%\r\n\r\n"+
            $"Último mensaje enviado (datos):\r\n{d.LastSent??"—"}\r\n\r\nÚltimo recibido (campos operativos):\r\n{d.LastReceived??"—"}";
    }
    protected override void Dispose(bool disposing){if(disposing){_timer.Dispose();_key.Clear();}base.Dispose(disposing);}
}
