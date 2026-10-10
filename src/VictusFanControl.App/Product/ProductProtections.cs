using VictusFanControl.Product;

namespace VictusFanControl.App;

/// <summary>Only resumes a previously running Automatic request, after a clean boundary. Bounded backoff avoids restart loops.</summary>
internal sealed class ProductAutomaticResumption
{
    private bool _wanted, _suppressed;
    private long? _interruptedAt, _healthyAt;
    private int _attempts;
    internal void Cancel() { _suppressed=true; _wanted=false; _interruptedAt=null; _healthyAt=null; _attempts=0; }
    internal void Arm() { Cancel(); _suppressed=false; }
    internal void Observe(ProductRuntimeState state, long now)
    {
        if(_suppressed||state.AutomaticReview)return;
        if(state.FanMode=="Automatic"&&!state.LifecycleBlocked)
        {
            _wanted=true;_interruptedAt=null;_healthyAt??=now;
            if(now-_healthyAt>=60000)_attempts=0;
        }
        else if(_wanted&&state.LifecycleBlocked) { _interruptedAt??=now;_healthyAt=null; }
    }
    internal bool TakeRequest(ProductRuntimeState state, ProductProtectionSettings settings, DateTimeOffset utc, long now)
    {
        if(!_wanted||!settings.ResumeAutomatic||!_interruptedAt.HasValue||_attempts>=3||
            now<_interruptedAt||now-_interruptedAt<(new[]{10000,30000,60000})[_attempts]||
            !state.LifecycleBlocked||state.FanAuthority!="Firmware"||state.Runtime!="Healthy"||
            state.AutomaticPreparing||state.PerformanceUpdating||!state.AutomaticAuthorized)return false;
        var s=state.Snapshot;
        if(s is null||!s.IsComplete||!s.IsFanTelemetryFreshAt(utc)||utc<s.Timestamp||utc-s.Timestamp>=TimeSpan.FromSeconds(3)||
            s.CpuControlTemperatureC is not (>=0 and <=90)||s.GpuTemperatureC is not (>=0 and <=82))return false;
        ++_attempts;_interruptedAt=now;return true;
    }
}

internal sealed partial class ProductCanvas
{
    private void Protections(Graphics g)
    {
        DrawText(g,"Protecciones",320,88,31,null,1300,true);
        DrawText(g,"Decide cuándo Automático vuelve a Firmware y cómo se reanuda.",320,132,22,Muted,1300);
        var p=Profiles.Protections;
        void Row(int i,string id,string title,string detail,bool enabled)
        {
            var y=187+i*125;Card(g,new(310,y,1337,112));
            DrawText(g,title,334,y+15,24,null,960,true);
            DrawText(g,detail,334,y+54,19,Muted,985,height:56);
            Button(g,id,enabled?"✓  Activada":"○  Desactivada",new(1370,y+28,249,53),enabled);
        }
        Row(0,"protections-cpu","Volver a Firmware por temperatura CPU","Activa: CPU ≥99 °C inmediata; ≥95 °C inicia confirmación con plazo de 2 s.\nDesactivada: sigue la curva con enfriamiento máximo ante calor.",p.CpuThermalHandoff);
        Row(1,"protections-gpu","Volver a Firmware por temperatura GPU","Activa: GPU >82 °C cancela Automático; a 87 °C actúa el control térmico compartido.\nDesactivada: Automático continúa la curva.",p.GpuThermalHandoff);
        Row(2,"protections-power","Volver a Firmware por el margen de potencia","Activa: CPU >60 W o GPU >75 W cancela Automático.\nDesactivada: conserva tus límites CPU/GPU y acepta valores plausibles.",p.PowerEnvelopeHandoff);
        Row(3,"protections-resume","Reanudar Automático después de una interrupción","Libera la sesión previa y espera lecturas nuevas; CPU ≤90 °C / GPU ≤82 °C.\nHasta 3 intentos con espera; se habilita si Automático ya estaba activo.",p.ResumeAutomatic);
        DrawText(g,"Siempre activos: sensores completos y recientes, identidad del equipo y exclusión de operaciones WMI inciertas. Las protecciones térmicas del firmware permanecen activas.",334,708,20,Muted,1280,height:61);
        DrawText(g,"Guardar conserva las preferencias. Aplicar libera el control actual y prepara una sesión nueva con estos ajustes.",334,782,19,Muted,815,height:52);
        Button(g,"save","Guardar",new(1163,785,175,45),enabled:!Busy);
        Button(g,"protections-apply","Aplicar y reanudar",new(1350,785,272,45),enabled:!Busy&&State.AutomaticAuthorized&&AutomaticRetryAvailable);
    }
}

internal sealed partial class ProductForm
{
    private readonly ProductAutomaticResumption _automaticResumption=new();
    private ProductProfiles? _resumptionProfiles;
    private void ObserveAutomaticResumption(ProductRuntimeState state)
    {
        if(state.FanMode=="Automatic"&&!state.LifecycleBlocked)
            _resumptionProfiles=ProductProfilesStore.Copy(state.AutomaticResumeProfiles??_startupPreferences??Draft);
        _automaticResumption.Observe(state,Environment.TickCount64);
    }
    internal void TryAutomaticResumption(long? clock=null)
    {
        if(_closing||_restarting||IsDisposed||_canvas.Busy||_displayOff||_automaticReview is not null||
            !_canvas.AutomaticRetryAvailable||_resumptionProfiles is null||!Draft.Protections.ResumeAutomatic)return;
        if(_automaticResumption.TakeRequest(_canvas.State,_resumptionProfiles.Protections,DateTimeOffset.UtcNow,clock??Environment.TickCount64))
        {
            _canvas.Notice="Reanudando Automático: liberando la sesión anterior antes de verificar sensores nuevos…";
            _ = RetryAutomaticAsync(unattended:true,preferences:_resumptionProfiles);
        }
    }
    private bool HandleProtectionCommand(string id)
    {
        if(!id.StartsWith("protections-",StringComparison.Ordinal))return false;
        if(_canvas.Busy)return true;
        var p=_draft.Protections;
        if(id=="protections-apply")
        {
            _automaticResumption.Arm();
            _ = RetryAutomaticAsync(applyProtections:true);
            return true;
        }
        var next=id switch
        {
            "protections-cpu"=>p with{CpuThermalHandoff=!p.CpuThermalHandoff},
            "protections-gpu"=>p with{GpuThermalHandoff=!p.GpuThermalHandoff},
            "protections-power"=>p with{PowerEnvelopeHandoff=!p.PowerEnvelopeHandoff},
            "protections-resume"=>p with{ResumeAutomatic=!p.ResumeAutomatic},
            _=>p
        };
        Change(_draft with{Protections=next});
        if(!next.ResumeAutomatic)_automaticResumption.Cancel();
        _canvas.Notice="Protecciones en edición. Aplica para preparar Automático; Guarda para conservar los ajustes.";
        return true;
    }
}
