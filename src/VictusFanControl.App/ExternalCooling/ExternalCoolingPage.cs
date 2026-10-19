using System.Globalization;
using System.Security.Cryptography;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

/// <summary>Independent external settings draft. ProductCanvas renders the page; only text editing uses native controls.</summary>
internal sealed class ExternalCoolingPage : IDisposable
{
    private readonly ProductCanvas _canvas;
    private readonly ExternalCoolingManager _manager;
    private readonly Func<bool> _paired;
    private readonly Func<ExternalCoolingSettings,string?,bool,bool,Task> _apply;
    private readonly Action<string> _copy;
    private readonly System.Windows.Forms.Timer _timer=new(){Interval=1000};
    private ExternalCoolingSettings _saved;
    internal Func<ExternalCurveConfiguration>? CurrentCurve {get;set;}
    private Font? _font;
    private bool _disposed,_selected,_forget;
    internal TextBox Host {get;}
    internal TextBox Port {get;}
    internal TextBox Key {get;}
    internal ExternalCoolingMode Mode {get;private set;}
    internal bool Reconnect {get;private set;}
    internal bool Simulator {get;private set;}
    internal bool Details {get;private set;}
    internal bool ReceivedMessage {get;private set;}
    internal bool Busy {get;private set;}
    internal string Notice {get;private set;}="";
    internal bool ForgetPending=>_forget;
    internal ExternalCoolingMode SavedMode=>_saved.Mode;
    internal bool Paired=>_paired();
    internal bool CanConnect=>Mode!=ExternalCoolingMode.Disabled&&((Paired&&!_forget)||!string.IsNullOrWhiteSpace(Key.Text));
    internal ExternalCoolingDiagnostics Status=>_manager.Diagnostics;
    internal bool Active=>Status.State is ExternalCoolingConnectionState.Connected or ExternalCoolingConnectionState.Connecting or ExternalCoolingConnectionState.Authenticating or ExternalCoolingConnectionState.Reconnecting;
    internal bool Dirty=>Mode!=_saved.Mode||Reconnect!=_saved.AutoReconnect||Simulator!=_saved.UseSimulator||Host.Text!=_saved.Host||Port.Text!=_saved.Port.ToString(CultureInfo.InvariantCulture)||Key.Text.Length>0||_forget;
    internal string Message=> (ReceivedMessage?Status.LastReceived:Status.LastSent)??"Todavía no hay mensajes.";
    internal bool Live=>Status.State==ExternalCoolingConnectionState.Connected&&Status.LastValidReceivedUtc is{} at&&(DateTimeOffset.UtcNow-at).TotalSeconds is >=0 and <=3;

    internal ExternalCoolingPage(ProductCanvas canvas,ExternalCoolingSettings saved,ExternalCoolingManager manager,Func<bool> paired,
        Func<ExternalCoolingSettings,string?,bool,bool,Task> apply,string? notice=null,Action<string>? copy=null)
    {
        _canvas=canvas;_saved=saved;_manager=manager;_paired=paired;_apply=apply;_copy=copy??Clipboard.SetText;
        Host=Editor("external-edit-host",253);Port=Editor("external-edit-port",5);Key=Editor("external-edit-key",64);Key.UseSystemPasswordChar=true;
        Restore();Notice=notice??"La base externa tiene su propia configuración. Guarda los cambios para aplicarlos.";
        _timer.Tick+=(_,_)=>{if(_selected&&_canvas.Visible)_canvas.Invalidate();};
        _canvas.VisibleChanged+=VisibilityChanged;
    }
    private void VisibilityChanged(object? sender,EventArgs e)=>Layout(_selected);
    private TextBox Editor(string name,int maximum)
    {
        var box=new ExternalEditor(_canvas){Name=name,AccessibleName=name switch{"external-edit-host"=>"Host o dirección IP","external-edit-port"=>"Puerto TCP",_=>"Nueva clave Base64"},
            BorderStyle=BorderStyle.None,AutoSize=false,MaxLength=maximum,BackColor=ProductCanvas.Surface,ForeColor=ProductCanvas.Ink,Visible=false};
        box.TextChanged+=(_,_)=>{if(!_disposed)_canvas.Invalidate();};
        box.GotFocus+=(_,_)=>_canvas.FocusExternalField(name);
        _canvas.Controls.Add(box);return box;
    }
    internal void UpdateSavedCurve(ExternalCurveConfiguration curve)=>_saved=_saved with{Curve=curve};
    private void Restore()
    {
        Mode=_saved.Mode;Reconnect=_saved.AutoReconnect;Simulator=_saved.UseSimulator;Host.Text=_saved.Host;
        Port.Text=_saved.Port.ToString(CultureInfo.InvariantCulture);Key.Clear();Key.UseSystemPasswordChar=true;_forget=false;
    }
    internal void Layout(bool selected)
    {
        if(_disposed)return;
        _selected=selected;
        var scale=Math.Min(_canvas.ClientSize.Width/1672f,_canvas.ClientSize.Height/941f);
        var x=(_canvas.ClientSize.Width-1672*scale)/2;var y=(_canvas.ClientSize.Height-941*scale)/2;
        var pixels=Math.Max(11,22*scale);
        if(_font is null||Math.Abs(_font.Size-pixels)>.05)
        {
            var old=_font;_font=new("Segoe UI",pixels,FontStyle.Regular,GraphicsUnit.Pixel);
            Host.Font=Port.Font=Key.Font=_font;old?.Dispose();
        }
        void Place(TextBox box,RectangleF field)
        {
            box.Visible=selected&&!Details;
            box.Bounds=Rectangle.Round(new RectangleF(x+(field.X+12)*scale,y+(field.Y+9)*scale,(field.Width-24)*scale,(field.Height-16)*scale));
        }
        Place(Host,new(334,535,432,43));Place(Port,new(788,535,152,43));Place(Key,new(1004,574,366,43));
        Host.Enabled=!Busy&&!Simulator;Port.Enabled=Key.Enabled=!Busy;
        if(selected&&_canvas.Visible)_timer.Start();else{_timer.Stop();Key.UseSystemPasswordChar=true;}
    }
    internal async Task ExecuteAsync(string id)
    {
        if(_disposed||Busy)return;
        if(id is "external-overview" or "external-details") {Details=id=="external-details";Key.UseSystemPasswordChar=true;Layout(_selected);_canvas.Invalidate();return;}
        if(id.StartsWith("external-mode-",StringComparison.Ordinal)&&int.TryParse(id[14..],out var mode)&&mode is >=0 and <=2)Mode=(ExternalCoolingMode)mode;
        else switch(id)
        {
            case "external-edit-host":Host.Focus();return;
            case "external-edit-port":Port.Focus();return;
            case "external-edit-key":Key.Focus();return;
            case "external-reconnect":Reconnect=!Reconnect;break;
            case "external-simulator":Simulator=!Simulator;break;
            case "external-generate":Key.Text=ExternalCoolingProtocol.NewPairingKey();_forget=false;Notice="Clave nueva pendiente de guardar. Usa la misma clave en el simulador.";break;
            case "external-reveal":Key.UseSystemPasswordChar=!Key.UseSystemPasswordChar;break;
            case "external-forget":_forget=true;Key.Clear();Key.UseSystemPasswordChar=true;Notice="El emparejamiento se eliminará al guardar. Descartar conserva la clave guardada.";break;
            case "external-discard":Restore();Notice="Se recuperaron los ajustes guardados de la base externa.";break;
            case "external-message-sent":ReceivedMessage=false;break;
            case "external-message-received":ReceivedMessage=true;break;
            case "external-copy":try{_copy(Message);Notice="Mensaje copiado.";}catch(System.Runtime.InteropServices.ExternalException){Notice="No se pudo copiar. Vuelve a intentarlo.";}break;
            case "external-save":case "external-connection":
                Busy=true;Layout(_selected);_canvas.Invalidate();
                try
                {
                    if(id=="external-connection"&&Active){await _manager.DisconnectAsync();Notice="Conexión detenida.";break;}
                    if(!int.TryParse(Port.Text,NumberStyles.None,CultureInfo.InvariantCulture,out var port))throw new ArgumentException();
                    var settings=_saved with{Mode=Mode,Host=Host.Text.Trim(),Port=port,AutoReconnect=Reconnect,UseSimulator=Simulator};settings.Validate();
                    var key=string.IsNullOrWhiteSpace(Key.Text)?null:Key.Text;
                    if(key is not null){var raw=ExternalCoolingProtocol.ParsePairingKey(key);CryptographicOperations.ZeroMemory(raw);}
                    var connect=id=="external-connection";
                    if(connect&&(Mode==ExternalCoolingMode.Disabled||(!Paired||_forget)&&key is null))throw new ArgumentException();
                    await _apply(settings,key,_forget,connect);
                    if(_disposed)return;
                    _saved=settings with{Curve=CurrentCurve?.Invoke()??settings.Curve};Restore();Notice=connect?"Preferencias guardadas; conexión solicitada.":"Preferencias guardadas. Pulsa Conectar para iniciar la comunicación.";
                }
                catch(Exception){if(!_disposed)Notice="No se pudo completar. Revisa host, puerto y clave Base64 de 32 bytes.";}
                finally{Busy=false;if(!_disposed)Layout(_selected);}
                break;
        }
        if(!_disposed){Layout(_selected);_canvas.Invalidate();}
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_canvas.VisibleChanged-=VisibilityChanged;_timer.Dispose();Key.Clear();Host.Dispose();Port.Dispose();Key.Dispose();_font?.Dispose();
    }
    private sealed class ExternalEditor(ProductCanvas owner) : TextBox
    {
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if((keyData&Keys.KeyCode)==Keys.Tab){owner.Focus();owner.HandleKey(keyData);return true;}
            if((keyData&Keys.KeyCode) is Keys.Enter or Keys.Escape){owner.Focus();return true;}
            return base.ProcessDialogKey(keyData);
        }
    }
}
