using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly ProductAutomaticRecovery _automaticRecovery = new();
    private bool _recoveryAtStartup, _recoveryResumeCancelled, _automaticRecoveryResumeSuppressed;

    internal void TryAutomaticRecovery(long? clock = null)
    {
        var now = clock ?? Environment.TickCount64;
        _automaticRecovery.Observe(_canvas.State, now);
        if (_closing || _exitRequested || _restarting || IsDisposed || _canvas.Busy || _displayOff ||
            _automaticReview is not null || !Draft.RecoverSessionsAutomatically ||
            _isolatedRuntime && RecoveryRunner is null)
        { _automaticRecovery.Pause(); return; }
        if (!_automaticRecovery.TakeRequest(_canvas.State, now)) return;
        var resume = !_automaticRecoveryResumeSuppressed && Draft.Protections.ResumeAutomatic &&
            (_automaticResumption.Wanted && _resumptionProfiles?.Protections.ResumeAutomatic == true ||
             _recoveryAtStartup && !_restartOpening && _saved.ActivateAutomaticOnStart && _saved.Protections.ResumeAutomatic);
        var preferences = ProductProfilesStore.Copy(_automaticResumption.Wanted && _resumptionProfiles is not null
            ? _resumptionProfiles : _recoveryAtStartup ? _saved : Draft);
        _recoveryResumeCancelled = _automaticRecoveryResumeSuppressed;
        _restarting = true; _restartLifecycleInterrupted = false; _canvas.Busy = true;
        // Reserve the operation before the first background inventory read; exit drains this same task.
        _recoveryTask = RecoverInBackgroundAsync(preferences, resume, _canvas.State.PerformanceRecovery!);
    }

    private async Task RecoverInBackgroundAsync(ProductProfiles preferences, bool resume, PerformanceRecoveryPreview expected)
    {
        await Task.Yield();
        try
        {
            var selected = await Task.Run(() => _isolatedRuntime ? RecoverySelectionReader() : ProductRecoverySelection.Read(ignoreCurrentOwner:true));
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) return;
            if (!selected.Pending) return; // Normal cleanup won the race; no recovery writes.
            if (selected.CpuSession != (expected.CpuSession ?? Guid.Empty) || selected.GpuSession != (expected.GpuSession ?? Guid.Empty) ||
                !ProductRecoveryInventory.AllowsRemaining(expected.FanRecords, selected.Fans))
                throw new IOException("Los registros cambiaron durante la espera; no se recuperó otra sesión.");
            if (selected.Fans?.Any(r => !r.Recoverable) == true)
                throw new IOException("Registro inválido o de propiedad incierta; conserva el diagnóstico.");
            await RecoverPerformanceCoreAsync(selected, preferences, resume);
        }
        catch (Exception ex)
        {
            AppLog.Write("PRODUCT BACKGROUND RECOVERY BLOCKED: " + ex);
            await RecoveryUiAsync(() =>
            {
                _canvas.Notice = "Recuperación automática detenida: " + ex.Message;
                NotifyRecoveryFailure(_canvas.Notice);
            });
        }
        finally
        {
            await RecoveryUiAsync(() =>
            {
                _restarting = false; _recoveryTask = null; _canvas.Busy = _pendingCommands > 0;
                if (!_closing) { _presentationTimer.Start(); TryStartupAutomatic(); }
                _canvas.Invalidate();
            });
        }
    }

    private void NotifyRecoveryFailure(string detail)
    {
        if (_tray.Visible && !_closing)
            _tray.ShowBalloonTip(10000, "VictusFanControl · Recuperación pendiente",
                detail[..Math.Min(250, detail.Length)], ToolTipIcon.Warning);
    }
}
