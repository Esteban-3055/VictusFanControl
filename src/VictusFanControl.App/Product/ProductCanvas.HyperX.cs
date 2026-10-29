namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    internal HyperXBatteryPage? HyperX {get;set;}
    private void HyperXView(Graphics g)
    {
        DrawText(g,"HyperX Battery",320,88,31,null,730,true);
        var p=HyperX;
        if(p is null){Card(g,new(310,153,1337,120));DrawText(g,"Preparando el lector de los audífonos…",334,184,24,Muted,1250);return;}
        void Action(string id,string text,RectangleF rect,bool selected=false,bool enabled=true)=>Button(g,"hyperx-"+id,text,rect,selected,enabled&&!p.Busy);
        Action("info","Información",new(1069,85,181,45),p.Tab==0);
        Action("notifications","Notificaciones",new(1266,85,201,45),p.Tab==1);
        Action("lcd","Pantalla LCD",new(1483,85,164,45),p.Tab==2);
        var s=p.Reading;var now=p.PresentedAt;bool fresh=s.Fresh(now);
        Card(g,new(310,153,1337,105));
        var color=fresh?Green:s.ReceiverPresent?Yellow:Muted;
        using(var b=new SolidBrush(color))g.FillEllipse(b,334,181,16,16);
        DrawText(g,"HyperX Cloud Flight",365,171,28,Ink,850,true);
        DrawText(g,s.Status,334,218,19,Muted,850);
        DrawText(g,"Última lectura de batería",1210,173,18,Muted,410);
        var age=s.SampledAt is{} at?$"{at.ToLocalTime():HH:mm:ss} · hace {Math.Max(0,(now-at).TotalSeconds):0} s":"Sin lectura disponible";
        DrawText(g,age,1210,211,21,fresh?Ink:Muted,410);
        void Toggle(string key,string name,bool value,RectangleF rect)=>Action(key,(value?"✓  ":"○  ")+name,rect,value);
        void Value(string key,string label,RectangleF rect,string unit)
        {
            var range=HyperXBatteryPage.Range(key);var id=HyperXBatteryPage.ValuePrefix+key;
            Slider(g,id,label,rect,p.Value(key),range.Min,range.Max,unit,true,!p.Busy);
            Hit(id+"-text",new(rect.Right-220,rect.Y-9,155,34),"Escribir "+label,!p.Busy);
        }
        if(p.Tab==0)
        {
            const float width=322.25f;
            string[] titles=["Batería aproximada","Voltaje reportado","Estado de carga","Micrófono"];
            string[] values=[fresh&&s.Percent is{} pct?$"{pct} %":"—",fresh&&s.Millivolts is{} mv?$"{mv/1000.0:0.000} V":"—",
                !fresh||s.Charging is null?"No confirmado":s.Charging.Value?"Cargando*":"Sin carga indicada",
                s.Muted is null?"Desconocido":s.Muted.Value?"Silenciado":"Sin silencio"];
            for(int i=0;i<4;i++)
            {
                float x=310+i*(width+16);Card(g,new(x,274,width,155));
                DrawText(g,titles[i],x+24,291,20,Muted,width-48);
                DrawText(g,values[i],x+24,337,i<2?37:25,i==3&&s.Muted==true?Yellow:i==0?Accent:Ink,width-48,true);
                if(i==0)Bar(g,new(x+24,398,width-48,7),fresh?s.Percent:null,100,Accent);
            }
            Card(g,new(310,445,1337,235));DrawText(g,"Estado de los audífonos",334,465,27,null,1260,true);
            DrawText(g,"Receptor USB",334,519,20,Muted,250);DrawText(g,s.ReceiverPresent?"Detectado":"No disponible",620,519,23,s.ReceiverPresent?Green:Muted,310);
            DrawText(g,"Encendido",1004,519,20,Muted,180);DrawText(g,s.Powered is null?"Desconocido":s.Powered.Value?"Encendidos":"Apagados",1210,519,23,Ink,410);
            DrawText(g,"Lectura de batería cada 60 s. Los cambios del micrófono se reciben al accionar su botón.",334,575,21,Muted,1250);
            DrawText(g,s.Muted is null?"Pulsa el botón de silencio para identificar el estado inicial del micrófono.":"Puedes ajustar los avisos en Notificaciones y su presentación en Pantalla LCD.",334,621,19,Muted,1250);
            Card(g,new(310,696,1337,60));DrawText(g,"El porcentaje es estimado por voltaje. Este reporte no confirma si hay cable de carga ni que los audífonos estén encendidos.",334,709,18,Muted,1287,height:38);
        }
        else if(p.Tab==1)
        {
            var rows=new[]{("low","Batería baja","low-enabled",p.Draft.LowEnabled),
                ("critical","Batería crítica","critical-enabled",p.Draft.CriticalEnabled),("full","Nivel completo","full-enabled",p.Draft.FullEnabled)};
            for(int i=0;i<3;i++)
            {
                int x=i==1?980:310,y=i==2?482:274;int w=i==1?667:654;
                Card(g,new(x,y,w,192));DrawText(g,rows[i].Item2,x+24,y+16,26,null,340,true);
                Toggle(rows[i].Item3,"Aviso "+(rows[i].Item4?"activado":"desactivado"),rows[i].Item4,new(x+w-250,y+14,226,38));
                Value(rows[i].Item1,"Umbral",new(x+24,y+84,w-48,83),"%");
            }
            Card(g,new(980,482,667,192));DrawText(g,"Micrófono",1004,498,26,null,610,true);
            Toggle("mute-enabled","Avisar al silenciar y quitar silencio",p.Draft.MuteEnabled,new(1004,548,617,42));
            DrawText(g,"La opción controla ambos avisos de Windows.",1004,617,18,Muted,610);
            DrawText(g,"Crítica: 0–100 %, con prioridad si se superpone. Mantén baja < completa. Windows puede ocultar avisos mediante No molestar.",334,700,18,Muted,1287,height:48);
        }
        else
        {
            for(int i=0;i<2;i++)
            {
                bool critical=i==1;int x=critical?980:310,w=critical?667:654;var key=critical?"critical":"low";
                Card(g,new(x,274,w,400));DrawText(g,critical?"Batería crítica":"Batería baja",x+24,290,26,null,340,true);
                bool enabled=critical?p.Draft.CriticalEnabled:p.Draft.LowEnabled;
                Toggle(key+"-enabled",enabled?"Aviso activado":"Aviso desactivado",enabled,new(x+w-250,288,226,38));
                Value(key+"-seconds","Duración total",new(x+24,359,w-48,83),"s");
                Value(key+"-cycles","Parpadeos",new(x+24,468,w-48,83),"ciclos");
                DrawText(g,"El tiempo incluye las fases visibles y en blanco.",x+24,580,18,Muted,w-48);
                DrawText(g,"Mínimo un segundo por ciclo de parpadeo.",x+24,620,18,Muted,w-48);
            }
            DrawText(g,"Crítica tiene prioridad. Sin PC, los avisos baja/crítica se repiten sin límite con el último porcentaje, marcado con * (lectura anterior).",334,700,18,Muted,1287,height:48);
        }
        DrawText(g,p.Busy?"Guardando ajustes…":p.Notice,334,770,18,p.Dirty?Yellow:Muted,1287,height:24);
        Action("discard","Descartar cambios",new(334,800,293,42),enabled:p.Dirty);
        Action("save",p.Dirty?"Guardar ajustes HyperX":"Ajustes guardados",new(643,800,565,42),true,p.Dirty);
        Action("reset","Restablecer",new(1224,800,397,42));
    }
}
