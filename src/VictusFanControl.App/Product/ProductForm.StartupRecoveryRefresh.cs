namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly Func<PerformanceRecoveryPreview>? _startupRecoveryPreviewReader;
    private Task? _startupInventoryRefresh;
    private int _startupInventoryReads;
    private long? _startupInventorySince;
    private bool _startupInventoryAbsenceConfirmed;
    internal bool StartupInventoryRefreshPending => _startupInventoryRefresh is {IsCompleted:false};
    private bool TryRefreshStartupInventory(long clock)
    {
        if (!_recoveryAtStartup || _runtime is not null || _shutdownRuntimeOwner is not null ||
            _startupRecoveryPreviewReader is null || _canvas.State.PerformanceRecovery is not {Pending:true,Actionable:false} expected)
            return false;
        if (StartupInventoryRefreshPending || _startupInventoryReads >= 3) return true;
        if (_startupInventorySince is null || clock < _startupInventorySince)
        { _startupInventorySince=clock;return true; }
        if (clock-_startupInventorySince < new[]{10000,30000,60000}[_startupInventoryReads])return true;
        _startupInventoryReads++;_startupInventorySince=clock;
        _startupInventoryRefresh=RefreshStartupInventoryAsync(expected,_updateLifecycleBoundary);
        return true;
    }
    private async Task RefreshStartupInventoryAsync(PerformanceRecoveryPreview expected,long powerBoundary)
    {
        await Task.Yield();
        try
        {
            // No controller exists on this path. This is discovery only, never
            // a recovery write, deletion, fabricated ID or release receipt.
            var current=await Task.Run(_startupRecoveryPreviewReader!);
            if (_closing||IsDisposed||_restarting||_displayOff||_runtime is not null||
                powerBoundary!=_updateLifecycleBoundary||!ReferenceEquals(_canvas.State.PerformanceRecovery,expected))return;
            _canvas.State=_canvas.State with{PerformanceRecovery=current,Failure=current.Pending?current.Detail:_canvas.State.Failure,
                CpuState=current.CpuSession.HasValue?"Recovering":"Disabled",GpuState=current.GpuSession.HasValue?"Recovering":"Disabled"};
            _startupInventoryAbsenceConfirmed=!current.Pending;
            _canvas.Notice=current.Pending?current.Detail:"Los registros ya no están pendientes. Verificando el cierre antes de abrir una sesión nueva…";
            _canvas.Invalidate();
        }
        catch(Exception ex)
        {
            AppLog.Write("Product read-only startup inventory refresh failed; no controller opened: "+ex);
            if(!_closing&&!IsDisposed)_canvas.Notice="No se pudo volver a leer la recuperación; se conserva el bloqueo. "+ex.Message;
        }
    }
}
