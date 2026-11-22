using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private sealed class ScopedRetryRuntime : IProductRuntime,IProductFanRetry
    {
        internal readonly RecordingRuntime Inner=new();
        internal readonly TaskCompletionSource RetirementGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Retirements,Verifications;
        internal bool RejectVerification;
        public event Action<ProductRuntimeState>? Changed {add=>Inner.Changed+=value;remove=>Inner.Changed-=value;}
        public ProductRuntimeState State=>Inner.State;
        public void Start()=>Inner.Start();
        public void RequestAutomaticTelemetry()=>Inner.RequestAutomaticTelemetry();
        public Task SelectFanModeAsync(AdaptiveFanProductionMode mode,ProductProfiles profiles)=>Inner.SelectFanModeAsync(mode,profiles);
        public Task ApplyFanCurveAsync(ProductPowerProfile source,UnifiedFanDemand demand)=>Inner.ApplyFanCurveAsync(source,demand);
        public Task ApplyFanTuningAsync(AdaptiveFanTuning tuning)=>Inner.ApplyFanTuningAsync(tuning);
        public Task ApplyManualAsync(int level)=>Inner.ApplyManualAsync(level);
        public Task ApplyPerformanceAsync(ProductProfiles profiles)=>Inner.ApplyPerformanceAsync(profiles);
        public Task ReleasePerformanceAsync()=>Inner.ReleasePerformanceAsync();
        public void FenceLifecycle(string reason)=>Inner.FenceLifecycle(reason);
        public Task ReleaseForLifecycleAsync(string reason)=>Inner.ReleaseForLifecycleAsync(reason);
        public void ResumeTelemetry(string reason)=>Inner.ResumeTelemetry(reason);
        public ValueTask DisposeAsync()=>Inner.DisposeAsync();
        public async Task RetireFanRuntimeAsync(){Interlocked.Increment(ref Retirements);await RetirementGate.Task;}
        public Task VerifyRetainedPerformanceAsync()
        {Interlocked.Increment(ref Verifications);return RejectVerification?Task.FromException(new IOException("Isolated ownership verification rejected.")):Task.CompletedTask;}
    }
    private static void TestScopedFanRetry(Action<bool,string> require)
    {
        {
            var retained=new ScopedRetryRuntime();
            retained.Inner.StateOverride=new(){Target="HP-8C40-9D0R1LA-F18",AutomaticAuthorized=true,LifecycleBlocked=true,
                SharedWmiRecoveryPending=true,CpuState="Active",GpuState="ActiveUnverified",PerformanceActive=true};
            using var pendingForm=new ProductForm("fixture://uncertain-shared-wmi",fixture:retained,fixtureProfiles:new());
            pendingForm.Show();Application.DoEvents();retained.Inner.Publish(retained.State);
            CompleteGuiTask(pendingForm.RetryAutomaticAsync(),"Uncertain shared WMI retry did not return.");
            require(retained.Retirements==0&&retained.Inner.Disposals==0&&pendingForm.Canvas.State.CpuState=="Active",
                "A local retry replaced uncertain WMI or globally released an unrelated CPU owner.");
            CompleteGuiTask(pendingForm.RequestExitAsync(),"Uncertain WMI fixture did not honor explicit global exit.");
        }
        foreach(var scenario in new[]{"success","factory-failure","verify-failure","suspend","exit"})
        {
            var previous=new ScopedRetryRuntime{RejectVerification=scenario=="verify-failure"};var next=new RecordingRuntime();var created=0;
            var blocked=new ProductRuntimeState{Target="HP-8C40-9D0R1LA-F18",AutomaticAuthorized=true,LifecycleBlocked=true,
                LifecycleBlockReason="Isolated fan failure",CpuState="Active",GpuState="ActiveUnverified",PerformanceActive=true,PerformanceProcessPresent=true};
            previous.Inner.StateOverride=blocked;
            using var form=new ProductForm("fixture://scoped-retry",fixture:previous,fixtureProfiles:new(),retryRuntimeFactory:_=>
            {
                created++;return scenario=="factory-failure"?Task.FromException<IProductRuntime>(new IOException("Isolated construction failure")):Task.FromResult<IProductRuntime>(next);
            });
            form.Show();Application.DoEvents();previous.Inner.Publish(blocked);
            var retry=form.RetryAutomaticAsync();
            PumpUntil(()=>previous.Retirements==1,"Scoped retry did not begin local retirement.");
            require(previous.Inner.Disposals==0&&created==0,"Fan retirement closed CPU/GPU or constructed before local drainage.");
            Task? exit=null;
            if(scenario=="suspend")CompleteGuiTask(form.HandlePowerEventAsync(4),"Suspend during scoped retry did not return.");
            if(scenario=="exit")exit=form.RequestExitAsync();
            previous.RetirementGate.SetResult();CompleteGuiTask(retry,"Scoped retry did not complete.");
            if(scenario=="success")
            {
                require(created==1&&next.Starts==1&&previous.Inner.Disposals==0&&previous.Verifications==1,
                    "Successful fan retry did not verify the retained owner or globally disposed it.");
                exit=form.RequestExitAsync();CompleteGuiTask(exit,"Scoped retry exit did not release replacement.");
                require(next.Disposals==1&&previous.Inner.Disposals==0,"Exit released the old transferred owner or skipped the replacement.");
            }
            else
            {
                if(exit is null)exit=form.RequestExitAsync();CompleteGuiTask(exit,"Cancelled scoped retry could not exit.");
                require(previous.Inner.Disposals==1&&next.Disposals==0&&created==(scenario=="factory-failure"?1:0),
                    "Failed, suspended or closing retry skipped global cleanup, released twice or opened another controller.");
            }
        }
        Console.WriteLine("PASS: actual form uses fan-only retirement, verifies ownership, preserves it on success and upgrades to global cleanup on failed construction, failed verification, suspend and Exit. No hardware IO.");
    }
    internal static int RunScopedRetryChecks()
    {
        try{TestScopedFanRetry((ok,message)=>{if(!ok)throw new InvalidOperationException(message);});return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
