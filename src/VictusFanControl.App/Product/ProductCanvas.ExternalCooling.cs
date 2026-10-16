using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    internal ExternalCoolingPage? CoolingPage {get;set;}
    internal void FocusExternalField(string id)=>_keyboardId=id;
    private void ExternalCooling(Graphics g)
    {
        DrawText(g,"Base externa / ESP32",320,88,31,null,860,true);
        var p=CoolingPage;
        if(p is null){Card(g,new(310,153,1337,145));DrawText(g,"Preparando la configuración de la base externa…",334,185,24,Muted,1250);return;}
        void Action(string id,string text,RectangleF rect,bool selected=false,bool enabled=true)=>Button(g,"external-"+id,text,rect,selected,enabled&&!p.Busy);
        Action("overview","Configuración",new(1190,85,210,45),!p.Details);
        Action("details","Diagnóstico",new(1416,85,231,45),p.Details);
        var d=p.Status;
        var state=d.State switch{ExternalCoolingConnectionState.Disabled=>"Deshabilitada",ExternalCoolingConnectionState.Disconnected=>"Desconectada",ExternalCoolingConnectionState.Connecting=>"Conectando",ExternalCoolingConnectionState.Authenticating=>"Autenticando",ExternalCoolingConnectionState.Connected=>"Conectada",ExternalCoolingConnectionState.Reconnecting=>"Reconectando",_=>"Error de conexión"};
        var color=d.State==ExternalCoolingConnectionState.Connected?Green:d.State==ExternalCoolingConnectionState.Error?Red:p.Active?Yellow:Muted;
        Card(g,new(310,153,1337,145));
        using(var b=new SolidBrush(color))g.FillEllipse(b,334,181,16,16);
        DrawText(g,state,365,172,29,color,730,true);
        DrawText(g,d.Detail,334,220,21,Muted,810,height:28);
        var savedMode=p.SavedMode switch{ExternalCoolingMode.MonitorOnly=>"Monitorización",ExternalCoolingMode.MonitorAndRequest=>"Monitorización + Solicitud",_=>"Deshabilitado"};
        DrawText(g,$"Guardado: {savedMode} · ESP32: {(p.Live?$"{d.Device?.Mode??"—"} · {d.Device?.Communication??"—"}":"Sin datos vigentes")}",334,260,17,Muted,810,height:25);
        var age=d.LastValidReceivedUtc is{} at?$"{Math.Max(0,(DateTimeOffset.UtcNow-at).TotalSeconds):0} s":"Sin recepción válida";
        DrawText(g,"Última comunicación válida",1130,176,19,Muted,485);
        DrawText(g,age,1130,209,27,null,485,true);
        DrawText(g,$"Sesiones: {d.Connections} · Fallos: {d.Failures}",1130,252,18,Muted,485);
        if(p.Details)
        {
            float width=(1337-48)/4f;
            void Stat(int index,string title,string value){var x=310+(width+16)*index;Card(g,new(x,314,width,105));DrawText(g,title,x+24,329,20,Muted,width-40);DrawText(g,value,x+24,363,32,Accent,width-40,true);}
            Stat(0,"Ambiente",p.Live&&d.Device?.AmbientTempC is{} temp?$"{temp:0.0} °C":"—");
            Stat(1,"PWM aplicado",p.Live&&d.Device?.AppliedPwmPct is{} pwm?$"{pwm:0.0} %":"—");
            Stat(2,"Enviados",d.Sent.ToString());Stat(3,"Recibidos",d.Received.ToString());
            Card(g,new(310,435,1337,325));
            Action("message-sent","Último enviado",new(334,449,270,43),!p.ReceivedMessage);
            Action("message-received","Último recibido",new(620,449,270,43),p.ReceivedMessage);
            Action("copy","Copiar mensaje",new(1270,449,351,43),false,(p.ReceivedMessage?d.LastReceived:d.LastSent) is not null);
            DrawText(g,p.Message,334,510,18,Muted,1285,height:230);
            DrawText(g,$"Dispositivo: {(p.Live?$"{d.Device?.Mode??"—"} · {d.Device?.Communication??"—"}":"Sin datos vigentes")} · Último fallo: {d.LastFailureKind?.ToString()??"—"} · {d.LastFailureUtc?.ToLocalTime().ToString("dd/MM HH:mm:ss")??"—"}",334,780,18,Muted,1285,height:45);
            DrawText(g,p.Notice,334,829,18,Muted,1285,height:35);
            return;
        }
        Card(g,new(310,314,1337,111));DrawText(g,p.Dirty?"Modo de operación · cambios pendientes de guardar":"Modo de operación",334,328,21,Muted,1250);
        Action("mode-0","Deshabilitado",new(334,370,290,39),p.Mode==ExternalCoolingMode.Disabled);
        Action("mode-1","Monitorización",new(640,370,400,39),p.Mode==ExternalCoolingMode.MonitorOnly);
        Action("mode-2","Monitorización + Solicitud",new(1056,370,565,39),p.Mode==ExternalCoolingMode.MonitorAndRequest);
        Card(g,new(310,441,654,240));DrawText(g,"Conexión",334,462,28,null,600,true);
        DrawText(g,"Host o dirección IP",334,506,20,Muted,430);DrawText(g,"Puerto TCP",788,506,20,Muted,152);
        Field(g,"external-edit-host",new(334,535,432,43),!p.Simulator&&!p.Busy);
        Field(g,"external-edit-port",new(788,535,152,43),!p.Busy);
        Action("reconnect",(p.Reconnect?"✓  ":"○  ")+"Reconexión automática",new(334,592,294,43),p.Reconnect);
        Action("simulator",(p.Simulator?"✓  ":"○  ")+"Simulador local",new(644,592,296,43),p.Simulator);
        DrawText(g,p.Simulator?"Destino: 127.0.0.1 · El simulador se ejecuta por separado.":"Destino: nombre o dirección del dispositivo en la red local.",334,645,17,Muted,605,height:28);
        Card(g,new(980,441,667,240));DrawText(g,"Emparejamiento",1004,462,28,null,610,true);
        DrawText(g,p.ForgetPending?"Eliminación pendiente de guardar":p.Paired?"Clave guardada y protegida por Windows":"Sin clave guardada",1004,506,19,p.ForgetPending?Yellow:Muted,610);
        DrawText(g,"Nueva clave Base64 · vacío conserva la guardada",1004,542,18,Muted,610);
        Field(g,"external-edit-key",new(1004,574,366,43),!p.Busy);
        Action("generate","Generar clave",new(1386,574,235,43));
        Action("reveal",p.Key.UseSystemPasswordChar?"Mostrar clave nueva":"Ocultar clave",new(1004,630,260,32),!p.Key.UseSystemPasswordChar,p.Key.TextLength>0);
        Action("forget","Olvidar emparejamiento",new(1280,630,341,32),false,p.Paired&&!p.ForgetPending);
        Card(g,new(310,697,1337,57));DrawText(g,p.Notice,334,707,18,Muted,1285,height:40);
        DrawText(g,p.Busy?"Operación en curso…":p.Dirty?"Hay cambios sin guardar en la base externa":"Configuración de la base externa guardada",334,766,18,p.Dirty?Yellow:Green,1250);
        Action("discard","Descartar cambios",new(334,805,300,48),false,p.Dirty);
        Action("save","Guardar cambios",new(650,805,400,48),true,p.Dirty);
        Action("connection",p.Active?"Desconectar":p.Dirty?"Guardar y conectar":"Conectar",new(1066,805,555,48),!p.Active,p.Active||p.CanConnect);
    }
    private void Field(Graphics g,string id,RectangleF rect,bool enabled)
    {
        using var path=Rounded(rect,10);using var fill=new SolidBrush(Surface);g.FillPath(fill,path);
        using var pen=new Pen(ContainsFocus&&_keyboardId==id?Accent:Border,1.4f);g.DrawPath(pen,path);Hit(id,rect,id,enabled);
    }
}
