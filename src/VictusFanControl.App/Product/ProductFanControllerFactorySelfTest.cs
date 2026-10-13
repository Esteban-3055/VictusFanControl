using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.PlatformThermalReplay;
using VictusFanControl.Product;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

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
            var backend=new NoIoBackend(writable,54);
            var fans=new FanControlCoordinator(backend);
            var controller=ProductFanControllerFactory.Create(fans,profiles,hardware,Hp8C40TargetProfile.Instance,
                automaticReview:false,upperRangePreview:preview,experimentalPolicy:retention);
            require(controller.Mode==AdaptiveFanProductionMode.Firmware && fans.Authority==FanAuthority.Firmware && backend.Calls==0,
                "Product controller startup acquired hardware authority.");
            require(controller.ManualMaximumLevel==54 && controller.ManualExecutionAuthorized==writable,
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
        TestProductControl(require,hardware);
        Console.WriteLine("Product runtime controller startup: PASS (real constructor, preview/normal, writable/closed backend, saved retention on/off, Firmware startup, 54/50 ranges, blocked Automatic and unchanged core envelope; zero hardware IO).");
    }
    private static void TestProductControl(Action<bool,string> require,HardwareIdentity hardware)
    {
        long clock=0;var origin=DateTimeOffset.UtcNow;
        DateTimeOffset Now()=>origin.AddMilliseconds(clock);
        var profiles=new ProductProfiles();var points=profiles.Ac.Fan.UnifiedDemand!.Curve.ToArray();points[^1]=points[^1] with{Level=54};
        profiles=profiles with{Ac=profiles.Ac with{Fan=profiles.Ac.Fan with{UnifiedDemand=profiles.Ac.Fan.UnifiedDemand! with{Curve=points}}}};
        var serialized=ProductProfilesStore.Serialize(profiles);profiles=ProductProfilesStore.Parse(serialized);
        require(profiles.Ac.Fan.BuildPolicy().MaximumLevel==54 && profiles.Battery.Fan.UnifiedDemand!.Curve[^1].Level==50,
            "54-level profile lost its range or changed its peer.");
        var backend=new ProductBackend();var fans=new FanControlCoordinator(backend);
        var retention=new ProductPlatformRetention();retention.Configure(false);
        var controller=ProductFanControllerFactory.Create(fans,profiles,hardware,Hp8C40TargetProfile.Instance,false,false,retention,()=>clock,Now);
        TelemetrySnapshot Sample(double cpu=45)=>new(Now(),"Intel Core i7-13700H",cpu,5,17,Hp8C40TargetProfile.ExpectedGpuName,47,14,0,3000,3000)
        {CpuExpectedPhysicalCoreCount=14,CpuCoreTemperatures=Enumerable.Range(0,14).Select(i=>new CpuCoreTemperatureSample(i,i,i<6?"Performance":"Efficiency",cpu)).ToArray()};
        SafetyGateResult Raw(TelemetrySnapshot s)=>SafetyGate.Evaluate(hardware,SystemState.Healthy,s,Now(),true);
        controller.SetModeAsync(AdaptiveFanProductionMode.Manual,default).AsTask().GetAwaiter().GetResult();
        foreach(var level in new[]{51,52,53,54})
        {
            var sample=Sample();controller.ApplyManualAsync(level,Raw(sample),default).AsTask().GetAwaiter().GetResult();
            var expected=Hp8C40ProductFanTargets.FromLogical(level);
            require(backend.Requests[^1].Payload.SequenceEqual(new byte[]{(byte)expected.Cpu,(byte)expected.Gpu,0,0}),"Product Manual dispatched the wrong pair.");
        }
        controller.SetModeAsync(AdaptiveFanProductionMode.Firmware,default).AsTask().GetAwaiter().GetResult();
        var before=backend.Requests.Count;
        controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,default).AsTask().GetAwaiter().GetResult();
        for(var i=0;i<25;i++)
        {
            clock+=1000;var sample=Sample(i==0?45:90);
            controller.ProcessAutomaticAsync(sample,Raw(sample),default).AsTask().GetAwaiter().GetResult();
        }
        require(backend.Requests.Skip(before).Any(r=>r.Payload.SequenceEqual(new byte[]{54,52,0,0})),"Product Automatic did not reach 54/52.");
        require(backend.Requests.Where(r=>r.CommandType==0x2E && r.Payload[0]!=255).All(r=>r.Payload[0]<=54 && r.Payload[1]<=52),"Product control exceeded fan limits.");
        // A newly applied curve is used without resetting authority or losing the upper range.
        controller.ApplyUnifiedDemandAsync(profiles.Ac.Fan.UnifiedDemand!,()=>{},default).AsTask().GetAwaiter().GetResult();
        clock+=1000;var critical=Sample(99);var released=controller.ProcessAutomaticAsync(critical,Raw(critical),default).AsTask().GetAwaiter().GetResult();
        require(released.Action==AdaptiveFanProductionActionKind.RestoreFirmware && fans.Authority==FanAuthority.Firmware,"54/52 Automatic lost immediate 99 C release.");
        fans.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("Product 54/52 control: PASS (real factory/controller/session, Manual 51..54, Automatic and live curve at 54/52, persisted AC curve/unchanged Battery, GPU cap, immediate 99 C release; fake IO).");
    }
    private sealed class ProductBackend:IFanControlBackend
    {
        public List<HpBiosRequest> Requests {get;}=[];
        private WmiFanSession _session;
        private bool _active;
        private WmiFanSession NewSession()=>new(r=>{Requests.Add(r);return 0;},_=>{},10,true);
        public ProductBackend(){_session=NewSession();}
        public string Name=>"product-control-fixture";
        public bool CanWrite=>true;
        public FanBackendCapabilities Capabilities=>new("8C40",10,54,false);
        public ValueTask ProbeControlDependencyAsync(CancellationToken t)=>ValueTask.CompletedTask;
        public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken t)=>ValueTask.FromResult(new FanBackendStatus(Name,true,_active,true,true,"fixture"));
        public ValueTask EnterCustomModeAsync(CancellationToken t){_active=true;return ValueTask.CompletedTask;}
        public ValueTask ApplyAsync(FanCommand c,CancellationToken t){FanDispatchAdmissionScope.EnsureAllowed();_session.Apply(c.CpuLevel,true);return ValueTask.CompletedTask;}
        public ValueTask RestoreFirmwareAutoAsync(CancellationToken t){_session.Recover();_session=NewSession();_active=false;return ValueTask.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
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
