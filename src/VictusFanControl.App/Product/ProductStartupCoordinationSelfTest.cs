using VictusFanControl.Product;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static class ProductStartupCoordinationSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var origin=DateTimeOffset.UtcNow;
        ProductRuntimeState State(int seconds) => new()
        {
            Target="HP-8C40-9D0R1LA-F18", Runtime="Healthy", Source="Ac",
            AutomaticAuthorized=true, CanApplyPerformance=true,
            Snapshot=new(origin.AddSeconds(seconds),"CPU",50,20,30,"GPU",45,15,20,2500,2300)
            {
                ProductTelemetryTolerance=true, CpuExpectedPhysicalCoreCount=1,
                CpuCoreTemperatures=[new(0,0,"Performance",50)],
                FanTelemetrySource="HP-WMI-ACPI-2D", FanRpmResolution=100,
                FanSampleAgeMilliseconds=1800, FanSampledAtUtc=origin.AddSeconds(seconds).AddMilliseconds(-1800),
                FanAgeCapturedAtUtc=origin.AddSeconds(seconds), FanMaximumAgeMilliseconds=10000
            }
        };
        foreach(var phase in new[]{"startup","manual-retry","resumption"})
        {
            var gate=new ProductStartupAutomatic(0,manualRetry:phase=="manual-retry",resumption:phase=="resumption");
            require(!gate.Observe(State(0),origin,0),"One acquisition admitted "+phase);
            require(!gate.Observe(State(0),origin.AddMilliseconds(1300),1300),"Expired duplicate admitted "+phase);
            require(gate.Issues.Any(i=>i.Code=="fan-stale"),"Expired duplicate lost its displayed freshness reason.");
            require(!gate.Observe(State(2),origin.AddSeconds(2),2000),"Two acquisitions admitted "+phase);
            require(!gate.Observe(State(2),origin.AddMilliseconds(3300),3300),"Expired second duplicate admitted "+phase);
            require(gate.Observe(State(4),origin.AddSeconds(4),4000),
                "Presentation ticks erased fresh acquisitions during "+phase+" (observed boot RPM cadence).");
        }
        foreach(var changed in new[]{State(0) with{Runtime="Recovering"},State(0) with{CanApplyPerformance=false},
            State(0) with{Source="Unknown"},State(0) with{Snapshot=State(0).Snapshot! with{GpuTemperatureC=null}}})
        {
            var gate=new ProductStartupAutomatic(0);
            gate.Observe(State(0),origin,0);
            require(!gate.Observe(changed,origin.AddMilliseconds(500),500),"Unavailable state admitted startup.");
            require(!gate.Observe(State(2),origin.AddSeconds(2),2000)&&!gate.Observe(State(4),origin.AddSeconds(4),4000),
                "Invalid/recovering data preserved qualification instead of resetting it.");
        }
        var interrupted=new ProductStartupAutomatic(0);
        interrupted.Observe(State(0),origin,0);
        require(!interrupted.Observe(State(4),origin.AddSeconds(4),4000)&&
            !interrupted.Observe(State(6),origin.AddSeconds(6),6000),"A discontinuity reused earlier startup qualification.");
        Console.WriteLine("PASS startup/retry/resumption coordination: delayed duplicate ticks, strict current freshness, invalid-state reset and continuity.");
    }
}
