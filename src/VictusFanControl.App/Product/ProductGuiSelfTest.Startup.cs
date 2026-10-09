using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestStartupAutomatic(Action<bool,string> require)
    {
        var at=DateTimeOffset.UtcNow.AddSeconds(-2);
        ProductRuntimeState State(int i,string source="Ac")=>new(){Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source=source,
            AutomaticAuthorized=true,CanApplyPerformance=true,
            Snapshot=new TelemetrySnapshot(at.AddSeconds(i),"CPU",50,20,50,"GPU",45,10,30,1200,1200){CpuExpectedPhysicalCoreCount=3,CpuCoreTemperatures=new[]{new CpuCoreTemperatureSample(0,0,"Performance",50),new CpuCoreTemperatureSample(1,2,"Performance",49),new CpuCoreTemperatureSample(2,4,"Performance",48)}}};
        var gate=new ProductStartupAutomatic(0);
        require(!gate.Observe(State(0),at,0)&&!gate.Observe(State(0),at,1000),"Startup counted a repeated sample.");
        require(!gate.Observe(State(1),at.AddSeconds(1),1000)&&gate.Observe(State(2),at.AddSeconds(2),2000),"Three fresh startup acquisitions did not admit.");
        require(!gate.Observe(State(3),at.AddSeconds(3),3000),"Startup activation repeated.");
        foreach(var blocked in new[]{State(0) with{LifecycleBlocked=true},State(0) with{Failure="fixture"},State(0) with{Target="Unsupported"},
            State(0) with{PerformanceProcessPresent=true},State(0) with{FanMode="Manual"}})
        {gate=new(0);require(!gate.Observe(blocked,at,0)&&gate.Finished&&!gate.Observe(State(2),at.AddSeconds(2),2000),"Startup retried a blocking failure.");}
        gate=new(0);require(!gate.Observe(State(0),at,30000)&&gate.Finished,"Startup wait did not expire.");
        gate=new(0);gate.Cancel("fixture");require(!gate.Observe(State(0),at,0),"User cancellation rearmed startup.");
        gate=new(0);require(!gate.Observe(State(0),at.AddSeconds(4),0)&&!gate.Observe(State(1),at,1000),"Stale/future startup data admitted.");
        gate=new(0);gate.Observe(State(0),at,0);gate.Observe(State(1,"Battery"),at.AddSeconds(1),1000);
        require(!gate.Observe(State(2,"Battery"),at.AddSeconds(2),2000)&&gate.Observe(State(3,"Battery"),at.AddSeconds(3),3000),"Startup failed to requalify a source change.");
        var runtime=new RecordingRuntime();
        using(var form=new ProductForm("fixture://startup",fixture:runtime,fixtureProfiles:new ProductProfiles{ActivateAutomaticOnStart=true},enableStartupAutomaticInFixture:true))
        {
            form.Show();Application.DoEvents();
            runtime.Publish(State(0));runtime.Publish(State(1));runtime.Publish(State(2));Application.DoEvents();
            require(runtime.Commands==1,"Startup did not use the normal runtime command exactly once.");
            runtime.Publish(State(2));form.PresentationTick();require(runtime.Commands==1,"UI ticks repeated startup hardware activation.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Startup fixture exit stalled.");
        }
        var legacy=System.Text.Json.Nodes.JsonNode.Parse(ProductProfilesStore.Serialize(new ProductProfiles{Ac=ProductProfiles.LegacyDefaultProfile(ProductPowerProfile.Ac)}))!;
        legacy.AsObject().Remove("defaultCurveRevision");legacy.AsObject().Remove("activateAutomaticOnStart");
        var migrated=ProductProfilesStore.Parse(legacy.ToJsonString());
        require(!migrated.ActivateAutomaticOnStart&&FanConfigurationStore.Serialize(migrated.Ac.Fan)==FanConfigurationStore.Serialize(new ProductProfiles().Ac.Fan),"Default upgrade lost the new curve or enabled startup implicitly.");
        var fan=migrated.Ac.Fan;var custom=migrated with{DefaultCurveRevision=1,Ac=migrated.Ac with{Fan=fan with{Tuning=fan.Tuning with{FallTimeConstantSeconds=29}}},ActivateAutomaticOnStart=true};
        require(ProductProfilesStore.Serialize(ProductProfilesStore.Parse(ProductProfilesStore.Serialize(custom)))==ProductProfilesStore.Serialize(custom with{DefaultCurveRevision=2}),"Upgrade changed custom fan tuning or persisted startup preference.");
        var clock=0L;var guard=new ProductAutomaticReview(()=>clock,ProductAutomaticReviewMode.Habitual);guard.Start();clock=2700001;
        require(!guard.Expired&&guard.RemainingSeconds is null,"Habitual Automatic retained a review duration limit.");
        Console.WriteLine("PASS Product v1 startup: one attempt, distinct/fresh acquisitions, source change, cancellation, timeout, custom migration; no hardware IO.");
    }
}
