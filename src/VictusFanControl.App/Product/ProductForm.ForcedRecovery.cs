using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    internal Task ForceSessionRecoveryAsync(bool confirmed = false)
    {
        if (_recoveryTask is not null) return _recoveryTask;
        if (_closing || _exitRequested || _restarting || IsDisposed || _canvas.Busy || _displayOff || _checkingUpdate || _canvas.UpdateBusy ||
            _automaticReview is not null || _isolatedRuntime && RecoveryRuntimeFactory is null && RecoveryRunner is null) return Task.CompletedTask;
        if (!confirmed && MessageBox.Show(this,
            "Se guardará un diagnóstico y se cerrará la sesión actual, incluidos sus controles de ventiladores y límites CPU/GPU. " +
            "Se abrirá una sesión nueva en Firmware y se conservarán tus perfiles y cambios sin guardar. Automático quedará desactivado.\n\n" +
            "Si una operación no puede cerrarse con seguridad, la recuperación se detendrá y conservará sus registros.\n\n¿Recuperar la sesión ahora?",
            "Recuperar sesión definitiva", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return Task.CompletedTask;
        _recoveryResumeCancelled = true;
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
            // Capture authority before closing, including the current owner's
            // lease. Only the exact remaining subset can be recovered later.
            var selected = selectedBeforeWait ?? await Task.Run(RecoverySelectionReader);
            if (selected.Fans?.Any(r => !r.Recoverable) == true)
                throw new IOException("Hay registros inválidos o de propiedad desconocida. Se conservan sin enviar órdenes.");
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) return;
            _canvas.Notice = "Guardando diagnóstico antes de cerrar la sesión…"; _canvas.Invalidate();
            try { await Task.Run(() => ProductDiagnostics.Export(diagnostic, before, preferences, AppLog.CurrentLogPath)); }
            catch(Exception ex)
            {
                // The diagnostic is optional evidence. Release the current owner
                // normally even when the disk is unavailable; do not start a
                // recovery write without its own validated backup/preflight.
                try { _runtime?.FenceLifecycle("Diagnóstico de recuperación no disponible"); }
                catch(Exception fenceFailure) { AppLog.Write("Recovery diagnostic fence failed: "+fenceFailure); }
                await ShutdownRuntimeAsync();
                DetachRuntime();_runtime=null;
                _startupFailure="Diagnóstico no disponible; la sesión se cerró. Reintenta Recuperar sesión definitiva.";
                _canvas.State=_canvas.State with { Runtime="Failed",LifecycleBlocked=true,LifecycleBlockReason=_startupFailure,
                    CanApplyPerformance=false,PerformanceActive=false,AutomaticPreparing=false,FanMode="Firmware",FanAuthority="Firmware" };
                throw new IOException("Se cerró la sesión, pero no se pudo guardar el diagnóstico. Conserva los registros antes de reintentar la recuperación. "+ex.Message,ex);
            }
            AppLog.Write("PRODUCT FORCED SESSION RECOVERY: diagnostic=" + diagnostic);
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) return;
            enteredRecovery = true;
            await RecoverPerformanceCoreAsync(selected, automaticPreferences, resume, forceSession: true);
        }
        catch (Exception ex)
        {
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
