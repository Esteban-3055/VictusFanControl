using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private Task? _recoveryTask;
    internal Func<ProductRecoverySelection> RecoverySelectionReader { get; set; } = () => ProductRecoverySelection.Read();
    internal Func<ProductRecoverySelection, Task<ProductRecoveryResult>>? RecoveryRunner { get; set; }
    internal Func<ProductProfiles, Task<IProductRuntime>>? RecoveryRuntimeFactory { get; set; }

    internal Task RecoverPerformanceAsync(bool confirmed = false)
    {
        if (_recoveryTask is not null) return _recoveryTask;
        if (_closing || _restarting || IsDisposed || _canvas.Busy || _displayOff || _isolatedRuntime && RecoveryRunner is null) return Task.CompletedTask;
        try
        {
            var selected = RecoverySelectionReader();
            if (!selected.Pending) { _canvas.Notice = "No hay sesiones pendientes. Puedes reiniciar la sesión o preparar Automático manualmente."; _canvas.Invalidate(); return Task.CompletedTask; }
            if (!confirmed && MessageBox.Show(this, selected.Summary +
                "\n\nCierra otros controladores de CPU/GPU. Se liberará esta sesión y se guardarán respaldos de los registros. Al terminar, la aplicación permanecerá en Firmware y conservará tus perfiles y cambios sin guardar.\n\n¿Recuperar sesiones pendientes?",
                "Recuperar sesiones anteriores", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return Task.CompletedTask;
            return _recoveryTask = RecoverPerformanceCoreAsync(selected);
        }
        catch (Exception ex) { _canvas.Notice = "No se pudo leer la recuperación: " + ex.Message; _canvas.Invalidate(); return Task.CompletedTask; }
    }

    private async Task RecoverPerformanceCoreAsync(ProductRecoverySelection selected)
    {
        _restarting = true; _restartLifecycleInterrupted = false; _canvas.Busy = true; _canvas.RestartAvailable = false;
        _presentationTimer.Stop(); _startupAutomatic?.Cancel("Recuperación explícita de CPU/GPU."); _startupAutomatic = null;
        var before = _canvas.State; var preferences = Draft; var oldRuntime = _runtime as ProductRuntime;
        var controllerReleased = false;
        try
        {
            // The owner remains open only as an idle coordinator. Every live controller is drained first.
            Exception? fenceFailure = null;
            try { _runtime?.FenceLifecycle("Recuperación explícita de CPU/GPU"); } catch (Exception ex) { fenceFailure = ex; }
            DetachRuntime(); _canvas.Notice = "Liberando la sesión y preparando recuperación…"; _canvas.Invalidate();
            await Task.Yield();
            await ShutdownRuntimeAsync();
            controllerReleased = true;
            _runtime = null;
            if (fenceFailure is not null) throw new IOException("No se confirmó el bloqueo del controlador anterior.", fenceFailure);
            if (_closing || _exitRequested) return;
            if (_displayOff || _restartLifecycleInterrupted) throw new IOException("Recuperación cancelada por suspensión o pantalla apagada. No se abrió el recuperador.");
            var pending = RecoverySelectionReader();
            // A normal cleanup may have resolved these exact records already.
            if (pending.CpuSession != Guid.Empty && pending.CpuSession != selected.CpuSession ||
                pending.GpuSession != Guid.Empty && pending.GpuSession != selected.GpuSession || !ProductRecoveryInventory.AllowsRemaining(selected.Fans,pending.Fans))
                throw new IOException("Los identificadores pendientes cambiaron. No se recuperó otra sesión.");
            await RecoveryUiAsync(() => { _canvas.Notice = "Recuperando sesiones y guardando respaldos…"; _canvas.Invalidate(); });
            var recovered = pending.Pending
                ? await (RecoveryRunner?.Invoke(pending) ?? ProductRecoveryClient.RunAsync(AppContext.BaseDirectory, _modules, pending))
                : new ProductRecoveryResult(true, "", "La liberación normal resolvió los registros pendientes.");
            AppLog.Write("PRODUCT GUIDED RECOVERY: " + recovered.Detail + "; evidence=" + recovered.EvidenceDirectory);
            if (!recovered.Succeeded || RecoverySelectionReader().Pending)
                throw new IOException(recovered.Detail + "\nEvidencia: " + recovered.EvidenceDirectory);
            if (!_isolatedRuntime) ProductSessionRestart.EnsureNoRecoveryRecords();
            if (_closing || _exitRequested) return;
            if (_displayOff || _restartLifecycleInterrupted) throw new IOException("Recuperación completada; un cambio de energía impidió abrir otra sesión. Usa Reiniciar sesión cuando el equipo esté activo.");
            var next = await (RecoveryRuntimeFactory?.Invoke(preferences) ?? Task.Run<IProductRuntime>(() => new ProductRuntime(_modules, preferences, _automaticReview, releasedRuntime: oldRuntime)));
            _runtime = next; _runtimeShutdown = null; _lifecycleRelease = Task.CompletedTask; _startupFailure = null;
            controllerReleased = false;
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted)
            {
                await ShutdownRuntimeAsync();
                throw new IOException("El nuevo controlador quedó liberado por cierre o suspensión.");
            }
            await RecoveryUiAsync(() =>
            {
                AttachRuntime(next); UpdateState(next.State); next.Start(); _presentationTimer.Start();
                _canvas.RestartAvailable = true; _canvas.AutomaticRetryAvailable = _automaticReview is null && (!_isolatedRuntime || _retryRuntimeFactory is not null);
                _canvas.Notice = "Recuperación completada. Sesión nueva en Firmware; Automático requiere activación manual. " +
                    (recovered.EvidenceDirectory.Length == 0 ? "" : "Respaldos: " + recovered.EvidenceDirectory);
            });
        }
        catch (Exception ex)
        {
            DetachRuntime();
            try { await ShutdownRuntimeAsync(); controllerReleased = true; } catch (Exception release) { controllerReleased = false; AppLog.Write("Guided recovery cleanup retained: " + release); }
            _runtime = null;
            await RecoveryUiAsync(() =>
            {
                _canvas.AutomaticRetryAvailable = false; _canvas.RestartAvailable = true;
                var preview = _isolatedRuntime ? before.PerformanceRecovery : PerformanceRecoveryPreview.Read();
                _canvas.State = before with { LifecycleBlocked = true, Runtime = "Failed", Failure = "Recuperación no completada: " + ex.Message,
                    PerformanceRecovery = preview, FanMode = preview?.FanRecords?.Count > 0 ? "RecoveryRequired" : controllerReleased ? "Firmware" : before.FanMode,
                    FanAuthority = preview?.FanRecords?.Count > 0 ? "Unknown" : controllerReleased ? "Firmware" : before.FanAuthority,
                    CpuState = preview?.CpuSession.HasValue == true ? "Recovering" : controllerReleased && preview?.Pending != true ? "Disabled" : "Failed",
                    GpuState = preview?.GpuSession.HasValue == true ? "Recovering" : controllerReleased && preview?.Pending != true ? "Disabled" : "Failed",
                    GuardianState = preview?.Pending == true ? "Recuperación pendiente" : "Sin sesión",
                    CanApplyPerformance = false, PerformanceActive = false, PerformanceUpdating = false, PerformanceProcessPresent = false, AutomaticPreparing = false };
                _canvas.Notice = _canvas.State.Failure!; UpdateTray(_canvas.State);
            });
            AppLog.Write("PRODUCT GUIDED RECOVERY BLOCKED: " + ex);
        }
        finally
        {
            await RecoveryUiAsync(() => { _restarting = false; _recoveryTask = null; _canvas.Busy = _pendingCommands > 0; _canvas.Invalidate(); });
        }
    }
    private Task RecoveryUiAsync(Action action)
    {
        if (IsDisposed) return Task.CompletedTask;
        if (!InvokeRequired) { action(); return Task.CompletedTask; }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() => { try { if (!IsDisposed) action(); completed.SetResult(); } catch (Exception ex) { completed.SetException(ex); } });
        return completed.Task;
    }
}
