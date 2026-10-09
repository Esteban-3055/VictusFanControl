using VictusFanControl.Control.Adaptive;
using VictusFanControl.OemShadow;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;

namespace VictusFanControl.PlatformThermalReplay;

internal static class ProductRetentionTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
        void Refused(Action action,string message){bool refused=false;try{action();}catch(InvalidOperationException){refused=true;}Check(refused,message);}
        var start=DateTimeOffset.UnixEpoch;
        AdaptiveFanPolicyInput Input(int t,double cpu=40)=>new(start.AddSeconds(t),cpu,5,0,35,2,0){CpuRawControlTemperatureC=cpu};
        TelemetrySnapshot Snapshot(int t)=>new(start.AddSeconds(t),"CPU",40,5,0,"GPU",35,2,0,null,null);
        void Observe(ProductPlatformRetention p,int t,double tz=90,double dtt=67,string source="Ac",bool custom=false)
        {var at=start.AddSeconds(t);p.SetSources(new(tz,at),new(dtt,at));p.ObserveTelemetry(Snapshot(t),source,custom);}
        var disabled=new ProductPlatformRetention();
        Check(disabled.GetSupplement(Input(0),12,40) is null,"disabled auxiliary sources must impose no admission");
        disabled.EnsureDispatchAllowed(start,start.AddHours(1));
        var bootstrap=new ProductPlatformRetention();bootstrap.Configure(true);
        bootstrap.SetSources(new(),new());bootstrap.ObserveTelemetry(Snapshot(0),"Ac",false);
        Observe(bootstrap,1);Check(!bootstrap.State.Ready,"initial absence cannot count as an acquisition");
        Observe(bootstrap,2);Check(bootstrap.State.Ready,"two real acquisitions qualify after initially empty native slots");
        bootstrap.SetSources(new(),new());bootstrap.ObserveTelemetry(Snapshot(3),"Ac",true);
        Refused(()=>bootstrap.GetSupplement(Input(3),12,40),"missing source after qualification remains an active fault");
        var p=new ProductPlatformRetention();p.Configure(true);Observe(p,0);
        Refused(()=>p.GetSupplement(Input(0),12,40),"one acquisition cannot qualify");
        Observe(p,1);Check(p.State.Ready,"distinct fresh source epochs qualify");
        Check(p.GetSupplement(Input(1),12,null) is null,"no acknowledged target means no retention");
        Check(p.GetSupplement(Input(1),12,40)==14,"auxiliary contribution is limited to two raw levels");
        p.EnsureDispatchAllowed(start.AddSeconds(1),start.AddSeconds(1.2));
        Refused(()=>p.EnsureDispatchAllowed(start.AddSeconds(1),start.AddSeconds(4)),"dispatch source at three seconds is stale");
        for(int i=2;i<=80;i++)
        {
            Observe(p,i);var extra=p.GetSupplement(Input(i),12,40);
            Check(i<61 ? extra==14 : extra is null,"continuous warm sources must not renew expired retention");
        }
        Observe(p,81);Check(p.GetSupplement(Input(81),40,40) is null,"baseline catches retention ceiling");
        Observe(p,82);Check(p.GetSupplement(Input(82),12,13)==13,"cannot request a target above the acknowledged level");
        Refused(()=>p.GetSupplement(Input(81),12,40),"prior frame cannot join current auxiliary epochs");
        Observe(p,83,source:"Battery",custom:true);
        Refused(()=>p.GetSupplement(Input(83),12,40),"source change permanently interrupts active feature");
        Observe(p,84);Refused(()=>p.GetSupplement(Input(84),12,40),"fresh sources alone cannot rearm a fault");
        p.Configure(true);Observe(p,90);Observe(p,91);Observe(p,95,custom:true);
        Refused(()=>p.GetSupplement(Input(95),12,40),"custom telemetry gaps fail closed");
        p=new();p.Configure(true);Observe(p,0);Observe(p,1);
        p.SetSources(new(90,start.AddSeconds(2)),new(double.NaN,start.AddSeconds(2)));p.ObserveTelemetry(Snapshot(2),"Ac",true);
        Refused(()=>p.GetSupplement(Input(2),12,40),"invalid auxiliary cannot be silently ignored when enabled");
        var original=new ProductProfiles {ExperimentalPlatformRetention=true};
        Check(ProductProfilesStore.Copy(original).ExperimentalPlatformRetention,"preference survives validated serialization");
        var legacyNode=System.Text.Json.Nodes.JsonNode.Parse(ProductProfilesStore.Serialize(new ProductProfiles()))!;
        legacyNode.AsObject().Remove("experimentalPlatformRetention");
        var legacy=legacyNode.ToJsonString();
        Check(!ProductProfilesStore.Parse(legacy).ExperimentalPlatformRetention,"existing preferences remain disabled");
        var candidate=ProductQuietCandidate.Stage(original);
        Check(candidate.Ac.CpuPl1Watts==original.Ac.CpuPl1Watts && candidate.Ac.CpuPl2Watts==original.Ac.CpuPl2Watts &&
            candidate.Ac.GpuMaximumMHz==original.Ac.GpuMaximumMHz && candidate.Battery==original.Battery,"preset preserves CPU/GPU limits and Battery");
        for(int i=0;i<=100;i++)
        {
            var input=Input(i,40+i*.5);var a=original.Ac.Fan.UnifiedDemand!.Evaluate(input);var b=candidate.Ac.Fan.UnifiedDemand!.Evaluate(input);
            Check(b.Level<=a.Level,"candidate never raises instantaneous base curve");
            Check(i<90 || b.Level>=44,"high thermal range cannot be reduced");
        }
        var basePolicy=new AdaptiveFanInertiaPolicy(original.Ac.Fan.BuildPolicy(),original.Ac.Fan.Tuning);
        var neutral=new AdaptiveFanInertiaPolicy(original.Ac.Fan.BuildPolicy(),original.Ac.Fan.Tuning);
        for(int i=0;i<2000;i++)Check(basePolicy.Evaluate(Input(i,40+i%60))==neutral.EvaluateWithSupplement(Input(i,40+i%60),_=>null),"null supplement is bit-for-bit neutral");
        var hot=new AdaptiveFanInertiaPolicy(candidate.Ac.Fan.BuildPolicy(),candidate.Ac.Fan.Tuning);
        var result=hot.EvaluateWithSupplement(Input(0,99),_=>12);
        Check(result.ThermalOverride && result.RawDemandLevel>=50,"hottest raw CPU retains thermal protection");
        var capped=new AdaptiveFanInertiaPolicy(original.Ac.Fan.BuildPolicy(),original.Ac.Fan.Tuning);
        var initial=capped.Evaluate(Input(0,65));var acknowledged=initial.EqualFanLevel!.Value;
        capped.Evaluate(Input(1,80));
        for(int i=2;i<=20;i++)
        {
            var held=capped.EvaluateWithSupplement(Input(i),_=>acknowledged,retentionOnly:true);
            Check(held.EqualFanLevel<=acknowledged,"latent filter memory must not turn retention into an upward fan request");
        }
        Console.WriteLine($"Product retention self-test: PASS ({checks} checks; bounded episodes, freshness, AC fault, legacy preferences, raw protection; no hardware IO).");
    }
}
