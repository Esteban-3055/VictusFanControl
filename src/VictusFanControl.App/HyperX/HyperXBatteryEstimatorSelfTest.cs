using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal static partial class HyperXBatteryEstimatorSelfTest
{
    internal static void Run(Action<bool,string> check)
    {
        byte[] Reply(int mv){var b=new byte[20];b[0]=0x21;b[1]=0xff;b[2]=5;b[3]=(byte)(mv>>8);b[4]=(byte)mv;return b;}
        double previous=0;var integers=new HashSet<int>();
        for(int mv=HyperXBatteryEstimator.MinimumMillivolts;mv<=HyperXBatteryEstimator.MaximumMillivolts;mv++)
        {
            var raw=HyperXBatteryEstimator.RawPercent(mv);
            check(raw is >=10 and <=100&&raw>=previous,"Continuous curve has a gap, inversion or invalid percentage");
            previous=raw!.Value;integers.Add((int)Math.Round(previous,MidpointRounding.AwayFromZero));
        }
        check(integers.SetEquals(Enumerable.Range(10,91)),"Curve cannot resolve all one-percent levels in its reference range");
        foreach(var mv in new[]{3763,3783,3824,3834,3839,4096})
            check(HyperXBatteryEstimator.RawPercent(mv) is not null,"Former srn range gap still loses battery data");
        foreach(var mv in new[]{int.MinValue,0,3299,3300,3583,4108,4500,int.MaxValue})
            check(HyperXBatteryEstimator.RawPercent(mv) is null,"Curve extrapolated an uncharacterized low or charging value");
        var estimate=new HyperXBatteryEstimator();
        check(estimate.Observe(3795,0)==56,"First reply waits for artificial smoothing or stays in five-point steps");
        for(int i=0;i<100;i++)check(estimate.Observe(3970,0)==56,"Duplicate clock/refresh advanced the filter");
        var smoothed=estimate.Observe(3804,60_000);
        check(smoothed is >56 and <60,"Fresh reply did not smooth a voltage step");
        var once=new HyperXBatteryEstimator();var twice=new HyperXBatteryEstimator();
        once.Observe(3792,0);twice.Observe(3792,0);twice.Observe(3825,30_000);
        check(once.Observe(3825,60_000)==twice.Observe(3825,60_000),"Smoothing depends on call count instead of acquisition interval");
        estimate.Reset();estimate.Observe(3804,0);
        check(estimate.Observe(3910,179_999) is <85,"Warm filter reset before the freshness gap boundary");
        estimate.Reset();estimate.Observe(3804,0);
        check(estimate.Observe(3910,180_000)==85,"A three-minute gap blended an old reading into the new one");
        check(estimate.Observe(3792,1000)==55,"A reversed monotonic epoch retained filter state");
        estimate.Reset();estimate.Observe(3787,0);
        for(int i=1;i<=8;i++)check(estimate.Observe(i%2==0?3787:3788,i*60_000)==52,"One-point jitter was amplified by integer rounding");
        estimate.Reset();estimate.Observe(3970,0);
        check(estimate.Observe(3584,60_000)==10,"Smoothing postponed the lowest supported critical reading");
        check(estimate.Observe(3500,120_000) is null&&estimate.Observe(3792,180_000)==55,"Unknown low voltage became zero or polluted recovery");
        estimate.Reset();estimate.Observe(3960,0);
        int? full=null;for(int i=1;i<=8;i++)full=estimate.Observe(3970,i*60_000);
        check(full==100,"Filtering or hysteresis permanently prevents full-threshold notification");

        var reader=new HyperXMonitor();var now=DateTimeOffset.UtcNow;
        try
        {
            reader.SelectBatterySource("fixture://receiver-A");
            check(reader.ApplyBatteryReport(Reply(3795),now,0)&&reader.Snapshot is{Percent:56,Millivolts:3795,SampledAt:not null},"Production snapshot does not use the continuous estimator");
            var captured=reader.Snapshot;
            check(!reader.ApplyBatteryReport(new byte[20],now.AddSeconds(1),1000)&&reader.Snapshot==captured,"Unrelated HID report advanced the battery acquisition");
            var export=new TelemetryExportService(()=>new(),new EmptyMemory(),()=>reader.Snapshot);
            for(int i=1;i<=90;i++)
            {
                var frame=export.Capture(now.AddSeconds(i));
                check(reader.Snapshot==captured&&frame.HyperxBatteryPct==56&&frame.HyperxSampledAtUtc==now,"UI/export refreshed sample age or changed smoothing");
            }
            var packet=ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,export.Capture(now));
            check(ExternalCoolingProtocol.Telemetry(packet).HyperxBatteryPct==56,"Non-five-point battery value changed on the ESP32 wire");
            check(export.Capture(now.AddSeconds(180)).HyperxBatteryPct is null,"Filtered battery bypassed the independent freshness limit");
            reader.ApplyBatteryReport(Reply(3804),now.AddSeconds(60),60_000);
            check(reader.Snapshot.Percent is >56 and <60,"Reader publishes the unsmoothed percentage");
            reader.ApplyBatteryReport(Reply(3584),now.AddSeconds(120),120_000);
            check(new HyperXNotificationPolicy().Evaluate(reader.Snapshot,new(),now.AddSeconds(120)).Single().Title.Contains("crítica"),"Default critical notice was delayed by smoothing");
            reader.InvalidateBattery("fixture failed query");
            check(reader.Snapshot.Percent is null&&reader.Snapshot.SampledAt is null,"Query failure retained a fresh battery");
            reader.ApplyBatteryReport(Reply(3792),now.AddSeconds(180),180_000);
            check(reader.Snapshot.Percent==55,"Failed query history contaminated the next acquisition");
            reader.ApplyBatteryReport(Reply(4200),now.AddSeconds(240),240_000);
            check(reader.Snapshot is{Percent:null,Charging:true}&&export.Capture(now.AddSeconds(240)).HyperxSampledAtUtc is null,"Inferred charging invented full battery or exported a null percentage timestamp");
            reader.ApplyBatteryReport(Reply(3970),now.AddSeconds(300),300_000);
            check(reader.Snapshot.Percent==100,"Charging history delayed the first later known percentage");
            reader.ApplyEvent(0x64,3);reader.ApplyBatteryReport(Reply(3970),now.AddSeconds(360),360_000);
            check(reader.Snapshot.Percent is null&&reader.Snapshot.SampledAt is null,"Late reply revived an explicitly powered-off headset");
            reader.ApplyEvent(0x64,1);reader.ApplyBatteryReport(Reply(3792),now.AddSeconds(420),420_000);
            check(reader.Snapshot.Percent==55,"Power-cycle retained the previous filter");
            reader.ApplyEvent(0x65,4);reader.SelectBatterySource("fixture://receiver-B");
            check(reader.Snapshot.SampledAt is null&&reader.Snapshot.Muted is null&&!reader.DrainMuteEvents().Any(),"Replacement receiver inherited the old headset state or queued mute events");
            reader.ApplyBatteryReport(Reply(3910),now.AddSeconds(480),480_000);
            check(reader.Snapshot.Percent==85,"Different receiver inherited smoothed charge");
            reader.ApplyBatteryReport(Reply(3500),now.AddSeconds(540),540_000);
            check(reader.Snapshot is{Millivolts:3500,Percent:null,Charging:false}&&export.Capture(now.AddSeconds(540)).HyperxBatteryPct is null,"Uncalibrated 0-9 region was invented, hidden as zero or exported");
        }
        finally{reader.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        Console.WriteLine("PASS HyperX continuous estimate: monotonic one-percent interpolation, repaired table gaps, acquisition-only EMA/hysteresis, no delayed 10% critical, source/error/charging resets and unchanged age/authenticated export; no hardware IO.");
    }
    private sealed class EmptyMemory:IPhysicalMemoryUsage {public double? ReadPercent()=>null;}
}
