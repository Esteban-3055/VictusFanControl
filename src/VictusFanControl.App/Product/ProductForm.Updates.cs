using System.Diagnostics;
using System.Security.Principal;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _checkingUpdate;
    private bool _updateCancellationDisposed;
    private Task? _updateInstallTask;
    private long _updateLifecycleBoundary;
    private void ResetUpdateSelection()
    {
        _canvas.AvailableUpdate = null; _canvas.UpdateCheckedAt = null; _canvas.UpdateProgressPercent = null;
        _canvas.UpdateStatus = "Canal cambiado. Guarda la preferencia y vuelve a buscar actualizaciones. El instalador descargado se conserva.";
    }
    private void DisposeUpdateCancellation()
    {
        if (_updateCancellationDisposed) return;
        _updateCancellationDisposed = true; _updateCancellation.Cancel(); _updateCancellation.Dispose();
    }
    internal Task InstallUpdateFixtureAsync(Action ensureReleased, Action launch)
    {
        if (!_isolatedRuntime) throw new InvalidOperationException("Update fixture requires an isolated runtime.");
        return _updateInstallTask ??= InstallUpdateAsync("fixture://installer", ensureReleased, launch);
    }
    internal async Task CheckUpdateAsync(Func<CancellationToken,Task<ProductUpdate?>>? checkFixture = null)
    {
        if (_checkingUpdate || _closing || _restarting || _canvas.Busy || (_isolatedRuntime && checkFixture is null)) return;
        if (!_isolatedRuntime && checkFixture is not null) throw new InvalidOperationException("Update fixture requires an isolated runtime.");
        _checkingUpdate = _canvas.UpdateBusy = true;
        var includePrereleases = _draft.IncludePrereleaseUpdates;
        _canvas.AvailableUpdate = null; _canvas.UpdateProgressPercent = null;
        _canvas.UpdateStatus = includePrereleases ? "Consultando releases estables y preliminares de GitHub…" : "Consultando releases estables de GitHub…"; _canvas.Invalidate();
        try
        {
            var update = await (checkFixture?.Invoke(_updateCancellation.Token) ?? ProductUpdates.CheckAsync(ProductRelease.NumericVersion, _updateCancellation.Token, includePrereleases));
            if (_closing || IsDisposed) return;
            if (includePrereleases != _draft.IncludePrereleaseUpdates) { ResetUpdateSelection(); return; }
            _canvas.AvailableUpdate = update; _canvas.UpdateCheckedAt = DateTimeOffset.Now;
            _canvas.UpdateStatus = update is null ? (includePrereleases ? "No hay una versión estable o preliminar más reciente que la instalada." : "No hay una versión estable más reciente que la instalada.") :
                $"Nueva versión {(update.IsPrerelease ? "preliminar" : "estable")} disponible. Instalador: {update.Size / (1024d * 1024):0.0} MB. Pulsa Descargar; después podrás instalar sin volver a descargar.";
        }
        catch (OperationCanceledException) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "La consulta se canceló o agotó su tiempo. Puedes volver a comprobar."; }
        catch (Exception ex) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "No se pudo comprobar: " + ex.Message; AppLog.Write("PRODUCT UPDATE CHECK: " + ex); }
        finally { _checkingUpdate = false; if (!IsDisposed) { _canvas.UpdateBusy = false; _canvas.Invalidate(); } }
    }
    private async Task RestoreDownloadedUpdateAsync()
    {
        if (_isolatedRuntime || _checkingUpdate || _closing || IsDisposed) return;
        _checkingUpdate = _canvas.UpdateBusy = true;
        var previews = _draft.IncludePrereleaseUpdates;
        try
        {
            var cached = await ProductUpdates.FindCachedAsync(ProductRelease.NumericVersion, previews, _updateCancellation.Token);
            if (!_closing && !IsDisposed && previews == _draft.IncludePrereleaseUpdates && cached is not null)
            {
                _canvas.DownloadedUpdate = cached; _canvas.UpdateProgressPercent = 100;
                _canvas.UpdateStatus = $"v{cached.Version} descargada y verificada. Pulsa Instalar cuando estés listo.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("PRODUCT UPDATE CACHE: " + ex); }
        finally { _checkingUpdate = false; if (!IsDisposed) { _canvas.UpdateBusy = false; _canvas.Invalidate(); } }
    }
    internal async Task DownloadUpdateAsync(Func<ProductUpdate,CancellationToken,Task<string>>? downloadFixture = null)
    {
        if (_checkingUpdate || _closing || _restarting || _canvas.Busy || _canvas.AvailableUpdate is not { } update || (_isolatedRuntime && downloadFixture is null)) return;
        if (update.IsPrerelease && !_draft.IncludePrereleaseUpdates) { ResetUpdateSelection(); return; }
        if (!_isolatedRuntime && downloadFixture is not null) throw new InvalidOperationException("Update fixture requires an isolated runtime.");
        _checkingUpdate = _canvas.UpdateBusy = true;
        _canvas.UpdateProgressPercent = 0; _canvas.UpdateStatus = "Descargando y verificando el instalador…"; _canvas.Invalidate();
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                if (_closing || IsDisposed || !_canvas.UpdateBusy) return;
                _canvas.UpdateProgressPercent = (int)Math.Clamp(bytes * 100 / update.Size, 0, 100); _canvas.Invalidate();
            });
            await (downloadFixture?.Invoke(update,_updateCancellation.Token) ?? ProductUpdates.DownloadAsync(update, _updateCancellation.Token, progress));
            if (_closing || IsDisposed) return;
            _canvas.DownloadedUpdate = update; _canvas.UpdateProgressPercent = 100;
            _canvas.UpdateStatus = $"v{update.Version} descargada y verificada. Pulsa Instalar; los errores de instalación no borrarán la descarga.";
        }
        catch (OperationCanceledException) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "La descarga se canceló o agotó su tiempo. Puedes volver a intentarlo."; }
        catch (Exception ex) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "No se pudo descargar: " + ex.Message; AppLog.Write("PRODUCT UPDATE DOWNLOAD: " + ex); }
        finally { _checkingUpdate = false; if (!IsDisposed) { _canvas.UpdateBusy = false; _canvas.Invalidate(); } }
    }
    internal async Task InstallDownloadedUpdateAsync(Action? ensureReleasedFixture = null, Action? launchFixture = null, Func<CancellationToken,Task>? verifyFixture = null)
    {
        if (_checkingUpdate || _closing || _restarting || _displayOff || _canvas.Busy || _canvas.Dirty || _canvas.DownloadedUpdate is not { } update || (_isolatedRuntime && verifyFixture is null)) return;
        if (!ProductUpdates.IsNewer(update.Version,update.IsPrerelease,ProductRelease.NumericVersion)) { _canvas.DownloadedUpdate = null; return; }
        if (update.IsPrerelease && !_draft.IncludePrereleaseUpdates) { ResetUpdateSelection(); return; }
        if (!_isolatedRuntime && (verifyFixture is not null || ensureReleasedFixture is not null || launchFixture is not null)) throw new InvalidOperationException("Update fixture requires an isolated runtime.");
        if (!_isolatedRuntime && MessageBox.Show(this, $"Se instalará v{update.Version}{(update.IsPrerelease ? " (preliminar)" : "")} desde la descarga guardada. Se verificará su SHA-256 y se liberarán ventiladores y CPU/GPU antes de cerrar. Si está habilitada la recuperación automática, se resolverán las sesiones pendientes antes de continuar. Tus preferencias se conservan.\n\n¿Abrir el instalador?", "Instalar actualización", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        _checkingUpdate = _canvas.UpdateBusy = true;
        var boundary = _updateLifecycleBoundary;
        FileStream? verified = null;
        try
        {
            _canvas.UpdateStatus = "Comprobando la release de GitHub y el instalador guardado…"; _canvas.Invalidate();
            if (_isolatedRuntime) await verifyFixture!(_updateCancellation.Token);
            else
            {
                update = await ProductUpdates.ConfirmPublishedInstallerAsync(update, ProductRelease.NumericVersion, _draft.IncludePrereleaseUpdates, _updateCancellation.Token);
                verified = await ProductUpdates.OpenVerifiedInstallerAsync(update, _updateCancellation.Token);
                _canvas.DownloadedUpdate = update;
            }
            if (_closing || IsDisposed) return;
            if (_canvas.Dirty || update.IsPrerelease && !_draft.IncludePrereleaseUpdates || _canvas.Busy || _restarting || _displayOff || boundary != _updateLifecycleBoundary)
            { _canvas.UpdateStatus = "El instalador se conserva. Guarda los cambios y resuelve la interrupción antes de volver a pulsar Instalar."; return; }
            if (_runtime is null || _canvas.State.PerformanceRecovery?.Pending == true)
            {
                var pending = await Task.Run(RecoverySelectionReader);
                if (_closing || IsDisposed) return;
                if (_displayOff || _canvas.Dirty || _canvas.Busy || _restarting || boundary != _updateLifecycleBoundary) return;
                if (pending.Pending)
                {
                    if (!Draft.RecoverSessionsAutomatically)
                    { _canvas.UpdateStatus = "La recuperación automática está desactivada. Usa Recuperar sesiones y luego Instalar. La descarga se conserva."; return; }
                    if (pending.Fans?.Any(r => !r.Recoverable) == true)
                    { _canvas.UpdateStatus = "Registro de propiedad inválida o incierta. La actualización se conserva y no se modifica el hardware."; return; }
                    _canvas.UpdateStatus = "Recuperando la sesión pendiente para continuar la instalación…"; _canvas.Invalidate();
                    var recovery = RecoverPerformanceCoreAsync(pending, ProductProfilesStore.Copy(Draft), resume: false, reopenRuntime: false);
                    _recoveryTask = recovery;
                    if (!await recovery)
                    { if (!_closing && !IsDisposed) _canvas.UpdateStatus = (_canvas.State.Failure ?? "No se confirmó la recuperación.") + " La descarga se conserva."; return; }
                    if (_closing || IsDisposed) return;
                    if (_exitRequested || _displayOff || boundary != _updateLifecycleBoundary)
                    { _canvas.UpdateStatus = "Sesión recuperada. La instalación se detuvo por cierre o cambio de energía; la descarga se conserva."; return; }
                }
            }
            _canvas.UpdateStatus = "Instalador verificado. Liberando la sesión para instalar…";
            _updateInstallTask = InstallUpdateAsync(verified?.Name ?? "fixture://installer",ensureReleasedFixture,launchFixture);
            await _updateInstallTask;
        }
        catch (InvalidDataException ex)
        { if (!_closing && !IsDisposed) { _canvas.DownloadedUpdate = null; _canvas.UpdateStatus = ex.Message; } AppLog.Write("PRODUCT UPDATE INTEGRITY: " + ex); }
        catch (FileNotFoundException)
        { if (!_closing && !IsDisposed) { _canvas.DownloadedUpdate = null; _canvas.UpdateStatus = "El instalador guardado ya no está disponible. Vuelve a descargarlo."; } }
        catch (OperationCanceledException) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "Instalación cancelada. La descarga se conserva; puedes volver a pulsar Instalar."; }
        catch (Exception ex) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "No se pudo instalar: " + ex.Message + " La descarga se conserva; resuelve el problema y pulsa Instalar."; AppLog.Write("PRODUCT UPDATE INSTALL: " + ex); }
        finally { if (verified is not null) await verified.DisposeAsync(); _updateInstallTask = null; _checkingUpdate = false; if (!IsDisposed) { _canvas.UpdateBusy = false; _canvas.Invalidate(); } }
    }
    private async Task InstallUpdateAsync(string installer, Action? ensureReleased = null, Action? launchFixture = null)
    {
        _restarting = true; _restartLifecycleInterrupted = false; _canvas.RestartAvailable = false; _canvas.Busy = true;
        _presentationTimer.Stop(); _startupAutomatic?.Cancel("Actualización solicitada.");
        var releaseCompleted = false; var recordsCleared = false;
        try
        {
            Exception? fenceFailure = null;
            try { _runtime?.FenceLifecycle("Actualización de producto"); } catch (Exception ex) { fenceFailure = ex; }
            _canvas.Notice = "Liberando la sesión antes de actualizar…"; _canvas.Invalidate();
            await ShutdownRuntimeAsync(); releaseCompleted = true;
            if (fenceFailure is not null) throw new IOException("No se confirmó el bloqueo del controlador anterior.", fenceFailure);
            if (_isolatedRuntime) (ensureReleased ?? throw new InvalidOperationException("Fixture release fence missing."))();
            else ProductSessionRestart.EnsureNoRecoveryRecords();
            recordsCleared = true;
            if (_closing || _exitRequested || _displayOff || _restartLifecycleInterrupted) throw new IOException("Actualización cancelada por cierre o cambio de energía. La sesión quedó liberada.");
            if (_isolatedRuntime) (launchFixture ?? throw new InvalidOperationException("Fixture launcher missing."))();
            else
            {
            using var owner = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(installer)! };
            // Numeric identity only; installer waits for this exact process to finish normally.
            using var identity = WindowsIdentity.GetCurrent();
            start.Arguments = $"--wait-pid {owner.Id} --wait-start {owner.StartTime.ToUniversalTime().Ticks} --owner-sid {identity.User!.Value}";
            using var child = Process.Start(start) ?? throw new IOException("No se abrió el instalador.");
            }
            _disposedRuntime = true; _exitRequested = true; Close(); _shutdown.TrySetResult();
        }
        catch (Exception ex)
        {
            DetachRuntime();
            _runtime = null;
            var released = releaseCompleted && recordsCleared;
            var preview = _canvas.State.PerformanceRecovery;
            if (!_isolatedRuntime) try { preview = PerformanceRecoveryPreview.Read(); } catch (Exception read) { AppLog.Write("PRODUCT UPDATE RECOVERY PREVIEW: " + read); }
            _canvas.State = _canvas.State with { LifecycleBlocked = true, Runtime = "Failed", CanApplyPerformance = false,
                PerformanceRecovery = preview, PerformanceActive = false, PerformanceProcessPresent = false, PerformanceUpdating = false, AutomaticPreparing = false,
                CpuState = released ? "Disabled" : "Recovering", GpuState = released ? "Disabled" : "Recovering",
                GuardianState = released ? "Sin sesión" : "Liberación no confirmada", FanMode = released ? "Firmware" : "RecoveryRequired", FanAuthority = released ? "Firmware" : "Unknown",
                Failure = "Actualización detenida: " + ex.Message + " El instalador descargado se conserva." };
            UpdateTray(_canvas.State);
            _canvas.RestartAvailable = true; _canvas.Busy = false;
            throw;
        }
        finally
        {
            _restarting = false;
            if (!_closing && !_exitRequested && !IsDisposed) _presentationTimer.Start();
        }
    }
}
