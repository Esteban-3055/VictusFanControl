using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private bool _restarting,_restartLifecycleInterrupted;
    private Task? _restartTask,_runtimeShutdown;
    private readonly string? _restartDirectory;
    private readonly bool _isolatedRuntime;
    private readonly bool _restartOpening;
    internal ProductRestartRequest? RestartRequest { get; private set; }
    internal Task RestartSessionAsync()
    {
        if(_closing||IsDisposed||!_canvas.RestartAvailable)return _restartTask??Task.CompletedTask;
        return _restartTask??= RestartSessionCoreAsync();
    }
    private async Task RestartSessionCoreAsync()
    {
        _restarting=true;_canvas.RestartAvailable=false;_canvas.Busy=true;_presentationTimer.Stop();
        var before=_canvas.State;var checkpoint=ProductSessionRestart.Capture(Draft,_saved,_hasSavedBaseline,_canvas.Dirty,_canvas.Page,_canvas.Editing);
        var directory=_restartDirectory??AppLog.SessionDirectory;
        var diagnostic=Path.Combine(directory,"diagnostic-before-restart.zip");
        Exception? preparationFailure=null;
        try
        {
            try{_runtime?.FenceLifecycle("Reinicio explícito de sesión");}
            catch(Exception ex){preparationFailure=ex;}
            _canvas.Notice="Guardando diagnóstico y liberando ventiladores, CPU/GPU y telemetría…";_canvas.Invalidate();
            try{await Task.Run(()=>ProductDiagnostics.Export(diagnostic,before,ProductProfilesStore.Parse(checkpoint.DraftJson),AppLog.CurrentLogPath));}
            catch(Exception ex){preparationFailure??=ex;}
            // Even a full diagnostic disk must not skip hardware cleanup.
            await ShutdownRuntimeAsync();
            // Save/import operations that were already pending may have completed while draining.
            // Preserve their final draft and saved baseline instead of an earlier UI snapshot.
            checkpoint=ProductSessionRestart.Capture(Draft,_saved,_hasSavedBaseline,_canvas.Dirty,_canvas.Page,_canvas.Editing);
            if(!_isolatedRuntime)ProductSessionRestart.EnsureNoRecoveryRecords();
            if(preparationFailure is not null)throw new IOException("No se completó la preparación del reinicio; se intentó liberar la sesión y se conservó el borrador. "+preparationFailure.Message,preparationFailure);
            if(_closing||_exitRequested)return;
            if(_displayOff||_restartLifecycleInterrupted)throw new IOException("Reinicio cancelado por suspensión o pantalla apagada. La sesión quedó cerrada; no se abrió otro controlador.");
            var statePath=Path.Combine(directory,ProductSessionRestart.StateFileName);
            await Task.Run(()=>ProductSessionRestart.WriteReleasedState(statePath,checkpoint));
            if(_closing||_exitRequested)return;
            if(_displayOff||_restartLifecycleInterrupted)throw new IOException("Reinicio cancelado por un cambio de energía; no se abrió otro controlador.");
            RestartRequest=new(statePath,_modules,_automaticReview);
            AppLog.Write("PRODUCT SESSION RESTART READY: cleanup completed; diagnostic="+diagnostic+"; draft retained; next startup=Firmware.");
            _disposedRuntime=true;_exitRequested=true;Close();_shutdown.TrySetResult();
        }
        catch(Exception ex)
        {
            AppLog.Write("PRODUCT SESSION RESTART BLOCKED: "+ex);
            DetachRuntime();
            // A partially disposed runtime is never reused or disposed a second time.
            _runtime=null;
            _startupFailure="No se pudo reiniciar la sesión: "+ex.Message;
            _canvas.State=_canvas.State with{LifecycleBlocked=true,LifecycleBlockReason=_startupFailure,Failure=_startupFailure,
                Runtime="Failed",CanApplyPerformance=false,AutomaticPreparing=false,PerformanceUpdating=false};
            _canvas.Notice=_startupFailure+" Conserva el diagnóstico y los registros de recuperación. Usa Salir desde la bandeja.";
            _canvas.Busy=false;_canvas.Invalidate();
        }
        finally{_restarting=false;}
    }
    private Task ShutdownRuntimeAsync()=>_runtimeShutdown??= ShutdownRuntimeCoreAsync();
    private async Task ShutdownRuntimeCoreAsync()
    {
        await _startup;
        if(_commandsDrained is not null)await _commandsDrained.Task;
        await _lifecycleRelease;
        if(_runtime is not null)await Task.Run(async()=>await _runtime.DisposeAsync());
    }
}
