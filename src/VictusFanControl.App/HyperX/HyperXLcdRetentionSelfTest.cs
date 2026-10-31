using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal static class HyperXLcdRetentionSelfTest
{
    internal static void Run(Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;var reader=new HyperXMonitor();
        var export=new TelemetryExportService(()=>new(),new EmptyMemory(),()=>reader.Snapshot);
        try
        {
            check(export.Capture(now) is{HyperxBatteryPct:null,HyperxDisplayRetained:false},"LCD history invented a startup battery");
            reader.SelectBatterySource("fixture://LCD");
            byte[] reply=Convert.FromHexString("21ff050cd0000000000000000000000000000000");
            reader.ApplyBatteryReport(reply,now,0);
            check(reader.Snapshot is{Percent:2,LastKnownPercent:2,LastKnownSampledAt:not null},"Real low report was not preserved as display history");
            var fresh=export.Capture(now);
            check(fresh is{HyperxBatteryPct:2,HyperxDisplayRetained:false,HyperxOriginalSampledAtUtc:null}&&fresh.HyperxSampledAtUtc==now,"Fresh acquisition lost its original wire timestamp");
            reader.ApplyEvent(0x64,3);
            var off=reader.Snapshot;
            check(off is{Powered:false,Percent:null,SampledAt:null,LastKnownPercent:2},"Power-off made history a live reading");
            var notices=new HyperXNotificationPolicy();
            for(int i=1;i<=4;i++)
            {
                var sent=now.AddHours(i);var frame=export.Capture(sent);
                check(frame is{HyperxBatteryPct:2,HyperxDisplayRetained:true}&&frame.HyperxSampledAtUtc==sent&&frame.HyperxOriginalSampledAtUtc==now,"Legacy LCD lost retained value or actual acquisition date");
                check(!frame.SolicitudValida&&frame.Solicitud is null&&frame.CpuTempC is null&&frame.SampledAtUtc is null,"LCD retention renewed control evidence");
                var decoded=ExternalCoolingProtocol.Telemetry(ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,frame));
                check(decoded==frame&&reader.Snapshot==off&&!reader.Snapshot.Fresh(sent),"Display projection changed monitor freshness or its wire metadata");
                check(notices.Evaluate(reader.Snapshot,new(),sent).Count==0,"Retained LCD value produced a new Windows alarm");
            }
            reader.SelectBatterySource(null);
            check(export.Capture(now.AddDays(1)) is{HyperxBatteryPct:2,HyperxDisplayRetained:true},"Receiver absence erased the last LCD value");
            reader.SelectBatterySource("fixture://LCD");reader.ApplyEvent(0x64,1);
            reply[3]=0x0e;reply[4]=0xd3;reader.ApplyBatteryReport(reply,now.AddDays(1),86_400_000);
            check(export.Capture(now.AddDays(1)) is{HyperxBatteryPct:56,HyperxDisplayRetained:false,HyperxOriginalSampledAtUtc:null},"Fresh return did not replace retained LCD history");
            var next=reader.Snapshot;
            reader.InvalidateBattery("fixture timeout");
            check(reader.Snapshot.Percent is null&&reader.Snapshot.LastKnownSampledAt==next.SampledAt,"Failure erased history or created a current value");
            // Genuine zero remains valid; a missing receiver/timeout never creates it.
            reader.ApplyBatteryReport(Convert.FromHexString("21ff050c80000000000000000000000000000000"),now.AddDays(1).AddMinutes(1),86_460_000);
            reader.InvalidateBattery("fixture timeout");
            var zero=export.Capture(now.AddDays(2));
            check(zero is{HyperxBatteryPct:0,HyperxDisplayRetained:true},"Genuine zero was lost during LCD retention");
            foreach(var bad in new[]{zero with{HyperxOriginalSampledAtUtc=null},zero with{HyperxOriginalSampledAtUtc=zero.CapturedAtUtc.AddSeconds(1)},zero with{HyperxDisplayRetained=false},zero with{HyperxSampledAtUtc=now}})
            {
                try{ExternalCoolingProtocol.Telemetry(ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,bad));throw new Exception("Invalid display history accepted");}
                catch(InvalidDataException){}
            }
        }
        finally{reader.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        var directory=Path.Combine(Path.GetTempPath(),"vfc-hyperx-history-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(directory,"last.json");
        try
        {
            var history=new HyperXDisplayHistory(1,0,now.AddDays(-1));history.Save(path);
            var loaded=HyperXDisplayHistory.Load(path,out var notice);
            check(loaded==history&&notice is null,"Saved genuine zero or original date changed on restart");
            var restarted=new HyperXMonitor();
            try
            {
                restarted.SeedDisplayHistory(loaded!);
                check(restarted.Snapshot is{ReceiverPresent:false,Percent:null,SampledAt:null,LastKnownPercent:0},"Saved history became a current receiver/acquisition");
                check(new HyperXNotificationPolicy().Evaluate(restarted.Snapshot,new(),now).Count==0,"Saved history invented a startup battery warning");
                check(new TelemetryExportService(()=>new(),new EmptyMemory(),()=>restarted.Snapshot).Capture(now) is{HyperxBatteryPct:0,HyperxDisplayRetained:true},"Restart lost the retained LCD value");
            }
            finally{restarted.DisposeAsync().AsTask().GetAwaiter().GetResult();}
            foreach(var bad in new[]{"broken","{\"SchemaVersion\":1,\"Percent\":101,\"SampledAtUtc\":\"2020-01-01T00:00:00Z\"}","{\"SchemaVersion\":1,\"Percent\":50,\"SampledAtUtc\":\"2099-01-01T00:00:00Z\"}"})
            {File.WriteAllText(path,bad);check(HyperXDisplayHistory.Load(path,out notice) is null&&notice is not null&&File.ReadAllText(path)==bad,"Invalid saved history was applied or destroyed");}
        }
        finally{if(File.Exists(path))File.Delete(path);if(Directory.Exists(directory))Directory.Delete(directory);}
        Console.WriteLine("PASS HyperX PC LCD retention: real 3280 mV replay, headset off/receiver loss/timeouts, original acquisition metadata, genuine zero, fresh return and no-history boot; no live notice/control renewal and no firmware change.");
    }
    private sealed class EmptyMemory:IPhysicalMemoryUsage {public double? ReadPercent()=>null;}
}
