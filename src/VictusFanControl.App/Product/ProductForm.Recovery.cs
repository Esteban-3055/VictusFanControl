using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private Task? _recoveryTask;
    internal bool RecoveryMonitorActive => _presentationTimer.Enabled;
    internal Func<ProductRecoverySelection> RecoverySelectionReader { get; set; } = () => ProductRecoverySelection.Read();
    internal Func<ProductRecoverySelection, Task<ProductRecoveryResult>>? RecoveryRunner { get; set; }
    internal Func<ProductProfiles, Task<IProductRuntime>>? RecoveryRuntimeFactory { get; set; }

    // Kept as an internal compatibility entry point: all manual recovery uses
    // the same diagnostic, exact-inventory and live-session drain pipeline.
    internal Task RecoverPerformanceAsync(bool confirmed = false) => ForceSessionRecoveryAsync(confirmed);

    private async Task<bool> RecoverPerformanceCoreAsync(ProductRecoverySelection selected, ProductProfiles? automaticPreferences = null, bool resume = false, bool reopenRuntime = true, bool forceSession = false)
    {
        if (selected.Pending) _automaticRecovery.RecordAttempt(new(true, "", selected.CpuSession == Guid.Empty ? null : selected.CpuSession,
            selected.GpuSession == Guid.Empty ? null : selected.GpuSession, selected.Fans));
        _restarting = true; _restartLifecycleInterrupted = false; _canvas.Busy = true; _canvas.RestartAvailable = false;
        _presentationTimer.Stop(); _startupAutomatic?.Cancel("Recuperación explícita de CPU/GPU."); _startupAutomatic = null;
        var unattended = automaticPreferences is not null;
        var before = _canvas.State; var preferences = automaticPreferences ?? Draft;
        var oldRuntime = (_runtime ?? _shutdownRuntimeOwner) as ProductRuntime;
        IProductRecoveryHandoff? handoff = null;
        var controllerReleased = false;
        try
        {
            // The owner remains open only as an idle coordinator. Every live controller is drained first.
            Exception? fenceFailure = null;
            try { _runtime?.FenceLifecycle("Recuperación explícita de CPU/GPU"); } catch (Exception ex) { fenceFailure = ex; }
            DetachRuntime(); _canvas.Notice = "Liberando la sesión y preparando recuperación…"; _canvas.Invalidate();
            await Task.Yield();
            try { await ShutdownRuntimeAsync(); }
            catch when (_shutdownRuntimeOwner is IProductRecoveryHandoff drained &&
                drained.DrainedGpuSession is { } session && session != Guid.Empty && session == selected.GpuSession)
            {
                handoff = (IProductRecoveryHandoff)_shutdownRuntimeOwner;
                AppLog.Write("PRODUCT RECOVERY HANDOFF: prior owner exited; local resources drained; GPU release unconfirmed; exact journal recovery required.");
            }
            controllerReleased = true;
            _runtime = null;
            if (fenceFailure is not null) throw new IOException("No se confirmó el bloqueo del controlador anterior.", fenceFailure);
            if (_closing || _exitRequested) return false;
            if (_displayOff || _restartLifecycleInterrupted) throw new IOException("Recuperación cancelada por suspensión o pantalla apagada. No se abrió el recuperador.");
            var pending = await Task.Run(RecoverySelectionReader);
            if (handoff is not null && (pending.GpuSession != selected.GpuSession || pending.CpuSession != Guid.Empty))
                throw new IOException("Falta el journal GPU esperado o hay otra sesión CPU pendiente; no se confirmó la liberación.");
            // A normal cleanup may have resolved these exact records already.
            if (pending.CpuSession != Guid.Empty && pending.CpuSession != selected.CpuSession ||
                pending.GpuSession != Guid.Empty && pending.GpuSession != selected.GpuSession || !ProductRecoveryInventory.AllowsRemaining(selected.Fans,pending.Fans))
                throw new IOException("Los identificadores pendientes cambiaron. No se recuperó otra sesión.");
            await RecoveryUiAsync(() => { _canvas.Notice = "Recuperando sesiones y guardando respaldos…"; _canvas.Invalidate(); });
            var recovered = pending.Pending
                ? await Task.Run(() => RecoveryRunner?.Invoke(pending) ?? ProductRecoveryClient.RunAsync(AppContext.BaseDirectory, _modules, pending))
                : new ProductRecoveryResult(true, "", "La liberación normal resolvió los registros pendientes.");
            AppLog.Write("PRODUCT GUIDED RECOVERY: " + recovered.Detail + "; evidence=" + recovered.EvidenceDirectory);
            if (!recovered.Succeeded || (await Task.Run(RecoverySelectionReader)).Pending)
                throw new IOException(recovered.Detail + "\nEvidencia: " + recovered.EvidenceDirectory);
            if (!_isolatedRuntime) await Task.Run(ProductSessionRestart.EnsureNoRecoveryRecords);
            // No reset is inferred from process exit, missing records or the
            // cached shutdown task. Only the verified recovery result permits
            // the old runtime's released-boundary claim to be completed.
            handoff?.CompleteRecovery(selected.GpuSession);
            _runtimeShutdown = Task.CompletedTask;
            if (_closing || _exitRequested) return false;
            if (_displayOff || _restartLifecycleInterrupted) throw new IOException("Recuperación completada; un cambio de energía impidió abrir otra sesión. Usa Reiniciar sesión cuando el equipo esté activo.");
            if (!reopenRuntime)
            {
                // The user already requested installation. Do not acquire new
                // telemetry or reopen a controller between recovery and handoff.
                _runtimeShutdown = Task.CompletedTask; _lifecycleRelease = Task.CompletedTask;
                _startupFailure = null; _recoveryAtStartup = false;
                _canvas.State = new() { Hardware = before.Hardware, Target = before.Target,
                    Runtime = "Stopped", FanMode = "Firmware", FanAuthority = "Firmware",
                    PerformanceRecovery = new(false, "Recuperación completada antes de instalar."),
                    GuardianState = "Sin sesión" };
                _canvas.Notice = "Recuperación completada. Continuando la actualización…";
                return true;
            }
            if (_isolatedRuntime && RecoveryRuntimeFactory is null) throw new IOException("El fixture aislado no tiene fábrica de runtime; no se abrirá hardware.");
            var next = await (RecoveryRuntimeFactory?.Invoke(preferences) ?? Task.Run<IProductRuntime>(() => new ProductRuntime(_modules, preferences, _automaticReview, releasedRuntime: oldRuntime)));
            _runtime = next; _runtimeShutdown = null; _shutdownRuntimeOwner = null; _lifecycleRelease = Task.CompletedTask; _startupFailure = null;
            controllerReleased = false;
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted)
            {
                await ShutdownRuntimeAsync();
                throw new IOException("El nuevo controlador quedó liberado por cierre o suspensión.");
            }
            await RecoveryUiAsync(() =>
            {
                _recoveryAtStartup = false;
                if (unattended && resume && !_recoveryResumeCancelled && Draft.Protections.ResumeAutomatic)
                {
                    _startupPreferences = ProductProfilesStore.Copy(preferences);
                    _startupRetryAt = 0; _startupAttempts = 0;
                    _startupAutomatic = new(Environment.TickCount64, protections:preferences.Protections, resumption:true);
                }
                _automaticRecovery.SessionOpened();
                if(_requirePowerNotifications)RegisterPowerNotifications();
                AttachRuntime(next); UpdateState(next.State); next.Start(); _presentationTimer.Start();
                _canvas.RestartAvailable = true; _canvas.AutomaticRetryAvailable = _automaticReview is null && (!_isolatedRuntime || _retryRuntimeFactory is not null);
                _canvas.Notice = (_startupAutomatic is not null ? "Recuperación completada. Esperando sensores nuevos para reanudar Automático. " : "Recuperación completada. Sesión nueva en Firmware. ") +
                    (recovered.EvidenceDirectory.Length == 0 ? "" : "Respaldos: " + recovered.EvidenceDirectory);
            });
            return true;
        }
        catch (Exception ex)
        {
            DetachRuntime();
            try { await ShutdownRuntimeAsync(); controllerReleased = true; } catch (Exception release) { controllerReleased = false; AppLog.Write("Guided recovery cleanup retained: " + release); }
            _runtime = null;
            var recoveryPreview = _isolatedRuntime ? before.PerformanceRecovery : await Task.Run(() => PerformanceRecoveryPreview.Read(ignoreCurrentOwner: !forceSession));
            await RecoveryUiAsync(() =>
            {
                if (unattended) { _automaticResumption.Cancel(); NotifyRecoveryFailure("No se pudo recuperar la sesión. " + ex.Message); }
                _canvas.AutomaticRetryAvailable = false; _canvas.RestartAvailable = true;
                var preview = recoveryPreview;
                _canvas.State = before with { LifecycleBlocked = true, Runtime = "Failed", Failure = "Recuperación no completada: " + ex.Message,
                    PerformanceRecovery = preview, FanMode = preview?.FanRecords?.Count > 0 ? "RecoveryRequired" : controllerReleased ? "Firmware" : forceSession ? "RecoveryRequired" : before.FanMode,
                    FanAuthority = preview?.FanRecords?.Count > 0 ? "Unknown" : controllerReleased ? "Firmware" : forceSession ? "Unknown" : before.FanAuthority,
                    CpuState = preview?.CpuSession.HasValue == true ? "Recovering" : controllerReleased && preview?.Pending != true ? "Disabled" : "Failed",
                    GpuState = preview?.GpuSession.HasValue == true ? "Recovering" : controllerReleased && preview?.Pending != true ? "Disabled" : "Failed",
                    GuardianState = preview?.Pending == true ? "Recuperación pendiente" : "Sin sesión",
                    CanApplyPerformance = false, PerformanceActive = false, PerformanceUpdating = false, PerformanceProcessPresent = false, AutomaticPreparing = false };
                _canvas.Notice = _canvas.State.Failure!; UpdateTray(_canvas.State);
            });
            AppLog.Write("PRODUCT GUIDED RECOVERY BLOCKED: " + ex);
            return false;
        }
        finally
        {
            await RecoveryUiAsync(() =>
            {
                _restarting = false; _recoveryTask = null; _canvas.Busy = _pendingCommands > 0;
                // A manual failure must not stop background monitoring forever.
                if (!_closing && !_exitRequested) _presentationTimer.Start();
                _canvas.Invalidate();
            });
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
