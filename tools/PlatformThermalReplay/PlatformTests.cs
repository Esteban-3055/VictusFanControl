using VictusFanControl.Control.Adaptive;
using VictusFanControl.OemShadow;
using VictusFanControl.Product;

namespace VictusFanControl.PlatformThermalReplay;

internal static class PlatformTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string reason){checks++;if(!ok)throw new InvalidOperationException(reason);}
        var start=DateTimeOffset.UnixEpoch;
        Frame F(double seconds,double tz=40,double dtt=40,double? epoch=null)
        {
            var now=start.AddSeconds(seconds);var at=start.AddSeconds(epoch??seconds);
            return new(now,new(40,now),new(40,now),new(35,now),new(tz,at),new(dtt,at));
        }
        PlatformThermalDemand Observer(bool tz=true,bool dtt=true)=>new(new(),tz,dtt);
        var o=Observer();
        Check(!o.Evaluate(F(0)).Available,"one acquisition cannot qualify");
        Check(!o.Evaluate(F(.5,epoch:0)).Available,"cache cannot qualify");
        Check(o.Evaluate(F(1)).Available,"two distinct acquisitions qualify");
        Check(o.Evaluate(F(2,epoch:1)).Available,"fresh cache can retain qualified state");
        Check(!o.Evaluate(F(4,epoch:1)).Available,"source age at three seconds is stale");
        Check(!o.Evaluate(F(5)).Available,"stale source requires requalification");
        Check(o.Evaluate(F(6)).Available,"source recovery qualifies");
        Check(!o.Evaluate(F(6)).Available,"duplicate frame breaks continuity");
        o=Observer();o.Evaluate(F(0));o.Evaluate(F(1));
        Check(!o.Evaluate(F(2,epoch:3)).Available,"future source refused");
        o=Observer();o.Evaluate(F(0));o.Evaluate(F(1));
        Check(!o.Evaluate(F(2,epoch:.5)).Available,"regressing acquisition refused");
        o=Observer();o.Evaluate(F(0));o.Evaluate(F(1));
        Check(!o.Evaluate(F(2,tz:41,epoch:1)).Available,"mutated cached value refused");
        o=Observer();o.Evaluate(F(0));o.Evaluate(F(1));
        Check(!o.Evaluate(F(5)).Available,"frame gap breaks continuity");
        foreach(var value in new[]{double.NaN,double.PositiveInfinity,-1,121})
            Check(!Observer().Evaluate(F(0,tz:value)).Available,"invalid sensor refused");
        Check(new PlatformThermalDemand(new(),false,false).Evaluate(F(0) with{Tz01=new(),Dtt3=new()}).Available,"disabled sensors do not become requirements");
        o=Observer(true,false);o.Evaluate(F(0) with{Dtt3=new()});
        Check(o.Evaluate(F(1) with{Dtt3=new()}).Available,"TZ-only does not require DTT");
        o=Observer();o.Evaluate(F(0,100,75));
        Check(o.Evaluate(F(1,100,75)).DemandLevel==44,"supplement has a level-44 cap");
        foreach(var setting in new[]{new PlatformSettings{MaximumSourceAgeSeconds=4},
            new PlatformSettings{QualificationAcquisitions=1},new PlatformSettings{QualificationSeconds=double.NaN},
            new PlatformSettings{Tz01Curve=[new(40,12),new(50,50)]},new PlatformSettings{Dtt3Curve=[new(40,12),new(30,20)]}})
        {
            bool failed=false;try{_ = new PlatformThermalDemand(setting,true,true);}catch(InvalidDataException){failed=true;}
            Check(failed,"invalid settings refused");
        }
        foreach(var source in Enum.GetValues<ProductPowerProfile>())
        {
            var fan=ProductProfiles.DefaultProfile(source).Fan;var config=fan.BuildPolicy();
            var a=new AdaptiveFanInertiaPolicy(config,fan.Tuning);var b=new AdaptiveFanInertiaPolicy(config,fan.Tuning);
            var neutral=new AdaptiveFanInertiaPolicy(config,fan.Tuning);
            for(int i=0;i<2000;i++)
            {
                var input=new AdaptiveFanPolicyInput(start.AddSeconds(i),40+(i%70),5+(i%60),i%101,
                    35+(i%47),2+(i%70),i%101){CpuRawControlTemperatureC=40+(i%70)};
                var decision=a.Evaluate(input);
                Check(decision==b.Evaluate(input,null),"inactive overload preserves exact decision");
                Check(decision==neutral.Evaluate(input,0),"zero supplement preserves exact decision");
            }
            var baseline=new AdaptiveFanInertiaPolicy(config,fan.Tuning);var extra=new AdaptiveFanInertiaPolicy(config,fan.Tuning);
            for(int i=0;i<300;i++)
            {
                var input=new AdaptiveFanPolicyInput(start.AddSeconds(i),40,5,0,35,2,0){CpuRawControlTemperatureC=40};
                var normal=baseline.Evaluate(input);var additional=extra.Evaluate(input,40);
                Check(additional.RawDemandLevel>=normal.RawDemandLevel,"supplement cannot reduce raw demand");
                Check(!additional.ThermalOverride,"platform cannot invoke raw CPU/GPU emergency");
                if(i==299)Check(additional.EqualFanLevel==40,"supplement passes through existing inertia");
            }
            var hot=new AdaptiveFanInertiaPolicy(config,fan.Tuning);
            hot.Evaluate(new(start,40,5,0,35,2,0));
            var thermal=hot.Evaluate(new(start.AddSeconds(1),40,5,0,35,2,0){CpuRawControlTemperatureC=99},12);
            Check(thermal.ThermalOverride&&thermal.RawDemandLevel>=50,"raw hottest CPU cannot be masked by low supplement");
            foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1,51})
                Check(!new AdaptiveFanInertiaPolicy(config,fan.Tuning).Evaluate(new(start,40,5,0,35,2,0),bad).Accepted,"invalid extra refused");
        }
        Console.WriteLine($"Platform thermal policy self-test: PASS ({checks} checks; no hardware IO).");
    }
}
