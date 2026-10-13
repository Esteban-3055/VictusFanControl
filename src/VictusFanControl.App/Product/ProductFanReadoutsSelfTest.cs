namespace VictusFanControl.App;

internal static class ProductFanReadoutsSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var now=DateTimeOffset.UtcNow;var display=new ProductFanReadouts();var state=ProductHomeTelemetrySelfTest.StateAt(now);
        require(display.Cpu(state,now) is null&&display.Gpu(state,now) is null,"Startup fabricated an RPM before any measurement.");
        display.Observe(state,now);
        require(display.Cpu(state,now) is {Rpm:3000,Held:false}&&display.Gpu(state,now) is {Rpm:1500,Held:false},
            "Measured RPM were replaced by shared simulated request RPM.");
        var missing=state with{Snapshot=state.Snapshot! with{CpuFanRpm=null,GpuFanRpm=null}};
        display.Observe(missing,now);
        require(display.Cpu(missing,now) is {Rpm:3000,Held:true}&&display.Gpu(missing,now) is {Rpm:1500,Held:true}&&
            missing.Snapshot!.CpuFanRpm is null&&!missing.Snapshot.IsComplete&&ProductHomeTelemetry.CurrentDemand(missing,now) is null,
            "RPM gaps blanked the display or the presentation cache weakened control/graph admission.");
        require(display.Cpu(state,now.AddMinutes(20)) is {Rpm:3000,Held:true},"Extended delay erased or relabelled the last measurement as live.");
        var later=now.AddSeconds(1);var partial=ProductHomeTelemetrySelfTest.StateAt(later);
        partial=partial with{Snapshot=partial.Snapshot! with{CpuFanRpm=3200,GpuFanRpm=null}};
        display.Observe(partial,later);
        require(display.Cpu(partial,later) is {Rpm:3200,Held:false}&&display.Gpu(partial,later) is {Rpm:1500,Held:true},
            "One unavailable fan prevented the other fan's valid reading from updating.");
        display.Observe(state,later);
        require(display.Cpu(state,later) is {Rpm:3200,Held:true},"An older publication overwrote a newer RPM measurement.");
        var refreshed=ProductHomeTelemetrySelfTest.StateAt(now.AddSeconds(4));
        refreshed=refreshed with{Snapshot=refreshed.Snapshot! with{CpuFanRpm=3500,FanSampledAtUtc=now,
            FanSampleAgeMilliseconds=4000,FanMaximumAgeMilliseconds=10000}};
        display.Observe(refreshed,now.AddSeconds(4));
        require(display.Cpu(refreshed,now.AddSeconds(4)) is {Rpm:3200,Held:true},"A new CPU timestamp renewed an old fan acquisition.");
        foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,-100,10001})
        {
            var bad=ProductHomeTelemetrySelfTest.StateAt(now.AddSeconds(2));
            bad=bad with{Snapshot=bad.Snapshot! with{CpuFanRpm=invalid,GpuFanRpm=invalid}};
            display.Observe(bad,now.AddSeconds(2));
            require(display.Cpu(bad,now.AddSeconds(2)) is {Rpm:3200,Held:true},"An invalid measurement replaced the last valid RPM.");
        }
        var future=ProductHomeTelemetrySelfTest.StateAt(now.AddMinutes(1));display.Observe(future,now);
        require(display.Cpu(future,now) is {Rpm:3200,Held:true},"A future sample replaced valid RPM.");
        var futureFan=state with{Snapshot=state.Snapshot! with{FanSampledAtUtc=now.AddMinutes(1)}};display.Observe(futureFan,now);
        require(display.Cpu(futureFan,now) is {Rpm:3200,Held:true},"A future fan acquisition was admitted.");
        var failed=state with{Runtime="Failed"};display.Observe(failed,now);
        require(display.Cpu(failed,now) is {Rpm:3200,Held:true},"Failed runtime erased or certified retained RPM.");
        var stopped=ProductHomeTelemetrySelfTest.StateAt(now.AddSeconds(3));
        stopped=stopped with{Snapshot=stopped.Snapshot! with{CpuFanRpm=0,GpuFanRpm=0}};
        display.Observe(stopped,now.AddSeconds(3));
        require(display.Cpu(stopped,now.AddSeconds(3)) is {Rpm:0,Held:false}&&display.Gpu(stopped,now.AddSeconds(3)) is {Rpm:0,Held:false},
            "A genuine measured zero was ignored or remained marked as delayed after recovery.");
        Console.WriteLine("Fan readouts: PASS (independent last valid RPM, gaps, stale acquisition, partial updates, ordering, invalid/future samples, measured zero and unchanged control admission; presentation only).");
    }
}
