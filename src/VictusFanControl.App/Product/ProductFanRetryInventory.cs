using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal static class ProductFanRetryInventory
{
    // A shared live guardian is not an orphan. Admit only the exact record
    // proven by the retained client's process handle and released fan domain.
    // Re-read the proof after enumeration to detect retirement/replacement.
    internal static void EnsureClear(Func<ProductRecoveryRecord?> retainedLease,string? commonRoot=null,string? localRoot=null)
    {
        var before=retainedLease();
        var records=ProductRecoveryInventory.ReadFans(commonRoot,localRoot,ignoreCurrentOwner:false);
        if(before is not null&&records.Count(record=>record==before)!=1||
            records.Any(record=>record.Kind!="FanGui"||record.Problem is not null||record!=before)||before!=retainedLease())
            throw new IOException("Hay sesiones de ventiladores ajenas o sin confirmar; se conserva el bloqueo de recuperación.");
    }
}

internal sealed partial class ProductRuntime
{
    public void EnsureFanRetryInventoryClear()
    {
        void Verify(){lock(_disposalSync)EnsureRetainedPerformance();}
        Verify();
        ProductFanRetryInventory.EnsureClear(()=>_wmiLifetime?.Client.RetainedFanLease());
        Verify();
    }
}
