namespace VictusFanControl.App;

internal sealed class HyperXBatteryPage : UserControl
{
    private readonly Label readings=new(){AutoSize=true,MaximumSize=new(1000,0)};
    private readonly Label notice=new(){AutoSize=true,MaximumSize=new(1000,0)};
    private readonly CheckBox low=new(){Text="Avisar batería baja",AutoSize=true};
    private readonly CheckBox critical=new(){Text="Avisar batería crítica",AutoSize=true};
    private readonly CheckBox full=new(){Text="Avisar nivel de carga completa",AutoSize=true};
    private readonly CheckBox mute=new(){Text="Avisar al silenciar y quitar el silencio",AutoSize=true};
    internal NumericUpDown Low {get;}=Number();
    internal NumericUpDown Critical {get;}=Number();
    internal NumericUpDown Full {get;}=Number();
    internal NumericUpDown LcdLowSeconds {get;}=new(){Minimum=1,Maximum=120,Width=85};
    internal NumericUpDown LcdCriticalSeconds {get;}=new(){Minimum=1,Maximum=120,Width=85};
    internal NumericUpDown LcdLowCycles {get;}=new(){Minimum=1,Maximum=20,Width=85};
    internal NumericUpDown LcdCriticalCycles {get;}=new(){Minimum=1,Maximum=20,Width=85};
    private readonly Button save=new(){Text="Guardar ajustes",AutoSize=true};
    private readonly Func<HyperXSettings,Task> persist;
    private HyperXSettings saved;
    private static NumericUpDown Number()=>new(){Minimum=1,Maximum=100,Width=85};
    internal HyperXBatteryPage(HyperXSettings settings,Func<HyperXSettings,Task> saveSettings)
    {
        persist=saveSettings;saved=settings;Dock=DockStyle.None;AutoScroll=true;
        Critical.Minimum=10;Low.Minimum=11;Full.Minimum=12;
        BackColor=ProductCanvas.Background;ForeColor=ProductCanvas.Ink;Font=new Font("Segoe UI",11);
        var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new(12)};
        Controls.Add(flow);
        flow.Controls.Add(readings);
        flow.Controls.Add(new Label{Text="Batería: consulta cada 60 s; caduca a los 180 s. Silencio: eventos USB, sin sondeo continuo.",AutoSize=true,Margin=new(0,14,0,8)});
        void Row(CheckBox box,NumericUpDown number)
        {
            var row=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Margin=new(0,5,0,5)};
            row.Controls.Add(box);row.Controls.Add(number);row.Controls.Add(new Label{Text="%",AutoSize=true});flow.Controls.Add(row);
        }
        low.Text="Avisar batería baja (Windows y LCD)";critical.Text="Avisar batería crítica (Windows y LCD)";
        Row(low,Low);Row(critical,Critical);Row(full,Full);flow.Controls.Add(mute);
        void Timing(string title,NumericUpDown seconds,NumericUpDown cycles)
        {
            var row=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Margin=new(0,5,0,5)};
            row.Controls.Add(new Label{Text=title,AutoSize=true});row.Controls.Add(seconds);
            row.Controls.Add(new Label{Text="segundos ·",AutoSize=true});row.Controls.Add(cycles);
            row.Controls.Add(new Label{Text="ciclos de parpadeo",AutoSize=true});flow.Controls.Add(row);
        }
        Timing("LCD baja:",LcdLowSeconds,LcdLowCycles);Timing("LCD crítica:",LcdCriticalSeconds,LcdCriticalCycles);
        flow.Controls.Add(new Label{Text="El aviso LCD interrumpe cualquier pantalla una vez al entrar en cada umbral.\nSin PC, Hyp conserva el último porcentaje con * (lectura anterior; no es una actualización).",AutoSize=true});
        flow.Controls.Add(new Label{Text="Porcentaje aproximado por voltaje. «Completa» avisa al alcanzar el umbral estimado,\nno confirma que el circuito haya terminado de cargar. Windows puede silenciar los avisos\nmediante No molestar o la configuración de notificaciones.",AutoSize=true,Margin=new(0,10,0,10)});
        var buttons=new FlowLayoutPanel{AutoSize=true};buttons.Controls.Add(save);
        var discard=new Button{Text="Descartar",AutoSize=true};buttons.Controls.Add(discard);flow.Controls.Add(buttons);flow.Controls.Add(notice);
        Restore();discard.Click+=(_,_)=>{Restore();notice.Text="Ajustes guardados restaurados.";};
        save.Click+=async(_,_)=>await SaveAsync();
    }
    private void Restore()
    {
        LcdLowSeconds.Value=saved.LcdLowSeconds;LcdCriticalSeconds.Value=saved.LcdCriticalSeconds;LcdLowCycles.Value=saved.LcdLowBlinkCycles;LcdCriticalCycles.Value=saved.LcdCriticalBlinkCycles;
        Low.Value=saved.LowPercent;Critical.Value=saved.CriticalPercent;Full.Value=saved.FullPercent;
        low.Checked=saved.LowEnabled;critical.Checked=saved.CriticalEnabled;full.Checked=saved.FullEnabled;mute.Checked=saved.MuteEnabled;
    }
    internal HyperXSettings Draft=>saved with{LowPercent=(int)Low.Value,CriticalPercent=(int)Critical.Value,FullPercent=(int)Full.Value,
        LcdLowSeconds=(int)LcdLowSeconds.Value,LcdCriticalSeconds=(int)LcdCriticalSeconds.Value,LcdLowBlinkCycles=(int)LcdLowCycles.Value,LcdCriticalBlinkCycles=(int)LcdCriticalCycles.Value,
        LowEnabled=low.Checked,CriticalEnabled=critical.Checked,FullEnabled=full.Checked,MuteEnabled=mute.Checked};
    internal async Task SaveAsync()
    {
        if(!save.Enabled)return;
        try
        {
            var candidate=Draft;candidate.Validate();save.Enabled=false;
            await persist(candidate);saved=candidate;notice.Text="Ajustes HyperX guardados.";
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException)
        {notice.Text=e is InvalidDataException?e.Message:"No se pudieron guardar los ajustes. Se conserva la configuración anterior.";}
        finally{if(!IsDisposed)save.Enabled=true;}
    }
    internal void UpdateReading(HyperXSnapshot s,DateTimeOffset now)
    {
        bool fresh=s.Fresh(now);
        var percent=fresh&&s.Percent is{} p?$"{p} % aprox.":"--";
        var volts=fresh&&s.Millivolts is{} mv?$"{mv/1000.0:0.000} V":"--";
        var charge=!fresh||s.Charging is null?"Desconocida":s.Charging.Value?"Cargando (inferido)":"Sin indicación de carga";
        var mic=s.Muted is null?"Desconocido: acciona el botón de silencio":s.Muted.Value?"Silenciado":"Sin silencio";
        var at=s.SampledAt is{} t?$"{t.ToLocalTime():HH:mm:ss} · hace {Math.Max(0,(now-t).TotalSeconds):0} s":"--";
        var power=s.Powered is null?"Sin evento de encendido/apagado":s.Powered.Value?"Encendidos":"Apagados";
        readings.Text=$"HyperX Cloud Flight · {s.Status}\nReceptor USB: {(s.ReceiverPresent?"detectado":"no disponible")} · {power}\nBatería: {percent}     Voltaje: {volts}\nCarga: {charge}\nMicrófono: {mic}\nÚltima adquisición: {at}";
    }
    internal void SetNotice(string? text){if(text is not null)notice.Text=text;}
    protected override void Dispose(bool disposing){if(disposing){var font=Font;base.Dispose(disposing);font.Dispose();return;}base.Dispose(disposing);}
}
