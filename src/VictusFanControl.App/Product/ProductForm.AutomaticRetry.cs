using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly Func<ProductProfiles,Task<IProductRuntime>>? _retryRuntimeFactory;
    private Task? _automaticRetryTask;
    private Action<ProductRuntimeState>? _runtimeChanged;
    private LatestUiMailbox<ProductRuntimeState>? _runtimeMailbox;
    private long _lastRuntimePublication;
    private void AttachRuntime(IProductRuntime runtime)
    {
        _lastRuntimePublication=0;_minimumRuntimePublication=0;_powerFenceFailure=false;
        void Observe(ProductRuntimeState state)
        {
            if(!ReferenceEquals(_runtime,runtime)||_closing||IsDisposed)return;
            if(state.PublicationSequence>0)
            {
                if(state.PublicationSequence<=_lastRuntimePublication||state.PublicationSequence<_minimumRuntimePublication)return;
                _lastRuntimePublication=state.PublicationSequence;
            }
            UpdateState(state);
        }
        var mailbox=new LatestUiMailbox<ProductRuntimeState>(action=>{if(IsHandleCreated&&!IsDisposed)BeginInvoke(action);},Observe,state=>state.PublicationSequence);
        _runtimeMailbox=mailbox;
        if(_externalCooling is not null)_=ExternalCoolingOperationAsync(()=>_externalCooling.ResumeAsync());
        _runtimeChanged=state=>
        {
            if(IsDisposed||_closing)return;
            if(InvokeRequired)
            {
                mailbox.Post(state);
            }
            else Observe(state);
        };
        runtime.Changed+=_runtimeChanged;
    }
    private void DetachRuntime()
    {
        if(_runtime is not null&&_runtimeChanged is not null)_runtime.Changed-=_runtimeChanged;
        _runtimeMailbox?.Dispose();_runtimeMailbox=null;
        _runtimeChanged=null;
    }
    internal Task RetryAutomaticAsync(bool applyProtections=false, bool unattended=false, ProductProfiles? preferences=null)
    {
        if(_automaticRetryTask is not null)return _automaticRetryTask;
        if(_closing||_restarting||IsDisposed||_canvas.Busy||_systemSuspended||(!applyProtections&&!_canvas.State.LifecycleBlocked)||!_canvas.AutomaticRetryAvailable||
            !_canvas.State.AutomaticAuthorized||_displayOff||_automaticReview is not null)return Task.CompletedTask;
        return _automaticRetryTask=RetryAutomaticCoreAsync(unattended,preferences);
    }
    private async Task RetryAutomaticCoreAsync(bool unattended,ProductProfiles? requestedPreferences)
    {
        _restarting=true;_restartLifecycleInterrupted=false;_canvas.Busy=true;_canvas.RestartAvailable=false;
        var retryReason = unattended ? "Reanudación automática" : "Reintento manual de Automático";
        _presentationTimer.Stop();_startupAutomatic?.Cancel(retryReason);
        var before=_canvas.State;
        var releasedRuntime=_runtime as ProductRuntime;
        try
        {
            _runtime?.FenceLifecycle(retryReason);
            DetachRuntime();
            _canvas.Notice="Liberando la interrupción anterior antes de preparar Automático…";_canvas.Invalidate();
            // Drain every pending operation and release all domains; never reuse a faulted controller/worker.
            await ShutdownRuntimeAsync();_recoveryEpisode.ConfirmOwnerReleased();
            if(!_isolatedRuntime)ProductSessionRestart.EnsureNoRecoveryRecords();
            if(_closing||_exitRequested)return;
            if(_displayOff||_restartLifecycleInterrupted)throw new IOException("Reintento cancelado por suspensión o pantalla apagada.");
            var diagnostic=Path.Combine(_restartDirectory??AppLog.SessionDirectory,"diagnostic-before-automatic-retry-"+Guid.NewGuid().ToString("N")+".zip");
            var diagnosticDraft=Draft;
            _ = ProductDiagnostics.TryExportAsync(diagnostic,before,diagnosticDraft,AppLog.CurrentLogPath);
            if(_closing||_exitRequested)return;
            if(_displayOff||_restartLifecycleInterrupted)throw new IOException("Reintento cancelado por un cambio de energía.");
            var preferences=ProductProfilesStore.Copy(requestedPreferences??Draft);
            var next=await (_retryRuntimeFactory?.Invoke(preferences)??Task.Run<IProductRuntime>(()=>new ProductRuntime(_modules,preferences,releasedRuntime:releasedRuntime)));
            _runtime=next;_runtimeShutdown=null;_lifecycleRelease=Task.CompletedTask;_startupFailure=null;
            if(_closing||_exitRequested||_displayOff||_restartLifecycleInterrupted)
            {
                await ShutdownRuntimeAsync();
                throw new IOException("Reintento cancelado durante la preparación. El nuevo controlador quedó liberado.");
            }
            // Same form/log session. Frozen active preferences are used for unattended reentry; the draft stays untouched.
            _startupPreferences=preferences;_startupAutomatic=new(Environment.TickCount64,manualRetry:!unattended,protections:preferences.Protections,resumption:unattended);
            if(_requirePowerNotifications)RegisterPowerNotifications();
            _recoveryEpisode.CarryPreparation();
            AttachRuntime(next);UpdateState(next.State);next.Start();_presentationTimer.Start();
            _canvas.RestartAvailable=true;
            AppLog.Write("PRODUCT AUTOMATIC RETRY (unattended="+unattended+"): previous release completed; fresh runtime in Firmware; waiting for three fresh observations; diagnostic="+diagnostic);
        }
        catch(Exception ex)
        {
            DetachRuntime();
            // If construction/start failed, release any new instance before leaving it unusable.
            try{await ShutdownRuntimeAsync();}catch(Exception release){AppLog.Write("Automatic retry cleanup unresolved: "+release);}
            _recoveryEpisode.Finish(ownerConfirmed:_runtimeShutdown is {IsCompletedSuccessfully:true});
            var failedState=_runtime?.State??before;
            var pending = _isolatedRuntime ? failedState.PerformanceRecovery : await Task.Run(() => PerformanceRecoveryPreview.Read());
            if (!unattended || pending?.Pending != true) _automaticResumption.Cancel();
            _runtime=null;_canvas.AutomaticRetryAvailable=false;_startupAutomatic?.Cancel("Reintento no completado.");
            _canvas.State=failedState with{LifecycleBlocked=true,Runtime="Failed",Failure="No se pudo preparar Automático: "+ex.Message,PerformanceRecovery=pending,CanApplyPerformance=false,AutomaticPreparing=false};
            _canvas.Notice=_canvas.State.Failure!+" Conserva los registros de recuperación y usa Salir desde la bandeja.";
            UpdateTray(_canvas.State);
            AppLog.Write("PRODUCT AUTOMATIC RETRY BLOCKED (unattended="+unattended+"): "+ex);
        }
        finally
        {
            _restarting=false;_automaticRetryTask=null;_canvas.Busy=_pendingCommands>0;
            if(!IsDisposed){if(!_closing)_presentationTimer.Start();TryStartupAutomatic();_canvas.Invalidate();}
        }
    }
}
