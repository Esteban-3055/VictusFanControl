using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static class ProductHomeTelemetrySelfTest
{
    internal static ProductRuntimeState StateAt(DateTimeOffset stamp,double percent=60,string source="Ac",string session="session-a")
    {
        var snapshot=new TelemetrySnapshot(stamp,"CPU",60,20,35,"GPU",50,30,45,3000,1500)
        {CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",60)],ProductTelemetryTolerance=true,
            FanTelemetrySource="HP-WMI-ACPI-2D",FanSampledAtUtc=stamp,FanSampleAgeMilliseconds=0,FanAgeCapturedAtUtc=stamp,FanRpmResolution=100,
            CpuFanSpeedLevel=30,GpuFanSpeedLevel=15};
        return new(){Runtime="Healthy",Source=source,FanMode="Automatic",FanAuthority="Custom",FanLevel=30,
            ManualAuthorized=true,AutomaticAuthorized=true,PerformanceSupported=true,CpuState="Active",GpuState="ActiveUnverified",
            AppliedPerformance=new ProductProfiles().PerformanceConfiguration(),AppliedPerformanceSource=source,
            AppliedFanProfile=source,AutomaticSessionId=session,Snapshot=snapshot,AutomaticDecisionSnapshot=snapshot,
            AppliedAutomaticConfiguration=new ProductProfiles().Ac.Fan,
            AutomaticDecision=new(AdaptiveFanProductionMode.Automatic,AdaptiveFanProductionActionKind.HoldCustom,true,30,30,FanAuthority.Custom,"fixture")
            {UnifiedDemand=new(percent,30,0,false,percent,40,30,20,10,15)}};
    }
    internal static void Run(Action<bool,string> require)
    {
        var now=DateTimeOffset.UtcNow;var history=new ProductHomeTelemetry();var state=StateAt(now);
        history.Observe(state,now);history.Observe(state,now);
        require(history.Samples.Count==1&&history.Samples[0].Percent==60,"Demand history duplicated an observation or evaluated a different demand.");
        history.Observe(StateAt(now.AddSeconds(1),65),now.AddSeconds(1));
        require(!history.Samples[^1].StartsSegment,"Continuous decisions broke their trace.");
        history.Observe(state with{FanMode="Firmware"},now.AddSeconds(1.5));
        history.Observe(StateAt(now.AddSeconds(2),70),now.AddSeconds(2));
        require(history.Samples[^1].StartsSegment,"Firmware interruption was joined by a fabricated line.");
        history.Observe(StateAt(now.AddSeconds(3),75,"Battery"),now.AddSeconds(3));
        require(history.Samples[^1].StartsSegment,"Source transition joined two profiles.");
        history.Observe(StateAt(now.AddSeconds(4),80,"Battery","session-b"),now.AddSeconds(4));
        require(history.Samples[^1].StartsSegment,"New activation joined a previous session.");
        history.Observe(StateAt(now.AddSeconds(9),85,"Battery","session-b"),now.AddSeconds(9));
        require(history.Samples[^1].StartsSegment,"Acquisition gap was filled in.");
        var prior=history.Samples.Count;
        foreach(var invalid in new[]{state with{LifecycleBlocked=true},state with{AutomaticSourceTransition="Battery"},
            state with{Runtime="Failed"},state with{FanLevel=31},state with{AppliedFanProfile="Battery"},
            state with{AppliedAutomaticConfiguration=null},StateAt(now, double.NaN),StateAt(now,101),StateAt(now.AddMinutes(1))})
            require(ProductHomeTelemetry.CurrentDemand(invalid,now) is null,"Unconfirmed, invalid or future demand appeared as current.");
        require(ProductHomeTelemetry.CurrentDemand(state,now.AddSeconds(10)) is null,"Expired demand remained current.");
        history.Observe(state,now.AddSeconds(10));require(history.Samples.Count==prior,"Out-of-order decision entered history.");
        var bounded=new ProductHomeTelemetry();
        for(int i=0;i<700;i++){var at=now.AddMilliseconds(i*100);bounded.Observe(StateAt(at),at);}
        require(bounded.Samples.Count==600,"Demand history grew without a bound.");
        bounded.Observe(new(),now.AddMinutes(7));require(bounded.Samples.Count==0,"Old history remained in the five-minute window.");
        require(ProductHomeTelemetry.FanSnapshot(state with{FanMode="Firmware"},now) is not null,"Firmware fans were hidden despite current telemetry.");
        require(ProductHomeTelemetry.FanSnapshot(state with{Snapshot=state.Snapshot! with{FanSampledAtUtc=now.AddSeconds(-4),FanSampleAgeMilliseconds=4000}},now) is null,
            "Delayed fan feedback authorized a moving fan.");
        Console.WriteLine("Home telemetry: PASS (actual decision demand, duplicates, freshness, profile/session/interruption gaps, bounded history and current fan feedback; zero hardware IO).");
    }
}
