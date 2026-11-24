using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;

namespace VictusFanControl.App;

// Explicit UI test entry only. No production runtime, device readers, helpers,
// startup writer, network manager or hardware interfaces are constructed.
internal static class ProductFunctionsInteractivePreview
{
    internal static void Run()
    {
        var folder=Path.Combine(AppContext.BaseDirectory,"interactive-preview",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"profiles.json");
        var profiles=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,Functions=new ProductFunctionPolicies().Unlinked()};
        ProductProfilesStore.Save(profiles,path);
        var runtime=new PreviewRuntime(profiles.Functions!);
        using var form=new ProductForm("fixture://functions-ui-preview",fixture:runtime,fixtureProfiles:profiles,
            profilesPath:path,restartDirectory:folder,startupRegistrationReader:()=>Task.FromResult(false));
        form.Text="VictusFanControl · PRUEBA AISLADA · sin hardware";
        form.Canvas.Page=ProductPage.Functions;form.KeyPreview=true;
        form.KeyDown+=async(_,e)=>{if(e.Control&&e.Shift&&e.KeyCode==Keys.Q){e.Handled=true;await form.RequestExitAsync();}};
        Console.WriteLine("PREVIEW_DATA="+folder);
        Application.Run(form);
        File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new{runtime.Commands,runtime.Disposed,
            Saved=ProductProfilesStore.Load(path,out _).Functions,scope="UI fixture only; no production control owner constructed"},new JsonSerializerOptions{WriteIndented=true}));
    }
    private sealed class PreviewRuntime : IProductRuntime
    {
        internal readonly List<string> Commands=[];
        internal bool Disposed;
        public event Action<ProductRuntimeState>? Changed;
        public ProductRuntimeState State{get;private set;}
        internal PreviewRuntime(ProductFunctionPolicies policies)
        {
            State=new(){Hardware="PRUEBA AISLADA · datos simulados · Ctrl+Shift+Q termina",Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source="Ac",
                FanMode="Firmware",FanAuthority="Firmware",AutomaticAuthorized=true,ManualAuthorized=true,PerformanceSupported=true,
                CanApplyPerformance=true,FunctionCoordinationExplicit=true,FunctionPolicies=policies,FunctionRequests=new(CpuLimits:true),CpuState="Active",
                AppliedPerformance=new ProductProfiles{CpuEnabled=true,GpuEnabled=false}.PerformanceConfiguration(),AppliedPerformanceSource="Ac",GuardianState="Simulado"};
        }
        private void Publish()
        {
            bool Ready(ProductFunction function)=>State.FunctionPolicies.Get(function).Binding switch
            {ProductFunctionBinding.FollowAutomatic=>State.FanMode=="Automatic",ProductFunctionBinding.FollowInternalFans=>State.FanMode!="Firmware",_=>true};
            State=State with{CpuState=State.FunctionRequests.CpuLimits&&Ready(ProductFunction.CpuLimits)?"Active":"Disabled",
                GpuState=State.FunctionRequests.GpuClocks&&Ready(ProductFunction.GpuClocks)?"ActiveUnverified":"Disabled",
                CtgpState=State.FunctionRequests.Ctgp&&Ready(ProductFunction.Ctgp)?"Active":"Disabled"};
            Changed?.Invoke(State);
        }
        public void Start()=>Publish();
        public void RequestAutomaticTelemetry(){}
        public Task ConfigureFunctionsAsync(ProductFunctionPolicies policies)
        {Commands.Add("configure-functions");State=State with{FunctionPolicies=policies};Publish();return Task.CompletedTask;}
        public Task SetPerformanceDomainAsync(bool cpu,bool enabled,ProductProfiles profiles)
        {Commands.Add((cpu?"CPU":"GPU")+"="+enabled);State=State with{FunctionRequests=State.FunctionRequests.With(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks,enabled)};Publish();return Task.CompletedTask;}
        public Task SetCtgpAsync(bool enabled)
        {Commands.Add("cTGP="+enabled);State=State with{FunctionRequests=State.FunctionRequests with{Ctgp=enabled}};Publish();return Task.CompletedTask;}
        public Task SetExternalCoolingRequestedAsync(bool enabled)
        {Commands.Add("base="+enabled);State=State with{FunctionRequests=State.FunctionRequests with{ExternalCooling=enabled}};Publish();return Task.CompletedTask;}
        public Task SelectFanModeAsync(AdaptiveFanProductionMode mode,ProductProfiles profiles)
        {Commands.Add("fan-mode="+mode);State=State with{FanMode=mode.ToString(),FanAuthority=mode==AdaptiveFanProductionMode.Firmware?"Firmware":"Custom"};Publish();return Task.CompletedTask;}
        public Task ApplyFanCurveAsync(ProductPowerProfile source,UnifiedFanDemand demand)=>Task.CompletedTask;
        public Task ApplyFanTuningAsync(AdaptiveFanTuning tuning)=>Task.CompletedTask;
        public Task ApplyManualAsync(int level)=>Task.CompletedTask;
        public Task ApplyPerformanceAsync(ProductProfiles profiles)=>Task.CompletedTask;
        public Task ReleasePerformanceAsync()=>Task.CompletedTask;
        public void FenceLifecycle(string reason){}
        public Task ReleaseForLifecycleAsync(string reason)=>Task.CompletedTask;
        public void ResumeTelemetry(string reason){}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
}
