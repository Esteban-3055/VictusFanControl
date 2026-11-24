using System.Reflection;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;

static class EdgeProbe
{
    static readonly List<object> Results=[];
    static ProductProfiles Independent()=>new(){CpuEnabled=false,GpuEnabled=false,Functions=new ProductFunctionPolicies().Unlinked()};
    static object? Field(object obj,string name)=>obj.GetType().GetField(name,R.Flags)!.GetValue(obj);
    static async Task Case(string name,Func<Task<object>> action)
    {try{var details=await action();Results.Add(new{name,completed=true,details});Console.WriteLine(name+": "+JsonSerializer.Serialize(details));}
     catch(Exception e){Results.Add(new{name,completed=false,error=e.ToString()});Console.WriteLine(name+": HARNESS ERROR "+e);}}
    static async Task Main()
    {
        foreach(var binding in Enum.GetValues<ProductFunctionBinding>())
        foreach(var mode in new[]{AdaptiveFanProductionMode.Manual,AdaptiveFanProductionMode.Automatic})
        await Case("G04-ctgp-presentation-"+binding+"-"+mode,async()=>
        {
            var p=Independent();p=p with{Functions=p.Functions! with{Ctgp=new(binding,true)}};
            await using var f=new Fixture(p,withWmi:true);
            await R.Invoke(f.Runtime,"SelectFanModeAsync",mode,p);await R.Invoke(f.Runtime,"SetCtgpAsync",true);
            var shown=(string)R.Get(f.State,"CtgpState")!;
            var text=(string)R.T("ProductCanvas").GetMethod("FunctionWaitText",R.Flags)!.Invoke(null,[f.State,ProductFunction.Ctgp,shown])!;
            return new{Binding=binding.ToString(),Mode=mode.ToString(),Requested=((ProductFunctionFlags)R.Get(f.State,"FunctionRequests")!).Ctgp,
                ActualManaged=R.Get(f.Raw!,"Ctgp") is{} observation?R.Get(observation,"Managed"):null,
                ActualActive=R.Get(f.Raw!,"Ctgp") is{} status?R.Get(status,"Active"):null,PresentedState=shown,FunctionsText=text};
        });
        await Case("G03-fan-retirement-cancels-ctgp-before-start",async()=>
        {
            var p=Independent();
            await using var fixture=(IAsyncDisposable)R.New(R.T("ProductRuntimePerformanceSelfTest").GetNestedType("RepairFixture",R.Flags)!,p,true);
            var runtime=Field(fixture,"Runtime")!;var shared=Field(fixture,"Shared")!;var raw=Field(fixture,"Raw")!;var client=Field(fixture,"Client")!;
            await R.Invoke(runtime,"SetPerformanceDomainAsync",true,true,p);
            var startup=(SemaphoreSlim)Field(shared,"_startup")!;await startup.WaitAsync();
            var ctgp=R.Invoke(runtime,"SetCtgpAsync",true);string? cancellation=null;string? replacementFailure=null;
            try
            {
                using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while(!(bool)R.Get(shared,"CtgpWanted")!)await Task.Delay(10,wait.Token);
                R.Call(runtime,"FenceFanRetry","Synthetic fan retry during initial cTGP preparation");
                await R.Invoke(runtime,"RetireFanRuntimeAsync");
                try{await ctgp;}catch(OperationCanceledException e){cancellation=e.Message;}
            }
            finally{startup.Release();}
            var rawStarted=(bool)R.Get(raw,"HasChild")!;var faulted=R.Get(shared,"CtgpFaulted");
            await R.Invoke(shared,"SetCtgpAsync",false,false,CancellationToken.None);
            try{R.Call(fixture,"Create",p,runtime);}catch(IOException e){replacementFailure=e.Message;}
            return new{CtgpCancellation=cancellation,WmiChildWasStarted=rawStarted,CtgpFaulted=faulted,
                FaultAfterOff=R.Get(shared,"CtgpFaulted"),ReplacementFailure=replacementFailure,CpuStillActive=R.Get(client,"LimitsActive"),
                Scope="Actual runtime retirement cancellation, exact retained CPU owner and actual replacement constructor; no Form or hardware acquisition."};
        });
        foreach(var cancelBeforeEntering in new[]{true,false})
        await Case("G01-ctgp-pre-start-cancel-before-command-"+cancelBeforeEntering,async()=>
        {
            await using var f=new Fixture(Independent(),withWmi:true);
            var shared=f.Shared!;var gate=(SemaphoreSlim)Field(shared,cancelBeforeEntering?"_commands":"_startup")!;
            await gate.WaitAsync();using var cancel=new CancellationTokenSource();
            Task request;
            try{request=R.Invoke(shared,"SetCtgpAsync",true,true,cancel.Token);cancel.Cancel();try{await request;}catch(OperationCanceledException){}}
            finally{gate.Release();}
            var childAtCancellation=(bool)R.Get(f.Raw!,"HasChild")!;
            var faultAtCancellation=R.Get(shared,"CtgpFaulted");
            await R.Invoke(shared,"SetCtgpAsync",false,false,CancellationToken.None);
            var faultAfterOff=R.Get(shared,"CtgpFaulted");string? retryFailure=null;
            try{await R.Invoke(shared,"SetCtgpAsync",true,true,CancellationToken.None);}catch(IOException e){retryFailure=e.Message;}
            return new{CancelledBeforeCommandEntry=cancelBeforeEntering,ChildAtCancellation=childAtCancellation,
                FaultAtCancellation=faultAtCancellation,FaultAfterOff=faultAfterOff,RetryFailure=retryFailure,ChildAfterExplicitRetry=R.Get(f.Raw!,"HasChild"),
                Scope="Cancellation before any cTGP child/native write. Shared WMI lifetime is real; cancellation is injected at its startup semaphore."};
        });
        foreach(var unsupportedCtgp in new[]{false,true})
        foreach(var automatic in new[]{false,true})
        await Case("G02-save-unrelated-rules-ctgp-requested-"+unsupportedCtgp+"-automatic-"+automatic,async()=>
        {
            var p=Independent();await using var f=new Fixture(p);
            if(automatic){await f.Auto(p);f.Publish(Fixture.Sample() with{CpuTemperatureC=null,CpuCoreTemperatures=[]});}
            await f.Domain(true,true,p);
            if(automatic)f.Publish(Fixture.Sample());
            if(unsupportedCtgp)try{await R.Invoke(f.Runtime,"SetCtgpAsync",true);}catch(NotSupportedException){}
            await R.Invoke(f.Runtime,"SetExternalCoolingRequestedAsync",true);
            var folder=Path.Combine(AppContext.BaseDirectory,"edge-files",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            var file=Path.Combine(folder,"profiles.json");ProductProfilesStore.Save(p,file);var before=File.ReadAllText(file);
            var proposed=p.Functions! with{ExternalOutputMode=ProductExternalOutputMode.Fixed,ExternalFixedOutput=87};
            Func<ProductFunctionPolicies,Task> apply=rules=>R.Invoke(f.Runtime,"ConfigureFunctionsAsync",rules);
            Action<ProductProfiles> save=profiles=>ProductProfilesStore.Save(profiles,file);string? failure=null;
            try
            {
                await (Task<ProductProfiles>)R.T("ProductFunctionRuleEditing").GetMethod("ApplyAndSaveAsync",R.Flags)!.Invoke(null,[p,proposed,apply,save])!;
            }
            catch(Exception e){failure=e.Message;}
            var memory=(ProductFunctionPolicies)R.Get(f.State,"FunctionPolicies")!;
            var applied=R.Get(f.Client,"AppliedConfiguration");
            return new{UnsupportedCtgpRequested=unsupportedCtgp,Automatic=automatic,AutomaticTracksActual=!automatic||Equals(Field(f.Runtime,"_automaticPerformance"),applied),Failure=failure,RuntimeCommittedNewRules=memory==proposed,
                SavedNewRules=ProductProfilesStore.Load(file,out _).Functions==proposed,OriginalFileUnchanged=File.ReadAllText(file)==before,
                CurrentExternalOutput=memory.ExternalFixedOutput,CpuStillActive=R.Get(f.Client,"LimitsActive"),
                Scope="Real apply/save helper and runtime coordinator, actual temporary file, no external device or Form."};
        });
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"edge-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"edge-results","results.json"),JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=Results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
}
