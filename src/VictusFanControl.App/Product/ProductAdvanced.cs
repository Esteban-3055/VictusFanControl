using System.Globalization;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

internal sealed record ProductTuningField(string Key,string Label,string Unit,int Tab,double Min,double Max,double Step,
    Func<AdaptiveFanTuning,double> Read,Func<AdaptiveFanTuning,double,AdaptiveFanTuning> Write)
{
    internal bool Integer => Step>=1;
    internal string Format(double value)=>value.ToString(Integer?"0":"0.##",CultureInfo.CurrentCulture)+" "+Unit;
}

/// <summary>Only settings consumed by the current engine; physical envelope and safety gates stay fixed.</summary>
internal static class ProductAdvancedSettings
{
    internal static readonly ProductTuningField[] Fields =
    [
        new("cores","P-Cores más calientes (N)","núcleos",0,1,64,1,t=>t.HottestPerformanceCoreCount,(t,v)=>t with{HottestPerformanceCoreCount=(int)v}),
        new("rise","Filtro de subida","s",1,.5,10,.5,t=>t.RiseTimeConstantSeconds,(t,v)=>t with{RiseTimeConstantSeconds=v}),
        new("rise-confirm","Confirmación de subida","s",1,0,5,.5,t=>t.IncreaseConfirmationSeconds,(t,v)=>t with{IncreaseConfirmationSeconds=v}),
        new("rise-step","Paso normal de subida","niveles",1,1,4,1,t=>t.NormalMaximumUpStepLevels,(t,v)=>t with{NormalMaximumUpStepLevels=(int)v}),
        new("short-fall","Filtro de bajada breve","s",1,1,60,.5,t=>t.ShortLoadFallTimeConstantSeconds,(t,v)=>t with{ShortLoadFallTimeConstantSeconds=v}),
        new("short-confirm","Confirmación de bajada breve","s",1,2,60,.5,t=>t.ShortLoadDecreaseConfirmationSeconds,(t,v)=>t with{ShortLoadDecreaseConfirmationSeconds=v}),
        new("fall","Filtro de bajada prolongada / fija","s",1,1,60,.5,t=>t.FallTimeConstantSeconds,(t,v)=>t with{FallTimeConstantSeconds=v}),
        new("fall-confirm","Confirmación de bajada prolongada / fija","s",1,2,60,.5,t=>t.DecreaseConfirmationSeconds,(t,v)=>t with{DecreaseConfirmationSeconds=v}),
        new("sustained","Carga acumulada para descenso lento","s",2,60,3600,60,t=>t.SustainedLoadSeconds,(t,v)=>t with{SustainedLoadSeconds=v}),
        new("load","Utilización CPU o GPU","%",2,1,100,1,t=>t.LoadThresholdPercent,(t,v)=>t with{LoadThresholdPercent=v}),
        new("cpu-load-power","Potencia CPU para contar carga","W",2,1,200,1,t=>t.CpuLoadPowerThresholdW,(t,v)=>t with{CpuLoadPowerThresholdW=v}),
        new("gpu-load-power","Potencia GPU para contar carga","W",2,1,250,1,t=>t.GpuLoadPowerThresholdW,(t,v)=>t with{GpuLoadPowerThresholdW=v}),
        new("pause","Pausa tolerada antes de calificar","s",2,0,120,1,t=>t.LoadPauseToleranceSeconds,(t,v)=>t with{LoadPauseToleranceSeconds=v}),
        new("cooldown","Reposo para volver al descenso breve","s",2,10,600,10,t=>t.SustainedLoadCooldownSeconds,(t,v)=>t with{SustainedLoadCooldownSeconds=v}),
        new("cpu-thermal","Respuesta térmica CPU desde","°C",3,75,85,1,t=>t.CpuThermalOverrideC,(t,v)=>t with{CpuThermalOverrideC=v}),
        new("gpu-thermal","Respuesta térmica GPU desde","°C",3,68,78,1,t=>t.GpuThermalOverrideC,(t,v)=>t with{GpuThermalOverrideC=v}),
        new("poll","Pausa entre lecturas normales","ms",3,500,1500,100,t=>t.NormalPollingDelayMilliseconds,(t,v)=>t with{NormalPollingDelayMilliseconds=(int)v})
    ];
    internal static ProductTuningField? Find(string key)=>Fields.FirstOrDefault(f=>f.Key==key);
}

internal sealed partial class ProductCanvas
{
    internal int AdvancedTab { get; set; }
    internal AdaptiveFanTuning DraftTuning=>Profiles.Ac.Fan.Tuning;
    internal bool TuningPending=>Profiles.Battery.Fan.Tuning!=DraftTuning||State.AppliedAutomaticConfiguration?.Tuning!=DraftTuning;
    internal bool CanApplyTuning=>!Busy&&!State.PerformanceUpdating&&!State.LifecycleBlocked&&!State.AutomaticPreparing&&State.AutomaticSourceTransition is null&&
        State.Target=="HP-8C40-9D0R1LA-F18"&&State.Runtime=="Healthy"&&FreshSnapshot is not null&&
        (State.FanMode=="Firmware"&&State.FanAuthority=="Firmware"||State.AutomaticAuthorized&&State.FanMode=="Automatic"&&State.FanAuthority=="Custom");
    private void TuningRow(Graphics g,ProductTuningField f,float y)
    {
        var value=f.Read(DraftTuning);DrawText(g,f.Label,342,y+10,21,null,473);
        Button(g,"tuning-"+f.Key+"-minus","−",new(820,y,45,43));
        Button(g,"tuning-"+f.Key+"-text",f.Format(value),new(873,y,167,43));
        Button(g,"tuning-"+f.Key+"-plus","+",new(1048,y,45,43));
        var applied=State.AppliedAutomaticConfiguration?.Tuning;
        DrawText(g,$"Rango {f.Min}–{f.Max} {f.Unit}"+(applied is null?"":$" · motor: {f.Format(f.Read(applied))}"),342,y+43,16,Muted,735);
    }
    private void Advanced(Graphics g)
    {
        DrawText(g,"Avanzado",320,87,30,null,1200,true);
        DrawText(g,"Respuesta común para AC y Batería · curvas y límites siguen siendo independientes",320,132,20,Muted,1300);
        string[] tabs=["Temperatura CPU","Suavizado e inercia","Historial de carga","Respuesta térmica"];
        for(int i=0;i<4;i++)Button(g,"advanced-tab-"+i,tabs[i],new(320+i*334,180,317,49),AdvancedTab==i);
        Card(g,new(320,249,807,578));Card(g,new(1148,249,500,578));
        float y=AdvancedTab==2?331:273;
        if(AdvancedTab==0)
        {
            DrawText(g,"Temperatura usada para calcular demanda",342,269,24,null,750,true);
            string[] sources=["Paquete / núcleo más caliente","Media de todos los núcleos","Media de P-Cores","Media de los N P-Cores más calientes"];
            for(int i=0;i<4;i++)Button(g,"tuning-source-"+i,sources[i],new(342,319+i*66,751,51),(int)DraftTuning.CpuTemperatureSource==i);
            TuningRow(g,ProductAdvancedSettings.Fields[0],591);
            var count=FreshSnapshot?.CpuCoreTemperatures.Count(c=>c.CoreType=="Performance");
            DrawText(g,count is >0?$"P-Cores detectados: {count}":"P-Cores detectados: sin lectura vigente",342,690,20,Muted,750);
            DrawText(g,"La emergencia siempre usa el paquete o núcleo más caliente, cualquiera sea la fuente de demanda.",342,742,21,Yellow,750);
        }
        else
        {
            if(AdvancedTab==2)Button(g,"tuning-adaptive",DraftTuning.AdaptiveDescentEnabled?"✓  Descenso según historial de carga":"○  Descenso fijo",new(342,270,751,46),DraftTuning.AdaptiveDescentEnabled);
            foreach(var f in ProductAdvancedSettings.Fields.Where(f=>f.Tab==AdvancedTab)){TuningRow(g,f,y);y+=77;}
            if(AdvancedTab==3)
            {
                Button(g,"tuning-remember",DraftTuning.RememberThermalDemand?"✓  Conservar picos en el filtro":"○  Filtrar sin conservar el pico térmico",new(342,519,751,48),DraftTuning.RememberThermalDemand);
                DrawText(g,"Rango físico: 10–50 · subida térmica: 4 niveles · bajada: 1 nivel",342,600,21,Muted,750);
                DrawText(g,"Protección de revisión: CPU >90 °C, recuperación en 2 s; CPU ≥99 °C inmediata. GPU >82 °C inmediata.",342,654,21,Yellow,750,height:80);
                DrawText(g,"Estos umbrales de respuesta adelantan la ventilación. Las protecciones, la frescura y la autoridad permanecen obligatorias.",342,745,20,Muted,750);
            }
        }
        DrawText(g,"Edición y motor",1178,276,27,null,438,true);
        DrawText(g,TuningPending?"Cambios pendientes de aplicar":"Ajustes coinciden con el motor",1178,331,22,TuningPending?Yellow:Green,438);
        DrawText(g,Dirty?"Preferencias sin guardar":"Preferencias guardadas",1178,383,21,Dirty?Yellow:Muted,438);
        DrawText(g,"Aplicar conserva la curva, los límites, el nivel actual y el filtro. Las confirmaciones empiezan de nuevo.",1178,437,21,Muted,438,height:90);
        DrawText(g,"Cambiar la definición de carga reinicia su contador. Guardar conserva los ajustes para próximos inicios.",1178,541,21,Muted,438,height:90);
        Button(g,"tuning-apply",State.FanMode=="Automatic"?"Aplicar en Automático":"Preparar ajustes",new(1178,650,438,47),true,CanApplyTuning);
        Button(g,"save","Guardar configuración",new(1178,708,438,47));
        Button(g,"tuning-reset","Restablecer ajustes",new(1178,766,438,43));
    }
}

internal sealed partial class ProductForm
{
    internal bool TryEditTuningValue(string key,string text,out string error)
    {
        error="";var f=ProductAdvancedSettings.Find(key);
        if(_canvas.Busy||_closing||IsDisposed){error="Espera a que termine la operación actual.";return false;}
        if(f is null){error="Ajuste desconocido.";return false;}
        if(!double.TryParse(text.Trim().Replace(',','.'),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||
            !double.IsFinite(value)||value<f.Min||value>f.Max||f.Integer&&value!=Math.Truncate(value))
        {error=$"Introduce un valor {(f.Integer?"entero ":"")}entre {f.Min} y {f.Max}.";return false;}
        try{StageTuning(f.Write(_canvas.DraftTuning,value));return true;}
        catch(InvalidDataException ex){error=ex.Message;return false;}
    }
    private void StageTuning(AdaptiveFanTuning tuning)
    {
        tuning.Validate();Change(_draft with{Ac=_draft.Ac with{Fan=_draft.Ac.Fan with{Tuning=tuning}},Battery=_draft.Battery with{Fan=_draft.Battery.Fan with{Tuning=tuning}}});
    }
    private bool HandleAdvancedCommand(string id)
    {
        if(id.StartsWith("advanced-tab-")){_canvas.AdvancedTab=Math.Clamp(int.Parse(id[13..]),0,3);_canvas.Invalidate();return true;}
        if(!id.StartsWith("tuning-"))return false;
        if(_canvas.Busy||_closing)return true;
        try
        {
            if(id=="tuning-apply")
            {
                if(!_canvas.CanApplyTuning){_canvas.Notice="Espera datos vigentes y Firmware o Automático activo; ninguna transición o recuperación pendiente.";return true;}
                var tuning=_canvas.DraftTuning;
                _ = RunAsync(async()=>{if(_runtime is null)throw new InvalidOperationException("Runtime no disponible.");await _runtime.ApplyFanTuningAsync(tuning);
                    _canvas.Notice="Ajustes preparados o aplicados; Guardar los conserva para próximos inicios.";});return true;
            }
            if(id=="tuning-reset"){StageTuning(VictusFanControl.Product.ProductProfiles.DefaultProfile(VictusFanControl.Product.ProductPowerProfile.Ac).Fan.Tuning);return true;}
            if(id.StartsWith("tuning-source-")){StageTuning(_canvas.DraftTuning with{CpuTemperatureSource=(CpuDemandTemperatureSource)int.Parse(id[14..])});return true;}
            if(id=="tuning-adaptive"){StageTuning(_canvas.DraftTuning with{AdaptiveDescentEnabled=!_canvas.DraftTuning.AdaptiveDescentEnabled});return true;}
            if(id=="tuning-remember"){StageTuning(_canvas.DraftTuning with{RememberThermalDemand=!_canvas.DraftTuning.RememberThermalDemand});return true;}
            var suffix=id.EndsWith("-text")?5:id.EndsWith("-minus")?6:id.EndsWith("-plus")?5:0;
            var f=suffix==0?null:ProductAdvancedSettings.Find(id[7..^suffix]);if(f is null)return true;
            if(id.EndsWith("-text"))
            {
                using var dialog=new ProductNumericDialog(f.Label+" ("+f.Unit+")",f.Read(_canvas.DraftTuning),f.Min,f.Max,
                    text=>TryEditTuningValue(f.Key,text,out var error)?null:error,f.Integer,"Editar cambia el borrador de AC y Batería. Aplicar actualiza el motor; Guardar conserva los cambios.");
                dialog.ShowDialog(this);
            }
            else if(!TryEditTuningValue(f.Key,Math.Clamp(f.Read(_canvas.DraftTuning)+(id.EndsWith("-plus")?f.Step:-f.Step),f.Min,f.Max).ToString(CultureInfo.InvariantCulture),out var error))_canvas.Notice=error;
        }
        catch(Exception ex){_canvas.Notice=ex.Message;}
        finally{_canvas.Invalidate();}
        return true;
    }
}
