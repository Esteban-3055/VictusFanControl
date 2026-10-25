using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly ProductAutomaticRecovery _automaticRecovery;
    private bool _recoveryAtStartup, _recoveryResumeCancelled, _automaticRecoveryResumeSuppressed;

    internal void TryAutomaticRecovery(long? clock = null)
    {
        if(_systemSuspended){_automaticRecovery.Pause();_recoveryEpisode.Observe(new(){Runtime="Suspended"},DateTimeOffset.UtcNow);return;}
        var now = clock ?? Environment.TickCount64;
        _automaticRecovery.Observe(_canvas.State, now);
        if (ProductFanRangePreview.Enabled || _closing || _exitRequested || _restarting || IsDisposed || _canvas.Busy || _displayOff || _checkingUpdate || _canvas.UpdateBusy ||
            _automaticReview is not null || !_draft.RecoverSessionsAutomatically ||
            _isolatedRuntime && RecoveryRunner is null)
        { _automaticRecovery.Pause(); return; }
        var resume = !_automaticRecoveryResumeSuppressed && _draft.Protections.ResumeAutomatic &&
            (_automaticResumption.Wanted && _resumptionProfiles?.Protections.ResumeAutomatic == true ||
             _recoveryAtStartup && !_restartOpening && _saved.ActivateAutomaticOnStart && _saved.Protections.ResumeAutomatic);
        var preferences = _automaticResumption.Wanted && _resumptionProfiles is not null
            ? _resumptionProfiles : _recoveryAtStartup ? _saved : _draft;
        if (TryRefreshStartupInventory(now)) return;
        if (_startupInventoryAbsenceConfirmed && _runtime is null && _recoveryAtStartup)
        {
            _startupInventoryAbsenceConfirmed=false;
            _recoveryResumeCancelled = _automaticRecoveryResumeSuppressed;
            _recoveryTask = ForceSessionRecoveryCoreAsync(preferences, resume, new(Guid.Empty,Guid.Empty));
            return;
        }
        if (_canvas.State.PerformanceRecovery?.Pending != true)
        {
            if (_runtime is null || _isolatedRuntime && RecoveryRuntimeFactory is null ||
                !_automaticRecovery.TakeSessionRequest(_canvas.State, now)) return;
            _recoveryResumeCancelled = _automaticRecoveryResumeSuppressed;
            _recoveryTask = ForceSessionRecoveryCoreAsync(preferences, resume);
            return;
        }
        if (!_automaticRecovery.TakeRequest(_canvas.State, now)) return;
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
            ProductRecoverySelection selected;
            try { selected = await Task.Run(() => _isolatedRuntime ? RecoverySelectionReader() : ProductRecoverySelection.Read(ignoreCurrentOwner:true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await RecoveryUiAsync(() =>
                {
                    var retry = _automaticRecovery.DeferInventoryRead(expected, Environment.TickCount64);
                    _canvas.Notice = retry ? "No se pudo leer la recuperación. Se reintentará automáticamente con espera progresiva." :
                        "Se alcanzó el límite de reintentos de lectura. Conserva el diagnóstico. " + ex.Message;
                    AppLog.Write("PRODUCT RECOVERY INVENTORY READ: retry=" + retry + "; " + ex);
                    if (!retry) NotifyRecoveryFailure(_canvas.Notice);
                });
                return;
            }
            if (ProductFanRangePreview.Enabled || _closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) return;
            if (!selected.Pending)
            {
                // Another normal cleanup can resolve the startup records while
                // waiting. Still drain our owner and verify absence again before
                // opening a fresh runtime; never call the recovery writer here.
                await ForceSessionRecoveryCoreAsync(preferences, resume, selected);
                return;
            }
            if (selected.CpuSession != (expected.CpuSession ?? Guid.Empty) || selected.GpuSession != (expected.GpuSession ?? Guid.Empty) ||
                !ProductRecoveryInventory.AllowsRemaining(expected.FanRecords, selected.Fans))
                throw new IOException("Los registros cambiaron durante la espera; no se recuperó otra sesión.");
            if (selected.Fans?.Any(r => !r.Recoverable) == true)
                throw new IOException("Registro inválido o de propiedad incierta; conserva el diagnóstico.");
            await ForceSessionRecoveryCoreAsync(preferences, resume, selected);
        }
        catch (Exception ex)
        {
            // A malformed or changed exact inventory is not a transient read.
            // Retain it for diagnosis; only a different inventory can qualify anew.
            _automaticRecovery.RejectInventoryRead(expected);
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
                _automaticRecovery.AbandonInventoryRead();
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
