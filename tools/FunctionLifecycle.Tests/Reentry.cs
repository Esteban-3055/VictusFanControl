using System.Text.Json;
using VictusFanControl.Performance;
using VictusFanControl.Product;

static class ReentryProbe
{
    static readonly List<object> Results=[];
    static object? Call(string method,params object?[] args)=>R.T("ProductSessionRestart").GetMethod(method,R.Flags)!.Invoke(null,args);
    static async Task Main()
    {
        foreach(var cpu in new[]{true,false})
        foreach(var savedOptin in new[]{false,true})
        foreach(var useDraft in new[]{false,true})
        {
            var name="H01-restart-"+(cpu?"CPU":"GPU")+"-saved-optin-"+savedOptin+"-use-draft-"+useDraft;
            try
            {
                var function=cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks;
                var policies=new ProductFunctionPolicies().Unlinked();
                var saved=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,
                    Functions=policies.With(function,policies.Get(function) with{StartWithApplication=savedOptin})};
                var draft=saved with{Functions=saved.Functions!.With(function,saved.Functions!.Get(function) with{StartWithApplication=true}),
                    Ac=saved.Ac with{CpuPl1Watts=28,CpuPl2Watts=38,GpuMaximumMHz=1100}};
                var state=Call("Capture",draft,saved,true,true,Enum.Parse(R.T("ProductPage"),"Functions"),ProductPowerProfile.Ac)!;
                var sessions=Path.Combine(AppContext.BaseDirectory,"restart-files",Guid.NewGuid().ToString("N"));
                var path=Path.Combine(sessions,(string)R.Get(state,"PreviousSessionId")!,"restart-state.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Call("WriteReleasedState",path,state);var restored=Call("ReadReleasedState",path,sessions)!;
                var restoredDraft=ProductProfilesStore.Parse((string)R.Get(restored,"DraftJson")!);
                var restoredSaved=ProductProfilesStore.Parse((string)R.Get(restored,"SavedJson")!);
                // ProductForm.InitializeAsync selects Draft even when restartState
                // is present. Only ActivateAutomaticOnStart is suppressed there.
                var runtimeProfiles=useDraft?(ProductProfiles)Call("RuntimeProfiles",restoredDraft,restoredSaved,true)!:restoredSaved;
                await using var f=new Fixture(runtimeProfiles);
                if(useDraft)Call("PrepareRuntime",f.Runtime,true);
                await R.Invoke(f.Runtime,"ReconcileFunctionsCoreAsync");
                var applied=(PerformanceGuiSessionConfiguration?)R.Get(f.Client,"AppliedConfiguration");
                var details=new{SavedStartupOptin=savedOptin,UseDraftAtConstruction=useDraft,
                    SavedValue=cpu?saved.Ac.CpuPl1Watts:saved.Ac.GpuMaximumMHz,DraftValue=cpu?draft.Ac.CpuPl1Watts:draft.Ac.GpuMaximumMHz,
                    ActualValue=cpu?applied?.AcPl1Watts:applied?.AcGpuMaximumMHz,
                    Active=cpu?applied?.CpuEnabled==true:applied?.GpuEnabled==true,FanMode=R.Get(f.State,"FanMode"),
                    DirtyPreserved=R.Get(restored,"Dirty"),Source="Actual capture/write/read receipt and runtime constructor/reconciliation. Full Form/process relaunch reviewed in source, not executed."};
                Results.Add(new{name,completed=true,details});Console.WriteLine(name+": "+JsonSerializer.Serialize(details));
            }
            catch(Exception e){Results.Add(new{name,completed=false,error=e.ToString()});Console.WriteLine(name+": HARNESS ERROR "+e);}
        }
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"reentry-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"reentry-results","results.json"),JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=Results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
}
