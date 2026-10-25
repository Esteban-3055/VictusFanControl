using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    internal Task ForceSessionRecoveryAsync(bool confirmed = false)
    {
        if (_recoveryTask is not null) return _recoveryTask;
        if (_closing || _exitRequested || _restarting || IsDisposed || _canvas.Busy || _displayOff || _systemSuspended || _checkingUpdate || _canvas.UpdateBusy ||
            _automaticReview is not null || _isolatedRuntime && RecoveryRuntimeFactory is null && RecoveryRunner is null) return Task.CompletedTask;
        if (!confirmed && MessageBox.Show(this,
            "Se guardará un diagnóstico y se cerrará la sesión actual, incluidos sus controles de ventiladores y límites CPU/GPU. " +
            "Se abrirá una sesión nueva en Firmware y se conservarán tus perfiles y cambios sin guardar. Automático quedará desactivado.\n\n" +
            "Si una operación no puede cerrarse con seguridad, la recuperación se detendrá y conservará sus registros.\n\n¿Recuperar la sesión ahora?",
            "Recuperar sesión definitiva", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return Task.CompletedTask;
        _recoveryResumeCancelled = true;
        _automaticRecoveryResumeSuppressed=true;
        _automaticResumption.Cancel();
        _startupAutomatic?.Cancel("Recuperación forzosa solicitada."); _startupAutomatic = null;
        return _recoveryTask = ForceSessionRecoveryCoreAsync();
    }

    private async Task ForceSessionRecoveryCoreAsync(ProductProfiles? automaticPreferences = null, bool resume = false,
        ProductRecoverySelection? selectedBeforeWait = null)
    {
        // Reserve the same task used by exit/update recovery before any background work.
        _restarting = true; _restartLifecycleInterrupted = false; _canvas.Busy = true; _presentationTimer.Stop();
        var enteredRecovery = false;
        var before = _canvas.State;
        var preferences = ProductProfilesStore.Copy(Draft);
        var diagnostic = Path.Combine(_restartDirectory ?? AppLog.SessionDirectory,
            "diagnostic-before-forced-recovery-" + Guid.NewGuid().ToString("N") + ".zip");
        await Task.Yield();
        try
        {
            // Mandatory admission fence precedes every optional read/export.
            _runtime?.FenceLifecycle("Recuperación de sesión solicitada");
            _ = ProductDiagnostics.TryExportAsync(diagnostic,before,preferences,AppLog.CurrentLogPath);
            // Capture authority before closing, including the current owner's
            // lease. Only the exact remaining subset can be recovered later.
            var selected = selectedBeforeWait ?? await Task.Run(RecoverySelectionReader);
            if (selected.Fans?.Any(r => !r.Recoverable) == true)
                throw new IOException("Hay registros inválidos o de propiedad desconocida. Se conservan sin enviar órdenes.");
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) return;
            _canvas.Notice = "Liberando la sesión; diagnóstico opcional en segundo plano…"; _canvas.Invalidate();
            AppLog.Write("PRODUCT FORCED SESSION RECOVERY: diagnostic=" + diagnostic);
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) return;
            enteredRecovery = true;
            await RecoverPerformanceCoreAsync(selected, automaticPreferences, resume, forceSession: true);
        }
        catch (Exception ex)
        {
            var released=false;
            try{await ShutdownRuntimeAsync();released=true;}
            catch(Exception releaseFailure){AppLog.Write("Forced recovery preparation failed; native cleanup remains unresolved: "+releaseFailure);}
            DetachRuntime();_runtime=null;
            _recoveryEpisode.Finish(ownerConfirmed:released);
            if(released)_recoveryEpisode.ConfirmOwnerReleased();
            _recoveryAtStartup=released;
            var unavailable=new PerformanceRecoveryPreview(true,"No se completó la comprobación de recuperación: "+ex.Message){ReadStatus=RecoveryInventoryStatus.Unavailable};
            _canvas.State=before with{Runtime="Failed",LifecycleBlocked=true,CanApplyPerformance=false,AutomaticPreparing=false,
                FanMode=released?"Firmware":"RecoveryRequired",FanAuthority=released?"Firmware":"Unknown",PerformanceRecovery=unavailable,Failure=ex.Message};
            _canvas.Notice = "No se pudo preparar la recuperación de sesión: " + ex.Message;
            AppLog.Write("PRODUCT FORCED SESSION RECOVERY BLOCKED: " + ex);
        }
        finally
        {
            if (!enteredRecovery)
            {
                _restarting = false; _recoveryTask = null; _canvas.Busy = _pendingCommands > 0;
                if (!_closing && !_exitRequested) _presentationTimer.Start();
                _canvas.Invalidate();
            }
        }
    }
}
