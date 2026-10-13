using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.PlatformThermalReplay;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static class ProductFanControllerFactorySelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var hardware=new HardwareIdentity(Hp8C40TargetProfile.BoardManufacturer,Hp8C40TargetProfile.BoardProduct,
            Hp8C40TargetProfile.BoardVersion,Hp8C40TargetProfile.SystemManufacturer,Hp8C40TargetProfile.SystemProductName,
            Hp8C40TargetProfile.SystemSkuPrefix+"#AKH",Hp8C40TargetProfile.ValidatedBiosVersion);
        foreach(var retentionEnabled in new[]{false,true})
        foreach(var preview in new[]{false,true})
        foreach(var writable in new[]{false,true})
        {
            var profiles=new ProductProfiles{ExperimentalPlatformRetention=retentionEnabled};
            var retention=new ProductPlatformRetention();retention.Configure(retentionEnabled);
            var backend=new NoIoBackend(writable,preview?54:50);
            var fans=new FanControlCoordinator(backend);
            var controller=ProductFanControllerFactory.Create(fans,profiles,hardware,Hp8C40TargetProfile.Instance,
                automaticReview:false,upperRangePreview:preview,experimentalPolicy:retention);
            require(controller.Mode==AdaptiveFanProductionMode.Firmware && fans.Authority==FanAuthority.Firmware && backend.Calls==0,
                "Product controller startup acquired hardware authority.");
            require(controller.ManualMaximumLevel==(preview?54:50) && controller.ManualExecutionAuthorized==writable,
                "Product controller startup lost its Manual range or backend gate.");
            require(controller.AutomaticExecutionAuthorized==(!preview && writable),
                "Product controller startup changed Automatic authorization.");
            if(preview || !writable)
            {
                var result=controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,CancellationToken.None).AsTask().GetAwaiter().GetResult();
                require(result.Action==AdaptiveFanProductionActionKind.Blocked && controller.Mode==AdaptiveFanProductionMode.Firmware && backend.Calls==0,
                    "Manual preview or closed backend admitted Automatic.");
            }
            require(profiles.ExperimentalPlatformRetention==retentionEnabled,"Startup rewrote retention preferences.");
            fans.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        var fallback=new FanControlCoordinator(new DisabledFanControlBackend());
        var unsupported=ProductFanControllerFactory.Create(fallback,new ProductProfiles(),hardware,null,false,true,null);
        require(!unsupported.ManualExecutionAuthorized && !unsupported.AutomaticExecutionAuthorized,
            "Unsupported target gained control through preview startup.");
        fallback.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // Keep the core's physical-envelope refusal intact; fix the caller's configuration.
        var guarded=new FanControlCoordinator(new NoIoBackend(true,54));
        var refused=false;
        try
        {
            _=new AdaptiveFanProductionController(guarded,Hp8C40AdaptiveCandidateV1.Create(),true,false,
                automaticHardware:hardware,automaticConfiguration:new ProductProfiles().Ac.Fan,automaticMinimumLevel:10,
                useRawCpuThermalResponse:true,experimentalPolicy:new ProductPlatformRetention());
        }
        catch(ArgumentException ex) when(ex.ParamName=="experimentalPolicy"){refused=true;}
        require(refused,"Core accepted an experimental Automatic policy while Automatic was unauthorized.");
        guarded.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("Product runtime controller startup: PASS (real constructor, preview/normal, writable/closed backend, saved retention on/off, Firmware startup, 54/50 ranges, blocked Automatic and unchanged core envelope; zero hardware IO).");
    }
    private sealed class NoIoBackend(bool writable,int maximum):IFanControlBackend
    {
        public int Calls {get;private set;}
        public string Name=>"startup-no-IO";
        public bool CanWrite=>writable;
        public FanBackendCapabilities Capabilities=>new("8C40",10,maximum,false);
        private ValueTask Unexpected(){Calls++;return ValueTask.FromException(new InvalidOperationException("Startup attempted backend IO."));}
        public ValueTask ProbeControlDependencyAsync(CancellationToken token)=>Unexpected();
        public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken token){Calls++;return ValueTask.FromException<FanBackendStatus>(new InvalidOperationException("Startup attempted status IO."));}
        public ValueTask EnterCustomModeAsync(CancellationToken token)=>Unexpected();
        public ValueTask ApplyAsync(FanCommand command,CancellationToken token)=>Unexpected();
        public ValueTask RestoreFirmwareAutoAsync(CancellationToken token)=>Unexpected();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
