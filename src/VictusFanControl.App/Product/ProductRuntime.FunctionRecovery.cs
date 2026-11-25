using VictusFanControl.Product;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal sealed partial class ProductRuntime
{
    private ProductFunctionRetry[] _functionRetries=Enumerable.Range(0,4).Select(_=>new ProductFunctionRetry()).ToArray();
    internal Func<long> FunctionRecoveryClock {get;set;}=()=>Environment.TickCount64;
    private ProductFunctionRetry Retry(ProductFunction function)=>_functionRetries[(int)function];
    private void RecordFunctionFailure(ProductFunction function,Exception error)
    {
        _functionFaults=_functionFaults.With(function,true);
        Retry(function).Failed(string.IsNullOrWhiteSpace(error.Message)?error.GetType().Name:error.Message,FunctionRecoveryClock());
        var detail=error.ToString();if(detail.Length>12000)detail=detail[..12000];
        AppLog.WriteStructured("PRODUCT FUNCTION FAILURE",new{function,exceptionType=error.GetType().FullName,error.HResult,error.Message,detail,retry=Retry(function).State});
    }
    private void ObservePerformanceFunctionFailure(ProductFunction function,bool applied)
    {
        if(!applied||!_functionRequests.Get(function)||_functionFaults.Get(function)||_performanceUpdating||!_performance.LastStatusFresh)return;
        var status=function==ProductFunction.CpuLimits?_performance.LastStatus?.CpuState:_performance.LastStatus?.GpuState;
        if(status is not("Failed" or "Faulted" or "RecoveryRequired" or "Contested" or "Yielded"))return;
        var detail=function==ProductFunction.CpuLimits?_performance.LastStatus?.CpuStatus:_performance.LastStatus?.GpuStatus;
        RecordFunctionFailure(function,new IOException(detail??"El dominio perdió su confirmación."));
        if(status is "RecoveryRequired" or "Contested" or "Yielded")
            Retry(function).WaitForConfirmation("El controlador de esta función requiere revisión: "+detail);
    }
    private void RecordRequestedPerformanceFailures(PerformanceGuiSessionConfiguration? before,PerformanceGuiSessionConfiguration requested,Exception error)
    {
        var changed=ProductPerformanceUpdate.ChangedActiveDomains(before,requested);
        var actual=_performance.AppliedConfiguration;var status=_performance.LastStatusFresh?_performance.LastStatus:null;
        var bound=status is not null&&actual is not null&&status.Configuration==actual;
        bool CpuConfirmed()=>bound&&actual!.CpuEnabled&&status!.CpuState=="Active"&&
            actual.AcPl1Watts==requested.AcPl1Watts&&actual.AcPl2Watts==requested.AcPl2Watts&&
            actual.BatteryPl1Watts==requested.BatteryPl1Watts&&actual.BatteryPl2Watts==requested.BatteryPl2Watts;
        bool GpuConfirmed()=>bound&&actual!.GpuEnabled&&status!.GpuState=="ActiveUnverified"&&
            actual.AcGpuMaximumMHz==requested.AcGpuMaximumMHz&&actual.BatteryGpuMaximumMHz==requested.BatteryGpuMaximumMHz;
        if(requested.CpuEnabled&&(changed.Cpu||!bound)&&!CpuConfirmed())RecordFunctionFailure(ProductFunction.CpuLimits,error);
        if(requested.GpuEnabled&&(changed.Gpu||!bound)&&!GpuConfirmed())RecordFunctionFailure(ProductFunction.GpuClocks,error);
    }
    private async Task TryRecoverPerformanceFunctionAsync(bool cpu)
    {
        var function=cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks;
        var retry=Retry(function);
        if(!_functionRequests.Get(function)){retry.Cancel();return;}
        var values=_latestFunctionProfiles.PerformanceConfiguration();
        var candidate=_performance.AppliedConfiguration??values with{CpuEnabled=false,GpuEnabled=false};
        candidate=cpu?candidate with{CpuEnabled=true,AcPl1Watts=values.AcPl1Watts,AcPl2Watts=values.AcPl2Watts,
            BatteryPl1Watts=values.BatteryPl1Watts,BatteryPl2Watts=values.BatteryPl2Watts}:
            candidate with{GpuEnabled=true,AcGpuMaximumMHz=values.AcGpuMaximumMHz,BatteryGpuMaximumMHz=values.BatteryGpuMaximumMHz};
        var ready=FollowsCurrentMode(function)&&PerformancePhaseReady(_performance.AppliedConfiguration,candidate)&&
            _powerSource.Read().Source is PerformancePowerSourceKind.Ac or PerformancePowerSourceKind.Battery&&
            ProductPerformanceUpdate.HasIndependentTelemetry(_snapshot,DateTimeOffset.UtcNow,_performance.AppliedConfiguration,candidate);
        // No process is not proof that old native ownership was withdrawn.
        try
        {
            if(!_performance.HasProcess&&_performanceRecoveryInventory().Pending)return;
            if(_performance.HasProcess&&(!_performance.LastStatusFresh||_performance.LastStatus is not{} status||
                status.Configuration!=_performance.AppliedConfiguration||!PerformanceRetainedSession.MatchesJournals(status,_retainedJournalReader())))return;
        }
        catch(Exception error){retry.WaitForConfirmation("Los registros de esta función requieren revisión: "+error.Message);return;}
        if(retry.State.NeedsConfirmation&&_performance.LastStatus is{} verified&&
            (cpu?verified.CpuState=="Disabled"&&verified.CpuSession is null:verified.GpuState=="Disabled"&&verified.GpuSession is null))
            retry.ConfirmedFailure(retry.State.Failure??"Retirada confirmada; esperando reintento local.",FunctionRecoveryClock());
        if(!retry.TryBegin(FunctionRecoveryClock(),ready))return;
        var releaseConfirmed=!_performance.HasProcess;
        try
        {
            if(_performance.HasProcess)
            {
                var selected=cpu?_performance.AppliedConfiguration?.CpuEnabled==true:_performance.AppliedConfiguration?.GpuEnabled==true;
                if(selected)await ReleasePerformanceDomainsAsync(cpu,!cpu,clearRequest:false);
                await _performance.VerifyRetainedSessionAsync(_retainedJournalReader);
                releaseConfirmed=true;
                var peer=_performance.AppliedConfiguration??values with{CpuEnabled=false,GpuEnabled=false};
                candidate=cpu?peer with{CpuEnabled=true,AcPl1Watts=values.AcPl1Watts,AcPl2Watts=values.AcPl2Watts,
                    BatteryPl1Watts=values.BatteryPl1Watts,BatteryPl2Watts=values.BatteryPl2Watts}:
                    peer with{GpuEnabled=true,AcGpuMaximumMHz=values.AcGpuMaximumMHz,BatteryGpuMaximumMHz=values.BatteryGpuMaximumMHz};
            }
            await ApplyPerformanceConfigurationAsync(candidate);
            _functionFaults=_functionFaults.With(function,false);retry.Succeeded(FunctionRecoveryClock());
            Publish("Función recuperada; los demás controles conservan su sesión.");
        }
        catch(Exception error)
        {
            RecordFunctionFailure(function,error);
            if(!releaseConfirmed)retry.WaitForConfirmation("La retirada del dominio requiere confirmación: "+error.Message);
            Publish("La función afectada espera su propia recuperación.",error.Message);
        }
    }
}
