using System.Text.Json;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal static class HyperXSelfTest
{
    internal static void Run()
    {
        static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        var decoded=HyperXSnapshot.Decode(Convert.FromHexString("21ff050ed3000000000000000000000000000000"));
        Check(decoded is{Millivolts:3795,Percent:56.25,Charging:false},"Recorded USB voltage or continuous reference estimate changed");
        HyperXBatteryEstimatorSelfTest.Run(Check);
        Check(HyperXSnapshot.Decode(new byte[20]) is null&&HyperXSnapshot.Decode(new byte[5]) is null,"Unknown replies invented battery");
        Check(HyperXSnapshot.Decode(Convert.FromHexString("21ff051020000000000000000000000000000000")) is{Percent:null,Charging:true},"Charging must not invent 100%");
        var now=DateTimeOffset.UtcNow;var sample=new HyperXSnapshot{ReceiverPresent=true,SampledAt=now,Percent=20};
        Check(sample.Fresh(now.AddSeconds(179))&&!sample.Fresh(now.AddSeconds(180))&&!sample.Fresh(now.AddMilliseconds(-1)),"HyperX independent freshness bounds");
        var cfg=new HyperXSettings();var policy=new HyperXNotificationPolicy();
        Check(policy.Evaluate(sample,cfg,now).Count==1&&policy.Evaluate(sample,cfg,now).Count==0,"Low alarm repeated");
        Check(policy.Evaluate(sample with{Percent=10},cfg,now).Single().Title.Contains("crítica"),"Critical alarm lost or duplicate low");
        Check(policy.Evaluate(new(),cfg,now).Count==0&&policy.Evaluate(sample with{Percent=10},cfg,now).Count==0,"Missing data rearmed critical alarm");
        policy.Evaluate(sample with{Percent=30},cfg,now);
        Check(policy.Evaluate(sample,cfg,now).Count==1,"Recovered low threshold did not rearm");
        Check(policy.Evaluate(sample with{Percent=100},cfg,now).Count==1&&policy.Evaluate(sample with{Percent=100},cfg,now).Count==0,"Full threshold repeated");
        Check(policy.Evaluate(sample with{Percent=55,Muted=true,MuteEventVersion=1,MuteEventAt=now},cfg,now).Count==1,"First observed mute event lost");
        Check(policy.Evaluate(sample with{Percent=55,Muted=false,MuteEventVersion=2,MuteEventAt=now},cfg,now).Count==1,"Unmute lost");
        Check(policy.Evaluate(sample with{Percent=55,Muted=false,MuteEventVersion=3,MuteEventAt=now},cfg,now).Count==0,"Duplicate unmute alarm");
        Check(new HyperXNotificationPolicy().Evaluate(sample with{Percent=10,Muted=true,MuteEventVersion=1,MuteEventAt=now},cfg with{LowEnabled=false,CriticalEnabled=false,FullEnabled=false,MuteEnabled=false},now).Count==0,"Disabled notifications escaped");
        Check(policy.Evaluate(sample with{SampledAt=now.AddSeconds(-180),Percent=1},cfg,now).Count==0,"Stale battery notified");
        var path=Path.Combine(Path.GetTempPath(),"vfc-hyperx-fixture-"+Guid.NewGuid().ToString("N"),"settings.json");
        try
        {
            var settings=cfg with{LowPercent=30,CriticalPercent=15,FullPercent=95,MuteEnabled=false,LcdLowSeconds=8,LcdLowBlinkCycles=4};settings.Save(path);
            Check(HyperXSettings.Load(path,out var notice)==settings&&notice is null,"HyperX persistence changed");
            File.WriteAllText(path,"broken");Check(HyperXSettings.Load(path,out notice)==cfg&&notice is not null&&File.ReadAllText(path)=="broken","Invalid settings evidence lost");
            foreach(var critical in new[]{0,100})
            {
                var endpoint=cfg with{CriticalPercent=critical};endpoint.Save(path);
                Check(HyperXSettings.Load(path,out notice)==endpoint&&notice is null,"Critical endpoint changed on save/reload");
                Check(endpoint.DisplayConfiguration().CriticalPercent==critical,"Critical endpoint clamped in authenticated export");
            }
            foreach(var critical in new[]{-1,101})
                try{(cfg with{CriticalPercent=critical}).Save(path);throw new Exception("Invalid critical threshold accepted");}catch(InvalidDataException){}
            try{(cfg with{LowPercent=99,FullPercent=99}).Save(path);throw new Exception("Invalid low/full ordering accepted");}catch(InvalidDataException){}
            Check(new HyperXNotificationPolicy().Evaluate(sample with{Percent=55},cfg with{CriticalPercent=100},now).Single().Title.Contains("crítica"),"Overlapping critical threshold lost priority");
            Check(new HyperXNotificationPolicy().Evaluate(sample with{Percent=0},cfg with{CriticalPercent=0},now).Single().Title.Contains("crítica"),"Critical zero was treated as null");
        }
        finally{if(Directory.Exists(Path.GetDirectoryName(path)))Directory.Delete(Path.GetDirectoryName(path)!,true);}
        var reader=new HyperXMonitor();reader.ApplyEvent(0x66,0);Check(reader.Snapshot.Muted is null,"Unknown report invented unmute");
        reader.ApplyEvent(0x65,4);reader.ApplyEvent(0x65,0);
        Check(reader.DrainMuteEvents().Select(s=>s.Muted).SequenceEqual(new bool?[]{true,false}),"Coalesced UI lost mute edges");
        reader.ApplyEvent(0x64,3);Check(reader.Snapshot.Powered==false&&reader.Snapshot.Muted is null,"Power-off retained mute");
        reader.DisposeAsync().AsTask().GetAwaiter().GetResult();reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var heldCleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock=System.Diagnostics.Stopwatch.StartNew();
        HyperXMonitor.DrainOptionalAsync(()=>heldCleanup.Task,TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult();
        Check(clock.Elapsed<TimeSpan.FromSeconds(2),"Optional USB driver held application shutdown");heldCleanup.SetResult();
        HyperXMonitor.DrainOptionalAsync(()=>Task.FromException(new IOException("fixture only")),TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var t=new TelemetryExportService(()=>new(){Runtime="Unavailable",FanAuthority="Unknown"},new EmptyMemory(),()=>sample);
        var export=t.Capture(now) with{HyperxDisplay=new HyperxDisplayConfiguration{LowDurationMs=8000,LowBlinkCycles=4}};Check(export.HyperxBatteryPct==20&&export.HyperxSampledAtUtc==now&&!export.SolicitudValida&&export.CpuTempC is null,"HyperX acquired fan authority or depended on CPU freshness");
        Check(t.Capture(now.AddSeconds(180)).HyperxBatteryPct is null,"Export renewed stale battery acquisition");
        var frame=ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,export);
        Check(ExternalCoolingProtocol.Telemetry(frame).HyperxBatteryPct==20&&ExternalCoolingProtocol.Telemetry(frame).HyperxDisplay?.LowBlinkCycles==4,"HyperX protocol roundtrip failed");
        foreach(var bad in new[]{export with{HyperxDisplay=new HyperxDisplayConfiguration{LowDurationMs=1000,LowBlinkCycles=20}},export with{HyperxBatteryPct=101},export with{HyperxSampledAtUtc=null},export with{HyperxSampledAtUtc=now.AddSeconds(1)}})
            try{ExternalCoolingProtocol.Telemetry(ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,bad));throw new Exception("Bad HyperX field admitted");}catch(InvalidDataException){}
        Console.WriteLine("PASS HyperX: recorded USB decoding, independent 180s freshness, nulls, thresholds/hysteresis/dedup, mute edges, persisted settings, inert authenticated export; zero hardware IO.");
    }
    private sealed class EmptyMemory:IPhysicalMemoryUsage {public double? ReadPercent()=>null;}
    internal static async Task ProbeAsync(int seconds)
    {
        await using var reader=new HyperXMonitor();reader.Start();string? previous=null;
        for(int i=0;i<seconds;i++)
        {
            var s=reader.Snapshot;
            var json=JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,status=s.Status,s.ReceiverPresent,s.Percent,s.Millivolts,s.Charging,s.Muted,s.Powered,s.SampledAt,s.MuteEventVersion});
            var identity=JsonSerializer.Serialize(new{s.Status,s.Percent,s.Millivolts,s.Charging,s.Muted,s.Powered,s.SampledAt,s.MuteEventVersion});
            if(identity!=previous){Console.WriteLine(json);previous=identity;}
            await Task.Delay(1000);
        }
        Console.WriteLine("PASS bounded HyperX probe completed; no audio settings changed.");
    }
}
