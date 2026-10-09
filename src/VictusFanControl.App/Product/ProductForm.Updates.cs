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
        _canvas.AvailableUpdate = null; _canvas.UpdateProgressPercent = null;
        _canvas.UpdateStatus = "Consultando releases estables de GitHub…"; _canvas.Invalidate();
        try
        {
            var update = await (checkFixture?.Invoke(_updateCancellation.Token) ?? ProductUpdates.CheckAsync(Version.Parse(ProductRelease.Version), _updateCancellation.Token));
            if (_closing || IsDisposed) return;
            _canvas.AvailableUpdate = update; _canvas.UpdateCheckedAt = DateTimeOffset.Now;
            _canvas.UpdateStatus = update is null ? "No hay una versión estable más reciente que la instalada." :
                $"Nueva versión disponible. Instalador: {update.Size / (1024d * 1024):0.0} MB. Pulsa Descargar e instalar para actualizar.";
        }
        catch (OperationCanceledException) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "La consulta se canceló o agotó su tiempo. Puedes volver a comprobar."; }
        catch (Exception ex) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "No se pudo comprobar: " + ex.Message; AppLog.Write("PRODUCT UPDATE CHECK: " + ex); }
        finally { _checkingUpdate = false; if (!IsDisposed) { _canvas.UpdateBusy = false; _canvas.Invalidate(); } }
    }
    internal async Task DownloadAndInstallUpdateAsync(Func<ProductUpdate,CancellationToken,Task<string>>? downloadFixture = null, Action? ensureReleasedFixture = null, Action? launchFixture = null)
    {
        if (_checkingUpdate || _closing || _restarting || _canvas.Busy || _canvas.Dirty || _canvas.AvailableUpdate is not { } update || (_isolatedRuntime && downloadFixture is null)) return;
        if (!_isolatedRuntime && downloadFixture is not null) throw new InvalidOperationException("Update fixture requires an isolated runtime.");
        if (!_isolatedRuntime && MessageBox.Show(this, $"Se descargará v{update.Version} y se verificará su SHA-256. Después se liberarán ventiladores y CPU/GPU y se cerrará esta sesión. Tus preferencias se conservan.\n\n¿Descargar y abrir el instalador?", "Actualizar VictusFanControl", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        _checkingUpdate = _canvas.UpdateBusy = true;
        var lifecycleBoundary = _updateLifecycleBoundary;
        _canvas.UpdateProgressPercent = 0;
        _canvas.UpdateStatus = "Descargando y verificando el instalador…"; _canvas.Invalidate();
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                if (_closing || IsDisposed || !_canvas.UpdateBusy) return;
                _canvas.UpdateProgressPercent = (int)Math.Clamp(bytes * 100 / update.Size, 0, 100);
                _canvas.Invalidate();
            });
            var path = await (downloadFixture?.Invoke(update,_updateCancellation.Token) ?? ProductUpdates.DownloadAsync(update, _updateCancellation.Token, progress));
            if (_closing || IsDisposed) return;
            // Navigation/editing and power events remain available while downloading.
            if (_canvas.Dirty || _canvas.Busy || _restarting || _displayOff || lifecycleBoundary != _updateLifecycleBoundary)
            { _canvas.UpdateStatus = "Descarga verificada. Guarda los cambios y resuelve la interrupción antes de instalar; después vuelve a pulsar Descargar e instalar."; return; }
            _canvas.UpdateProgressPercent = 100;
            _canvas.UpdateStatus = "Descarga verificada. Liberando la sesión para instalar…";
            _updateInstallTask = InstallUpdateAsync(path,ensureReleasedFixture,launchFixture);
            await _updateInstallTask;
        }
        catch (OperationCanceledException) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "La descarga se canceló o agotó su tiempo. Puedes volver a intentarlo."; }
        catch (Exception ex) { if (!_closing && !IsDisposed) _canvas.UpdateStatus = "No se pudo instalar: " + ex.Message; AppLog.Write("PRODUCT UPDATE INSTALL: " + ex); }
        finally { _checkingUpdate = false; if (!IsDisposed) { _canvas.UpdateBusy = false; _canvas.Invalidate(); } }
    }
    private async Task InstallUpdateAsync(string installer, Action? ensureReleased = null, Action? launchFixture = null)
    {
        _restarting = true; _restartLifecycleInterrupted = false; _canvas.RestartAvailable = false; _canvas.Busy = true;
        _presentationTimer.Stop(); _startupAutomatic?.Cancel("Actualización solicitada.");
        try
        {
            _runtime?.FenceLifecycle("Actualización de producto");
            _canvas.Notice = "Liberando la sesión antes de actualizar…"; _canvas.Invalidate();
            await ShutdownRuntimeAsync();
            if (_isolatedRuntime) (ensureReleased ?? throw new InvalidOperationException("Fixture release fence missing."))();
            else ProductSessionRestart.EnsureNoRecoveryRecords();
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
        catch
        {
            DetachRuntime();
            _runtime = null;
            _canvas.State = _canvas.State with { LifecycleBlocked = true, Runtime = "Failed", CanApplyPerformance = false, Failure = "Sesión cerrada para actualizar. Sal desde la bandeja y vuelve a abrir el programa si cancelaste la instalación." };
            UpdateTray(_canvas.State);
            _canvas.Busy = false;
            throw;
        }
        finally { _restarting = false; }
    }
}
