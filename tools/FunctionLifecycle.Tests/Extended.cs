using System.Reflection;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;

static class ExtendedProbe
{
    static readonly List<object> Results=[];
    static ProductProfiles Independent()=>new(){CpuEnabled=false,GpuEnabled=false,Functions=new ProductFunctionPolicies().Unlinked()};
    static object? Field(object obj,string name)=>obj.GetType().GetField(name,R.Flags)!.GetValue(obj);
    static void SetField(object obj,string name,object? value)=>obj.GetType().GetField(name,R.Flags)!.SetValue(obj,value);
    static object? Static(string type,string method,params object?[] args)
    {try{return R.T(type).GetMethod(method,R.Flags)!.Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException!;}}
    static async Task Case(string name,Func<Task<object>> action)
    {try{var details=await action();Results.Add(new{name,completed=true,details});Console.WriteLine(name+": "+JsonSerializer.Serialize(details));}
     catch(Exception e){Results.Add(new{name,completed=false,error=e.ToString()});Console.WriteLine(name+": HARNESS ERROR "+e);}}
    static async Task Main()
    {
        Console.WriteLine("App MVID "+R.App.ManifestModule.ModuleVersionId);
        foreach(var cpu in new[]{true,false})
        foreach(var expired in new[]{false,true})
        await Case("F04-pre-resume-snapshot-"+(cpu?"CPU":"GPU")+"-expired-"+expired,async()=>
        {
            var p=Independent();await using var f=new Fixture(p);f.Power("Unknown");await f.Domain(cpu,true,p);
            var before=R.Get(f.State,"Snapshot");
            R.Call(f.Runtime,"FenceLifecycle","Synthetic brief display-off boundary");await R.Invoke(f.Runtime,"ReleaseForLifecycleAsync","Synthetic boundary release");
            f.Power("Ac");R.Call(f.Runtime,"ResumeTelemetry","Synthetic display-on");
            // RecoverAndValidateAsync performs this transition before its settle
            // delay and before its first post-boundary read. Do not publish a
            // replacement snapshot; test the original pre-boundary sample.
            R.Call(R.Get(f.Worker,"StateMachine")!,"Transition",SystemState.Recovering,"Synthetic recovery before first read");
            if(expired)await Task.Delay(3100);
            await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
            var applied=(PerformanceGuiSessionConfiguration?)R.Get(f.Client,"AppliedConfiguration");
            return new{ExpiredByAge=expired,WorkerPhase=R.Get(R.Get(f.Worker,"StateMachine")!,"State")?.ToString(),
                SamePreBoundarySnapshot=ReferenceEquals(before,R.Get(f.State,"Snapshot")),PostResumeAcquisitions=0,
                Active=cpu?applied?.CpuEnabled==true:applied?.GpuEnabled==true,
                Scope="Actual boundary/reconciliation/guardian fixture, using the worker's production pre-read state transition. No real suspend or sensor acquisition."};
        });
        foreach(var cpu in new[]{true,false})
        foreach(var reconcileEarly in new[]{false,true})
        await Case("F01-pending-"+(cpu?"CPU":"GPU")+"-resume-early-"+reconcileEarly,async()=>
        {
            var p=Independent();await using var f=new Fixture(p);
            f.Power("Unknown");await f.Domain(cpu,true,p);
            R.Call(f.Runtime,"FenceLifecycle","Synthetic suspension");await R.Invoke(f.Runtime,"ReleaseForLifecycleAsync","Synthetic release");
            f.Power("Ac");R.Call(f.Runtime,"ResumeTelemetry","Synthetic resume");f.Publish(Fixture.Sample());
            var phase=R.Get(R.Get(f.Worker,"StateMachine")!,"State");string? earlyError=null;
            if(reconcileEarly)try{await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");}catch(Exception e){earlyError=e.Message;}
            R.Call(R.Get(f.Worker,"StateMachine")!,"Transition",SystemState.Healthy,"Synthetic qualification complete");f.Publish(Fixture.Sample());
            await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
            var applied=(PerformanceGuiSessionConfiguration?)R.Get(f.Client,"AppliedConfiguration");
            return new{ResumePhase=phase?.ToString(),EarlyError=earlyError,Fault=((ProductFunctionFlags)Field(f.Runtime,"_functionFaults")!).Get(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks),
                ActiveAfterHealthy=cpu?applied?.CpuEnabled==true:applied?.GpuEnabled==true,Requests=R.Get(f.State,"FunctionRequests")};
        });
        await Case("F02-cancelled-shutdown-independent-requests",async()=>
        {
            var p=Independent();await using var f=new Fixture(p);
            var cooling=R.New("ProductShutdownCooling",f.Runtime,(Func<long>)(()=>0),(Func<bool>)(()=>true));R.Call(cooling,"Start");
            string? preparationFailure=null;try{await (Task)R.Get(cooling,"Preparation")!;}catch(Exception e){preparationFailure=e.Message;}
            R.Call(cooling,"Cancel");R.Call(f.Runtime,"FenceLifecycle","Synthetic shutdown cancellation");
            await (Task)Static("ProductForm","ReleaseCancelledCoolingAsync",f.Runtime,cooling,Task.CompletedTask)!;
            R.Call(R.Get(f.Worker,"StateMachine")!,"Transition",SystemState.Healthy,"Synthetic post-cancel healthy");f.Publish(Fixture.Sample());
            string? requestFailure=null;try{await f.Domain(true,true,p);}catch(Exception e){requestFailure=e.Message;}
            var beforeResume=new{Maintenance=Field(f.Runtime,"_maintenanceActive"),PerformanceBlocked=R.Get(f.State,"PerformanceLifecycleBlocked"),RequestFailure=requestFailure};
            R.Call(f.Runtime,"ResumeTelemetry","Synthetic later power notification");R.Call(R.Get(f.Worker,"StateMachine")!,"Transition",SystemState.Healthy,"Synthetic ready again");f.Publish(Fixture.Sample());
            await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
            return new{Attempted=R.Get(cooling,"Attempted"),PreparationFailure=preparationFailure,BeforeLaterPowerNotification=beforeResume,
                MaintenanceAfterLaterResume=Field(f.Runtime,"_maintenanceActive"),CpuActiveAfterReconcile=R.Get(f.Client,"LimitsActive"),
                Scope="Actual static cancellation cleanup, no Form instance or Windows shutdown message. Fake backend rejects manual authority; cancellation still follows its production release path."};
        });
        foreach(var coupled in new[]{false,true})
        await Case("F03-independent-owner-failed-fan-preparation-coupled-"+coupled,async()=>
        {
            var p=Independent();p=p with{Functions=p.Functions! with{StopAutomaticOnPerformanceFailure=coupled}};
            await using var fixture=(IAsyncDisposable)R.New(R.T("ProductRuntimePerformanceSelfTest").GetNestedType("RepairFixture",R.Flags)!,p,false);
            var runtime=Field(fixture,"Runtime")!;var client=Field(fixture,"Client")!;
            await R.Invoke(runtime,"SetPerformanceDomainAsync",true,true,p);
            var state=R.Get(runtime,"State")!;SetField(fixture,"RejectRetainedJournals",true);R.Call(runtime,"FenceFanRetry","Synthetic fan-only failure");
            var rejected=false;try{await R.Invoke(runtime,"RetireFanRuntimeAsync");}catch(IOException){rejected=true;}
            SetField(fixture,"RejectRetainedJournals",false);var closed=0;
            Func<Task> shutdown=async()=>{closed++;await ((IAsyncDisposable)runtime).DisposeAsync();};
            var kept=await (Task<bool>)Static("ProductAutomaticRecovery","CleanupFailedFanPreparationAsync",state,true,true,false,false,shutdown)!;
            return new{CoupledFailureOption=coupled,Binding="Independent",FanPreparationRejected=rejected,OwnerKept=kept,GlobalCloses=closed,
                CpuOwnerPresent=(bool)R.Get(client,"HasProcess")!,Scope="Same shared coordinator used by the Form catch. Full Form recovery lifecycle not run."};
        });
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"extended-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"extended-results","results.json"),JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=Results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
}
