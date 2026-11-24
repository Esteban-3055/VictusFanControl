using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Product;

static class LinkedProbe
{
    static async Task Main()
    {
        var results=new List<object>();
        foreach(var cpu in new[]{true,false})
        foreach(var binding in new[]{ProductFunctionBinding.StartWithInternalFans,ProductFunctionBinding.FollowInternalFans})
        foreach(var mode in new[]{AdaptiveFanProductionMode.Manual,AdaptiveFanProductionMode.Automatic})
        {
            var name="H02-linked-"+(cpu?"CPU":"GPU")+"-"+binding+"-"+mode;
            try
            {
                var function=cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks;
                var rules=new ProductFunctionPolicies().Unlinked();
                var original=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,Functions=rules.With(function,new(binding,true))};
                var edited=original with{Ac=original.Ac with{CpuPl1Watts=28,CpuPl2Watts=38,GpuMaximumMHz=1100}};
                await using var f=new Fixture(original);
                await R.Invoke(f.Runtime,"SelectFanModeAsync",mode,edited);await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
                var applied=(PerformanceGuiSessionConfiguration?)R.Get(f.Client,"AppliedConfiguration");
                var details=new{Binding=binding.ToString(),Mode=mode.ToString(),Expected=cpu?28:1100,
                    Actual=cpu?applied?.AcPl1Watts:applied?.AcGpuMaximumMHz,Active=cpu?applied?.CpuEnabled==true:applied?.GpuEnabled==true,
                    Requested=((ProductFunctionFlags)R.Get(f.State,"FunctionRequests")!).Get(function),Scope="Explicit linked mode entry with edited values; actual runtime and simulated guardian. No Form or hardware IO."};
                results.Add(new{name,completed=true,details});Console.WriteLine(name+": "+JsonSerializer.Serialize(details));
            }
            catch(Exception e){results.Add(new{name,completed=false,error=e.ToString()});Console.WriteLine(name+": HARNESS ERROR "+e);}
        }
        foreach(var cpu in new[]{true,false})
        foreach(var mode in new[]{AdaptiveFanProductionMode.Manual,AdaptiveFanProductionMode.Automatic})
        {
            var name="H03-linked-fault-retry-"+(cpu?"CPU":"GPU")+"-"+mode;
            try
            {
                var function=cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks;
                var rules=new ProductFunctionPolicies().Unlinked();
                var p=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,Functions=rules.With(function,new(ProductFunctionBinding.StartWithInternalFans,true))};
                await using var fixture=(IAsyncDisposable)R.New(R.T("ProductRuntimePerformanceSelfTest").GetNestedType("RepairFixture",R.Flags)!,p,false);
                object? Field(string key)=>fixture.GetType().GetField(key,R.Flags)!.GetValue(fixture);
                void Set(string key,object? value)=>fixture.GetType().GetField(key,R.Flags)!.SetValue(fixture,value);
                var runtime=Field("Runtime")!;var client=Field("Client")!;Set("RefuseLaunch",true);
                try{await R.Invoke(runtime,"SetPerformanceDomainAsync",cpu,true,p);}catch(IOException){}
                Set("RefuseLaunch",false);
                await R.Invoke(runtime,"SelectFanModeAsync",mode,p);await R.Invoke(runtime,"ReconcileFunctionsCoreAsync");
                var state=R.Get(runtime,"State")!;
                var fault=((ProductFunctionFlags)R.Get(state,"FunctionFaults")!).Get(function);
                var active=(bool)R.Get(client,"LimitsActive")!;
                await R.Invoke(runtime,"SetPerformanceDomainAsync",cpu,true,p);
                var details=new{Mode=mode.ToString(),LinkedSelection=true,FaultAfterLinkedEntry=fault,ActiveAfterLinkedEntry=active,
                    ExplicitDomainRetryWorks=(bool)R.Get(client,"LimitsActive")!,Scope="Selected linked-start retry semantics differ by mode. Record as H02 policy consistency extension, not a separate unrelated bug."};
                results.Add(new{name,completed=true,details});Console.WriteLine(name+": "+JsonSerializer.Serialize(details));
            }
            catch(Exception e){results.Add(new{name,completed=false,error=e.ToString()});Console.WriteLine(name+": HARNESS ERROR "+e);}
        }
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"linked-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"linked-results","results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
}
