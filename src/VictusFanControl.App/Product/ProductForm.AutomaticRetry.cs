using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly Func<ProductProfiles,Task<IProductRuntime>>? _retryRuntimeFactory;
    private Task? _automaticRetryTask;
    private Action<ProductRuntimeState>? _runtimeChanged;
    private void AttachRuntime(IProductRuntime runtime)
    {
        _runtimeChanged=state=>
        {
            if(IsDisposed||_closing)return;
            if(InvokeRequired)
            {
                if(IsHandleCreated)BeginInvoke(()=>{if(ReferenceEquals(_runtime,runtime))UpdateState(state);});
            }
            else if(ReferenceEquals(_runtime,runtime))UpdateState(state);
        };
        runtime.Changed+=_runtimeChanged;
    }
    private void DetachRuntime()
    {
        if(_runtime is not null&&_runtimeChanged is not null)_runtime.Changed-=_runtimeChanged;
        _runtimeChanged=null;
    }
    internal Task RetryAutomaticAsync()
    {
        if(_automaticRetryTask is not null)return _automaticRetryTask;
        if(_closing||_restarting||IsDisposed||_canvas.Busy||!_canvas.State.LifecycleBlocked||!_canvas.AutomaticRetryAvailable||
            !_canvas.State.AutomaticAuthorized||_displayOff||_automaticReview is not null)return Task.CompletedTask;
        return _automaticRetryTask=RetryAutomaticCoreAsync();
    }
    private async Task RetryAutomaticCoreAsync()
    {
        _restarting=true;_restartLifecycleInterrupted=false;_canvas.Busy=true;_canvas.RestartAvailable=false;
        _presentationTimer.Stop();_startupAutomatic?.Cancel("Reintento manual solicitado.");
        var before=_canvas.State;
        var releasedRuntime=_runtime as ProductRuntime;
        try
        {
            _runtime?.FenceLifecycle("Reintento manual de Automático");
            DetachRuntime();
            _canvas.Notice="Liberando la interrupción anterior antes de preparar Automático…";_canvas.Invalidate();
            // Drain every pending operation and release all domains; never reuse a faulted controller/worker.
            await ShutdownRuntimeAsync();
            if(!_isolatedRuntime)ProductSessionRestart.EnsureNoRecoveryRecords();
            if(_closing||_exitRequested)return;
            if(_displayOff||_restartLifecycleInterrupted)throw new IOException("Reintento cancelado por suspensión o pantalla apagada.");
            var diagnostic=Path.Combine(_restartDirectory??AppLog.SessionDirectory,"diagnostic-before-automatic-retry-"+Guid.NewGuid().ToString("N")+".zip");
            var diagnosticDraft=Draft;
            await Task.Run(()=>ProductDiagnostics.Export(diagnostic,before,diagnosticDraft,AppLog.CurrentLogPath));
            if(_closing||_exitRequested)return;
            if(_displayOff||_restartLifecycleInterrupted)throw new IOException("Reintento cancelado por un cambio de energía.");
            var preferences=Draft;
            var next=await (_retryRuntimeFactory?.Invoke(preferences)??Task.Run<IProductRuntime>(()=>new ProductRuntime(_modules,preferences,releasedRuntime:releasedRuntime)));
            _runtime=next;_runtimeShutdown=null;_lifecycleRelease=Task.CompletedTask;_startupFailure=null;
            if(_closing||_exitRequested||_displayOff||_restartLifecycleInterrupted)
            {
                await ShutdownRuntimeAsync();
                throw new IOException("Reintento cancelado durante la preparación. El nuevo controlador quedó liberado.");
            }
            // Same form, same log session and untouched draft/saved preferences. Only a new explicit request can arm this gate.
            _startupPreferences=preferences;_startupAutomatic=new(Environment.TickCount64,manualRetry:true);
            AttachRuntime(next);UpdateState(next.State);next.Start();_presentationTimer.Start();
            _canvas.RestartAvailable=true;
            AppLog.Write("PRODUCT MANUAL AUTOMATIC RETRY: previous release completed; fresh runtime in Firmware; waiting for three fresh observations; diagnostic="+diagnostic);
        }
        catch(Exception ex)
        {
            DetachRuntime();
            // If construction/start failed, release any new instance before leaving it unusable.
            try{await ShutdownRuntimeAsync();}catch(Exception release){AppLog.Write("Automatic retry cleanup unresolved: "+release);}
            _runtime=null;_canvas.AutomaticRetryAvailable=false;_startupAutomatic?.Cancel("Reintento no completado.");
            _canvas.State=before with{LifecycleBlocked=true,Runtime="Failed",Failure="No se pudo preparar Automático: "+ex.Message,CanApplyPerformance=false,AutomaticPreparing=false};
            _canvas.Notice=_canvas.State.Failure!+" Conserva los registros de recuperación y usa Salir desde la bandeja.";
            UpdateTray(_canvas.State);
            AppLog.Write("PRODUCT MANUAL AUTOMATIC RETRY BLOCKED: "+ex);
        }
        finally
        {
            _restarting=false;_automaticRetryTask=null;_canvas.Busy=_pendingCommands>0;
            if(!IsDisposed){TryStartupAutomatic();_canvas.Invalidate();}
        }
    }
}
