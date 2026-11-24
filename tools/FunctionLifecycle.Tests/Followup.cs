using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Product;

static class FollowupProbe
{
    static readonly List<object> Results=[];
    static ProductProfiles Independent()=>new(){CpuEnabled=false,GpuEnabled=false,Functions=new ProductFunctionPolicies().Unlinked()};
    static object? Field(object obj,string name)=>obj.GetType().GetField(name,R.Flags)!.GetValue(obj);
    static async Task Case(string name,Func<Task<object>> action)
    {
        try{var details=await action();Results.Add(new{name,completed=true,details});Console.WriteLine(name+": "+JsonSerializer.Serialize(details));}
        catch(Exception e){Results.Add(new{name,completed=false,error=e.ToString()});Console.WriteLine(name+": HARNESS ERROR "+e);}
    }
    static async Task Main()
    {
        Console.WriteLine("App MVID "+R.App.ManifestModule.ModuleVersionId);
        foreach(var cpu in new[]{true,false})
        foreach(var automaticAttempt in new[]{false,true})
        await Case("E01-independent-pending-"+(cpu?"CPU":"GPU")+"-failed-auto-"+automaticAttempt,async()=>
        {
            var p=Independent();await using var f=new Fixture(p);
            var requested=p with{Ac=p.Ac with{CpuPl1Watts=28,CpuPl2Watts=38,GpuMaximumMHz=1100}};
            f.Power("Unknown");await f.Domain(cpu,true,requested);
            var before=(ProductProfiles)Field(f.Runtime,"_latestFunctionProfiles")!;
            string? refusal=null;
            if(automaticAttempt)try{await f.Auto(p);}catch(Exception e){refusal=e.Message;}
            var after=(ProductProfiles)Field(f.Runtime,"_latestFunctionProfiles")!;
            var faults=(ProductFunctionFlags)Field(f.Runtime,"_functionFaults")!;
            f.Power("Ac");f.Publish(Fixture.Sample());
            await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
            var applied=(PerformanceGuiSessionConfiguration?)R.Get(f.Client,"AppliedConfiguration");
            return new{AutomaticAttempt=automaticAttempt,Refusal=refusal,Expected=cpu?28:1100,
                StoredBefore=cpu?before.Ac.CpuPl1Watts:before.Ac.GpuMaximumMHz,StoredAfter=cpu?after.Ac.CpuPl1Watts:after.Ac.GpuMaximumMHz,
                DomainLatchedFault=faults.Get(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks),
                ActiveAfterSourceReturns=cpu?applied?.CpuEnabled==true:applied?.GpuEnabled==true,
                AppliedValue=cpu?applied?.AcPl1Watts:applied?.AcGpuMaximumMHz,
                Requests=R.Get(f.State,"FunctionRequests"),FanMode=R.Get(f.State,"FanMode")};
        });
        foreach(var cpu in new[]{true,false})
        await Case("E02-independent-pending-"+(cpu?"CPU":"GPU")+"-successful-auto",async()=>
        {
            var p=Independent();await using var f=new Fixture(p);
            var requested=p with{Ac=p.Ac with{CpuPl1Watts=28,CpuPl2Watts=38,GpuMaximumMHz=1100}};
            f.Power("Unknown");await f.Domain(cpu,true,requested);
            f.Power("Ac");f.Publish(Fixture.Sample());await f.Auto(p);
            await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
            var applied=(PerformanceGuiSessionConfiguration?)R.Get(f.Client,"AppliedConfiguration");
            return new{Expected=cpu?28:1100,Actual=cpu?applied?.AcPl1Watts:applied?.AcGpuMaximumMHz,
                Active=cpu?applied?.CpuEnabled==true:applied?.GpuEnabled==true,FanMode=R.Get(f.State,"FanMode"),
                Scope="No selected linked shortcuts; only the fan mode was requested after the pending independent domain."};
        });
        foreach(var cpu in new[]{true,false})
        foreach(var reason in new[]{ProductFunctionEntryReason.User,ProductFunctionEntryReason.AutomaticRecovery})
        await Case("E03-failed-independent-"+(cpu?"CPU":"GPU")+"-fan-entry-"+reason,async()=>
        {
            var p=Independent();
            await using var fixture=(IAsyncDisposable)R.New(R.T("ProductRuntimePerformanceSelfTest").GetNestedType("RepairFixture",R.Flags)!,p,false);
            var runtime=Field(fixture,"Runtime")!;var client=Field(fixture,"Client")!;
            R.SetField(fixture,"RefuseLaunch",true);
            var rejected=false;try{await R.Invoke(runtime,"SetPerformanceDomainAsync",cpu,true,p);}catch(IOException){rejected=true;}
            R.SetField(fixture,"RefuseLaunch",false);
            var faultBefore=((ProductFunctionFlags)Field(runtime,"_functionFaults")!).Get(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks);
            await R.Invoke(runtime,"SelectFanModeAsync",AdaptiveFanProductionMode.Automatic,p,reason);
            await R.Invoke(runtime,"ReconcileFunctionsCoreAsync");
            return new{FixtureLaunchRefused=rejected,FaultBefore=faultBefore,EntryReason=reason.ToString(),
                FaultAfter=((ProductFunctionFlags)Field(runtime,"_functionFaults")!).Get(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks),
                LaunchAttempts=Field(fixture,"Launches"),Active=(bool)R.Get(client,"LimitsActive")!,
                ExplicitPerDomainRetry=false,Binding="Independent"};
        });
        foreach(var cpu in new[]{true,false})
        await Case("E04-independent-pending-"+(cpu?"CPU":"GPU")+"-cancelled-auto",async()=>
        {
            var p=Independent();await using var f=new Fixture(p);f.Power("Unknown");await f.Domain(cpu,true,p);f.Power("Ac");f.Publish(Fixture.Sample());
            var gate=(SemaphoreSlim)Field(f.Runtime,"_commands")!;await gate.WaitAsync();
            Task attempt;
            try{attempt=f.Auto(p);await R.Invoke(f.Runtime,"SelectFanModeAsync",AdaptiveFanProductionMode.Firmware,p);}
            finally{gate.Release();}
            string? refused=null;try{await attempt;}catch(Exception e){refused=e.Message;}
            await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
            return new{Cancellation=refused,Requested=R.Get(f.State,"FunctionRequests"),
                Fault=((ProductFunctionFlags)Field(f.Runtime,"_functionFaults")!).Get(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks),
                AnyPerformanceChild=(bool)R.Get(f.Client,"HasProcess")!,FanMode=R.Get(f.State,"FanMode")};
        });
        await Case("E05-profile-input-mutations",()=>Task.FromResult(ProfileMutations()));
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"followup-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"followup-results","results.json"),JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=Results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
    static object ProfileMutations()
    {
        var baseline=JsonNode.Parse(ProductProfilesStore.Serialize(Independent()))!;
        var paths=new List<string[]>();
        void Walk(JsonNode? node,List<string> path)
        {
            if(path.Count>0)paths.Add(path.ToArray());
            if(node is JsonObject obj)foreach(var entry in obj)Walk(entry.Value,[..path,entry.Key]);
            else if(node is JsonArray array)for(var i=0;i<array.Count;i++)Walk(array[i],[..path,i.ToString()]);
        }
        Walk(baseline,[]);
        var folder=Path.Combine(AppContext.BaseDirectory,"profile-mutations");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"input.json");
        var accepted=0;var rejected=0;var altered=0;var unexpected=new List<object>();
        foreach(var path in paths)
        foreach(var mutation in new[]{"null","{}","[]","\"wrong-type\""})
        {
            var root=baseline.DeepClone();var parent=root;
            foreach(var step in path[..^1])parent=parent is JsonArray array?array[int.Parse(step)]!:parent[step]!;
            if(parent is JsonArray target)target[int.Parse(path[^1])]=JsonNode.Parse(mutation);else parent[path[^1]]=JsonNode.Parse(mutation);
            var text=root.ToJsonString();File.WriteAllText(file,text);
            try
            {
                _=ProductProfilesStore.Load(file,out var notice);
                if(notice is null)accepted++;else rejected++;
                if(File.ReadAllText(file)!=text)altered++;
            }
            catch(Exception e)
            {unexpected.Add(new{Path=string.Join('.',path),Mutation=mutation,Error=e.ToString()});File.WriteAllText(Path.Combine(folder,$"unexpected-{unexpected.Count}.json"),text);}
        }
        return new{Cases=paths.Count*4,Accepted=accepted,GracefullyRejected=rejected,OriginalFilesAltered=altered,UnexpectedErrors=unexpected};
    }
}
