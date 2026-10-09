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
    private async Task CheckUpdateAsync()
    {
        if (_checkingUpdate || _closing || _restarting || _isolatedRuntime) return;
        _checkingUpdate = true;
        try
        {
            _canvas.Notice = "Consultando releases estables de GitHub…"; _canvas.Invalidate();
            var update = await ProductUpdates.CheckAsync(Version.Parse(ProductRelease.Version), _updateCancellation.Token);
            if (_closing || IsDisposed) return;
            if (update is null) { _canvas.Notice = "Ya tienes la versión estable más reciente (" + ProductRelease.Version + ")."; return; }
            if (_canvas.Dirty) { _canvas.Notice = $"Disponible v{update.Version}. Guarda o descarta el borrador antes de actualizar."; return; }
            if (MessageBox.Show(this, $"Disponible v{update.Version}.\n\nSe descargará y verificará el instalador. Después se liberarán ventiladores y CPU/GPU y se cerrará esta sesión. Tus preferencias se conservan.\n\n¿Descargar y abrir el instalador?", "Actualizar VictusFanControl", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            _canvas.Notice = "Descargando y verificando el instalador…"; _canvas.Invalidate();
            var path = await ProductUpdates.DownloadAsync(update, _updateCancellation.Token);
            if (_closing || IsDisposed) return;
            // Downloading leaves the controls available; recheck after awaited work.
            if (_canvas.Dirty || _canvas.Busy) { _canvas.Notice = "Descarga verificada. Guarda el borrador y espera a que termine la operación antes de actualizar."; return; }
            _updateInstallTask = InstallUpdateAsync(path);
            await _updateInstallTask;
        }
        catch (OperationCanceledException) { if (!_closing && !IsDisposed) _canvas.Notice = "La consulta o descarga se canceló o agotó su tiempo."; }
        catch (Exception ex) { if (!_closing && !IsDisposed) _canvas.Notice = "No se pudo actualizar: " + ex.Message; AppLog.Write("PRODUCT UPDATE: " + ex); }
        finally { _checkingUpdate = false; if (!IsDisposed) _canvas.Invalidate(); }
    }
    private async Task InstallUpdateAsync(string installer, Action? ensureReleased = null, Action? launchFixture = null)
    {
        _restarting = true; _canvas.RestartAvailable = false; _canvas.Busy = true;
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
            if (_runtime is not null) _runtime.Changed -= UpdateState;
            _runtime = null;
            _canvas.State = _canvas.State with { LifecycleBlocked = true, Runtime = "Failed", CanApplyPerformance = false, Failure = "Sesión cerrada para actualizar. Sal desde la bandeja y vuelve a abrir el programa si cancelaste la instalación." };
            _canvas.Busy = false;
            throw;
        }
        finally { _restarting = false; }
    }
}
