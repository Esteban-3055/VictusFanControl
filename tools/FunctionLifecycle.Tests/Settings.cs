using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using VictusFanControl.ExternalCooling;

static class SettingsProbe
{
    sealed class InertTelemetry : ITelemetryExportSource
    {public int Captures;public ExportTelemetry Capture(DateTimeOffset now){Captures++;throw new InvalidOperationException("No network worker is allowed in this test.");}}
    static async Task Main()
    {
        var results=new List<object>();
        foreach(var failConnecting in new[]{false,true})
        {
            try
            {
                using var canvas=(Control)R.T("ProductCanvas").GetConstructors(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
                    .Single(c=>c.GetParameters().Length==0).Invoke(null);
                var source=new InertTelemetry();
                var initial=new ExternalCoolingSettings();
                var folder=Path.Combine(AppContext.BaseDirectory,"settings-files",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                var path=Path.Combine(folder,"external-cooling.json");var store=R.New("ExternalCoolingSettingsStore",path);R.Call(store,"Save",initial,null,false);
                // This is deliberately invalid Base64 in our own test file.
                // ReadKey fails in Convert.FromBase64String before DPAPI or a
                // network worker. No real pairing key is read or generated.
                var node=JsonNode.Parse(File.ReadAllText(path))!;node["protectedPairingKey"]="invalid-base64!";
                File.WriteAllText(path,node.ToJsonString());R.Call(store,"Load",(object?)null);
                await using var manager=new ExternalCoolingManager(source,()=>(byte[]?)R.Call(store,"ReadKey"));
                Func<ExternalCoolingSettings,string?,bool,bool,Task> apply=async(settings,key,remove,connect)=>
                {await (Task)R.T("ExternalSettingsApply").GetMethod("PersistAndConfigureAsync",R.Flags)!.Invoke(null,
                    [(Func<Task>)(()=>{R.Call(store,"Save",settings,key,remove);return Task.CompletedTask;}),
                     (Func<Task>)(()=>manager.ConfigureAsync(settings,connect))])!;};
                using var page=(IDisposable)R.New("ExternalCoolingPage",canvas,initial,manager,(Func<bool>)(()=>(bool)R.Get(store,"Paired")!),apply,null,(Action<string>)(_=>{}));
                SynchronizationContext.SetSynchronizationContext(null); // No visible Form/message pump in this fixture.
                ((Control)R.Get(page,"Host")!).Text="127.0.0.2";
                await R.Invoke(page,"ExecuteAsync","external-mode-2");
                await R.Invoke(page,"ExecuteAsync",failConnecting?"external-connection":"external-save");
                var memorySaved=R.Get(page,"SavedMode")!.ToString();var dirty=R.Get(page,"Dirty");var notice=R.Get(page,"Notice");
                var disk=R.Call(store,"Load",(object?)null); // One out-argument, not a null params array.
                await R.Invoke(page,"ExecuteAsync","external-discard");
                var visibleMode=R.Get(page,"Mode")!.ToString();var diskMode=((ExternalCoolingSettings)disk!).Mode.ToString();
                var noHandles=!canvas.IsHandleCreated&&new[]{"Host","Port","Key"}.All(k=>!((Control)R.Get(page,k)!).IsHandleCreated)&&Application.OpenForms.Count==0;
                if(!noHandles||source.Captures!=0)throw new InvalidOperationException("Settings fixture created a window handle or started telemetry.");
                var details=new{ConnectionFailureInjected=failConnecting,SavedModelMode=memorySaved,DiskMode=diskMode,
                    DirtyAfterSave=dirty,Notice=notice,VisibleModeAfterDiscard=visibleMode,DirtyAfterDiscard=R.Get(page,"Dirty"),
                    ModelAndFileAgreeAfterDiscard=visibleMode==diskMode,DesktopHandlesCreated=false,TelemetryCaptures=source.Captures,
                    Scope="Actual settings page model, file store, key decoder and manager. Test file has invalid Base64; failure occurs before DPAPI, sockets or a network worker. No Form or dialogs shown."};
                results.Add(new{name="K01-external-save-connection-failure-"+failConnecting,completed=true,details});
                Console.WriteLine(JsonSerializer.Serialize(details));
            }
            catch(Exception e){results.Add(new{name="K01-"+failConnecting,completed=false,error=e.ToString()});Console.WriteLine("HARNESS ERROR "+e);}
        }
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"settings-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"settings-results","results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
}
