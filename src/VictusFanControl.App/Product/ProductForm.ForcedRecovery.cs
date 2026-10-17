using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    internal Task ForceSessionRecoveryAsync(bool confirmed = false)
    {
        if (_recoveryTask is not null) return _recoveryTask;
        if (_closing || _exitRequested || _restarting || IsDisposed || _canvas.Busy || _displayOff || _checkingUpdate || _canvas.UpdateBusy ||
            _automaticReview is not null || _isolatedRuntime && RecoveryRuntimeFactory is null) return Task.CompletedTask;
        if (!confirmed && MessageBox.Show(this,
            "Se guardará un diagnóstico y se cerrará la sesión actual, incluidos sus controles de ventiladores y límites CPU/GPU. " +
            "Se abrirá una sesión nueva en Firmware y se conservarán tus perfiles y cambios sin guardar. Automático quedará desactivado.\n\n" +
            "Si una operación no puede cerrarse con seguridad, la recuperación se detendrá y conservará sus registros.\n\n¿Recuperar la sesión ahora?",
            "Recuperación forzosa de sesión", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return Task.CompletedTask;
        _recoveryResumeCancelled = true;
        _automaticResumption.Cancel();
        _startupAutomatic?.Cancel("Recuperación forzosa solicitada."); _startupAutomatic = null;
        return _recoveryTask = ForceSessionRecoveryCoreAsync();
    }

    private async Task ForceSessionRecoveryCoreAsync()
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
            _canvas.Notice = "Guardando diagnóstico antes de cerrar la sesión…"; _canvas.Invalidate();
            await Task.Run(() => ProductDiagnostics.Export(diagnostic, before, preferences, AppLog.CurrentLogPath));
            AppLog.Write("PRODUCT FORCED SESSION RECOVERY: diagnostic=" + diagnostic);
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) return;
            enteredRecovery = true;
            // This action drains only the current session. Any inherited/remaining
            // record requires the existing exact-selection recovery flow.
            await RecoverPerformanceCoreAsync(new ProductRecoverySelection(Guid.Empty, Guid.Empty), forceSession: true);
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
