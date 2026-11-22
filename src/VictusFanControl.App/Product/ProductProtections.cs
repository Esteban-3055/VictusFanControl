using VictusFanControl.Product;

namespace VictusFanControl.App;

/// <summary>Only resumes a previously running Automatic request, after a clean boundary. Bounded backoff avoids restart loops.</summary>
internal sealed class ProductAutomaticResumption
{
    private readonly ProductRecoveryEpisode _episode;
    internal ProductAutomaticResumption(ProductRecoveryEpisode? episode=null)=>_episode=episode??new();
    private bool _wanted, _suppressed;
    private long? _interruptedAt, _healthyAt;
    internal bool Wanted => _wanted && !_suppressed;
    internal void Cancel() { _suppressed=true; _wanted=false; _interruptedAt=null; _healthyAt=null; }
    internal void Arm() { Cancel(); _suppressed=false; }
    internal bool ConfirmActivation()
    {
        if(_suppressed)return false;
        _wanted=true;_interruptedAt=null;_healthyAt=null;return true;
    }
    internal void Observe(ProductRuntimeState state, long now)
    {
        if(state.CtgpFaulted&&!state.CtgpIndependent||state.AutomaticResumptionBlocked) { Cancel(); return; }
        if(_suppressed||state.AutomaticReview)return;
        if(state.FanMode=="Automatic"&&!state.LifecycleBlocked)
        {
            _wanted=true;_interruptedAt=null;_healthyAt??=now;
        }
        else if(_wanted&&state.LifecycleBlocked) { _interruptedAt??=now;_healthyAt=null; }
    }
    internal bool TakeRequest(ProductRuntimeState state, ProductProtectionSettings settings, DateTimeOffset utc, long now)
    {
        if(state.CtgpFaulted&&!state.CtgpIndependent||state.AutomaticResumptionBlocked||!_wanted||!settings.ResumeAutomatic||!_interruptedAt.HasValue||
            now<_interruptedAt||now-_interruptedAt<_episode.DelayMilliseconds||
            !state.LifecycleBlocked||state.FanAuthority!="Firmware"||state.Runtime!="Healthy"||
            state.AutomaticPreparing||state.PerformanceUpdating||!state.AutomaticAuthorized)return false;
        var s=state.Snapshot;
        if(s is null||!s.IsComplete||!s.IsFanTelemetryFreshAt(utc,3000)||s.RetainedTelemetry is not null||utc<s.Timestamp||utc-s.Timestamp>=TimeSpan.FromSeconds(3)||
            s.CpuControlTemperatureC is not (>=0 and <=90)||s.GpuTemperatureC is not (>=0 and <=95))return false;
        if(!_episode.TryReserve(now))return false;
        _interruptedAt=now;return true;
    }
}

internal sealed partial class ProductCanvas
{
    private bool AutomaticPolicyInUse => State.FanMode=="Automatic"&&State.FanAuthority=="Custom"&&
        State.Runtime=="Healthy"&&!State.LifecycleBlocked&&!State.AutomaticPreparing;
    internal string AutomaticThermalPolicySummary
    {
        get
        {
            if(State.AutomaticReview)return "Revisión acotada: conserva la envolvente térmica y los plazos originales de la prueba.";
            var policy=State.AppliedProtections??Profiles.Protections;
            var source=State.AppliedProtections is null?"En edición":AutomaticPolicyInUse?"En uso":"Última sesión";
            return source+": "+(policy.CpuThermalHandoff?"CPU ≥95 °C hasta 2 s; ≥99 °C inmediata":"cesión térmica CPU desactivada")+
                "; "+(policy.GpuThermalHandoff?"GPU >95 °C solicita Firmware":"cesión térmica GPU desactivada")+".";
        }
    }
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
            DrawText(g,"Preferencia en edición",1370,y+6,14,Muted,249,centered:true,height:19);
            Button(g,id,enabled?"✓  Activada":"○  Desactivada",new(1370,y+28,249,53),enabled);
            var applied=State.AppliedProtections is {} active ? i switch
                {0=>active.CpuThermalHandoff,1=>active.GpuThermalHandoff,_=>active.ResumeAutomatic&&p.ResumeAutomatic} : (bool?)null;
            var label=applied is null?"Sin sesión aplicada":
                (AutomaticPolicyInUse?"En uso: ":"Última sesión: ")+(applied.Value?"activada":"desactivada");
            DrawText(g,label,1370,y+87,15,applied.HasValue&&applied!=enabled?Yellow:Muted,249,centered:true,height:22);
        }
        Row(0,"protections-cpu","Volver a Firmware por temperatura CPU","Activa: CPU ≥99 °C inmediata; ≥95 °C inicia confirmación con plazo de 2 s.\nDesactivada: sigue la curva con enfriamiento máximo ante calor.",p.CpuThermalHandoff);
        Row(1,"protections-gpu","Volver a Firmware por temperatura GPU","Activa: GPU >95 °C cancela Automático; exactamente 95 °C mantiene el control.\nDesactivada: Automático continúa la curva.",p.GpuThermalHandoff);
        Row(2,"protections-resume","Reanudar Automático después de una interrupción","Libera la sesión previa y espera lecturas nuevas; CPU ≤90 °C / GPU ≤95 °C.\nHasta 3 intentos con espera; se habilita si Automático ya estaba activo.",p.ResumeAutomatic);
        DrawText(g,"El consumo CPU/GPU no cancela Automático. Los límites PL1/PL2 configurados se aplican por separado.",334,590,20,Muted,1280,height:61);
        DrawText(g,"Desactivar la reanudación impide nuevos intentos de inmediato. Activarla se confirma al aplicar.",334,660,18,Muted,1280,height:38);
        DrawText(g,"Siempre activos: sensores completos y recientes, identidad del equipo y exclusión de operaciones WMI inciertas. Las protecciones térmicas del firmware permanecen activas.",334,708,20,Muted,1280,height:61);
        DrawText(g,"Guardar conserva las preferencias. Aplicar libera el control actual y prepara una sesión nueva con estos ajustes.",334,782,19,Muted,815,height:52);
        Button(g,"save","Guardar",new(1163,785,175,45),enabled:!Busy);
        Button(g,"protections-apply","Aplicar y reanudar",new(1350,785,272,45),enabled:!Busy&&State.AutomaticAuthorized&&AutomaticRetryAvailable);
    }
}

internal sealed partial class ProductForm
{
    private readonly ProductRecoveryEpisode _recoveryEpisode=new();
    private readonly ProductAutomaticResumption _automaticResumption;
    private ProductProfiles? _resumptionProfiles;
    private void RememberConfirmedAutomaticActivation(IProductRuntime owner,ProductProfiles profiles)
    {
        // Called only after the owner's activation task succeeds. Presentation
        // may coalesce every Automatic frame; it must not own this intent.
        if(!ReferenceEquals(owner,_runtime)||owner.State.AutomaticResumptionBlocked||_closing||_automaticReview is not null||_automaticRecoveryResumeSuppressed||!_automaticResumption.ConfirmActivation())return;
        _resumptionProfiles=ProductProfilesStore.Freeze(owner.State.AutomaticResumeProfiles??profiles);
        _automaticResumption.Observe(_canvas.State,Environment.TickCount64);
    }
    private void ObserveAutomaticResumption(ProductRuntimeState state)
    {
        // The latest owner-bound publication can already be interrupted when a
        // committed live update and the fault arrive before one UI paint. Keep
        // its confirmed configuration even if the intermediate frame was dropped.
        if(state.AutomaticResumeProfiles is {} confirmed&&!ReferenceEquals(_resumptionProfiles,confirmed))
            _resumptionProfiles=confirmed;
        else if(state.FanMode=="Automatic"&&!state.LifecycleBlocked&&_resumptionProfiles is null)
            _resumptionProfiles=_startupPreferences??ProductProfilesStore.Copy(Draft);
        _automaticResumption.Observe(state,Environment.TickCount64);
    }
    internal void TryAutomaticResumption(long? clock=null)
    {
        if(_closing||_restarting||IsDisposed||_canvas.Busy||_displayOff||_systemSuspended||_startupAutomatic is {Finished:false}||_automaticReview is not null||
            !_canvas.AutomaticRetryAvailable||_resumptionProfiles is null||!_draft.Protections.ResumeAutomatic)return;
        if(_automaticResumption.TakeRequest(_canvas.State,_resumptionProfiles.Protections,DateTimeOffset.UtcNow,clock??Environment.TickCount64))
        {
            _canvas.Notice="Reanudando Automático: liberando la sesión anterior antes de verificar sensores nuevos…";
            _ = RetryAutomaticAsync(unattended:true,preferences:_resumptionProfiles);
        }
    }
    private bool HandleProtectionCommand(string id)
    {
        if(!id.StartsWith("protections-",StringComparison.Ordinal))return false;
        if(_canvas.Busy || id=="protections-power")return true;
        var p=_draft.Protections;
        if(id=="protections-apply")
        {
            _automaticRecoveryResumeSuppressed=false;
            _automaticResumption.Arm();
            _ = RetryAutomaticAsync(applyProtections:true);
            return true;
        }
        var next=id switch
        {
            "protections-cpu"=>p with{CpuThermalHandoff=!p.CpuThermalHandoff},
            "protections-gpu"=>p with{GpuThermalHandoff=!p.GpuThermalHandoff},
            "protections-resume"=>p with{ResumeAutomatic=!p.ResumeAutomatic},
            _=>p
        };
        Change(_draft with{Protections=next});
        _canvas.Notice="Protecciones en edición. Aplica para preparar Automático; Guarda para conservar los ajustes.";
        return true;
    }
}
