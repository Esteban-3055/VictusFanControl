using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private bool _restarting,_restartLifecycleInterrupted;
    private Task? _restartTask,_runtimeShutdown;
    private IProductRuntime? _shutdownRuntimeOwner;
    private bool _runtimeShutdownPreservesPerformance;
    private readonly string? _restartDirectory;
    private readonly bool _isolatedRuntime;
    private readonly bool _restartOpening;
    internal ProductRestartRequest? RestartRequest { get; private set; }
    internal Task RestartSessionAsync()
    {
        if(_closing||IsDisposed||!_canvas.RestartAvailable)return _restartTask??Task.CompletedTask;
        if(_canvas.OptionalEditsPending)
        {
            _canvas.Notice="Guarda o descarta los cambios de HyperX y de la base externa antes de reiniciar la sesión.";
            _canvas.Invalidate();
            return Task.CompletedTask;
        }
        return _restartTask is { IsCompleted: false } pending ? pending : (_restartTask = RestartSessionCoreAsync());
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
            _ = ProductDiagnostics.TryExportAsync(diagnostic,before,ProductProfilesStore.Parse(checkpoint.DraftJson),AppLog.CurrentLogPath);
            // Even a full diagnostic disk must not skip hardware cleanup.
            await ShutdownRuntimeAsync(confirmLateReadDrain: true);
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
            if(_canvas.OptionalEditsPending)throw new IOException("Se detectaron cambios de HyperX o de la base externa durante el reinicio. Los cambios siguen en edición.");
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
            _canvas.RestartAvailable = _shutdownRuntimeOwner is IProductLateReadDrain { HasPendingReadDrain: true };
            _canvas.Notice=_startupFailure+(_canvas.RestartAvailable
                ? " Puedes reintentar cuando termine la lectura WMI."
                : " Conserva el diagnóstico y los registros de recuperación. Usa Salir desde la bandeja.");
            UpdateTray(_canvas.State);
            _canvas.Busy=false;_canvas.Invalidate();
        }
        finally{_restarting=false;}
    }
    private Task ShutdownRuntimeAsync(bool confirmLateReadDrain = false,bool fanRetry=false)
    {
        if (confirmLateReadDrain && _runtimeShutdown is { IsFaulted: true } &&
            _shutdownRuntimeOwner is IProductLateReadDrain { HasPendingReadDrain: true } pending)
        {
            if(_runtimeShutdownPreservesPerformance&&_shutdownRuntimeOwner is IProductFanRetry retained)
            {
                _runtimeShutdownPreservesPerformance=fanRetry;
                return _runtimeShutdown=ConfirmScopedReadAsync(pending,retained,_shutdownRuntimeOwner,fanRetry);
            }
            return _runtimeShutdown=pending.ConfirmReadDrainAsync();
        }
        // A completed fan-only retirement is not a global release receipt.
        // Exit, restart, recovery or failed construction must still close CPU/GPU.
        if(!fanRetry&&_runtimeShutdownPreservesPerformance&&_shutdownRuntimeOwner is{} owner)
        {
            _runtimeShutdownPreservesPerformance=false;
            return _runtimeShutdown=Task.Run(async()=>await owner.DisposeAsync());
        }
        if(_runtimeShutdown is not null)return _runtimeShutdown;
        _runtimeShutdownPreservesPerformance=fanRetry&&_runtime is IProductFanRetry;
        return _runtimeShutdown=ShutdownRuntimeCoreAsync(_runtimeShutdownPreservesPerformance);
    }
    private static async Task ConfirmScopedReadAsync(IProductLateReadDrain pending,IProductFanRetry retained,IProductRuntime owner,bool fanRetry)
    {
        await pending.ConfirmReadDrainAsync();
        if(fanRetry)await retained.RetireFanRuntimeAsync();else await owner.DisposeAsync();
    }
    private async Task ShutdownRuntimeCoreAsync(bool preservePerformance)
    {
        if(!preservePerformance)_externalCooling?.Suspend();
        Exception? startupFailure = null;
        // File-only startup can finish late but checks the lifecycle before
        // constructing anything. Native construction, once begun, must drain.
        try { if(_startupResourcesAcquired)await _startup; }
        catch(Exception ex) { startupFailure=ex;AppLog.Write("Product startup task failed; attempting known runtime cleanup: "+ex); }
        if(_commandsDrained is not null)await _commandsDrained.Task;
        Exception? previousReleaseFailure = null;
        try { await _lifecycleRelease; }
        catch(Exception ex)
        {
            previousReleaseFailure = ex;
            AppLog.Write("Product earlier lifecycle release failed; awaiting final runtime cleanup: "+ex);
        }
        _shutdownRuntimeOwner = _runtime;
        if(_shutdownRuntimeOwner is IProductFanRetry retained&&preservePerformance)await Task.Run(retained.RetireFanRuntimeAsync);
        else if(_shutdownRuntimeOwner is { } owner)await Task.Run(async()=>await owner.DisposeAsync());
        else if(previousReleaseFailure is not null || startupFailure is not null)throw new IOException("No hay controlador disponible para confirmar la liberación anterior.",previousReleaseFailure??startupFailure);
    }
}
