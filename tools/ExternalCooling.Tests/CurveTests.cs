using System.Text.Json;
using VictusFanControl.ExternalCooling;
internal static partial class Program
{
    private static void CurveTests()
    {
        var c=new ExternalCurveConfiguration();c.Validate();
        var copy=JsonSerializer.Deserialize<ExternalCurveConfiguration>(JsonSerializer.Serialize(c,ExternalCoolingProtocol.Json),ExternalCoolingProtocol.Json)!;
        Require(copy==c,"Curve preferences did not roundtrip by value.");
        foreach(var bad in new[]{c with{Rise=0},c with{StartMs=3001},c with{RequestMaximum=0},c with{Points=[new(0,30),new(0,255)]},
            c with{Points=[new(1,30),new(100,255)]},c with{Points=[new(0,256),new(100,255)]},c with{Revision="bad"},c with{Points=null!}})
            Reject(bad.Validate,"Bad external config accepted.");
        var legacy=JsonSerializer.Deserialize<ExternalCoolingSettings>("{\"mode\":\"Disabled\",\"host\":\"victus-base.local\"}",ExternalCoolingProtocol.Json)!;
        legacy.Validate();Require(legacy.Curve==c,"Legacy settings did not acquire safe default curve.");
        var t=new ExportTelemetry{CapturedAtUtc=DateTimeOffset.UtcNow,ExternalConfiguration=c};
        var f=ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,t);
        Require(ExternalCoolingProtocol.Encode(f).Length<4096&&ExternalCoolingProtocol.Telemetry(f).ExternalConfiguration==c,"External config changed on wire.");
        var max=c with{Points=Enumerable.Range(0,16).Select(i=>new ExternalCurvePoint(i==15?100:i*6,30+i*15)).ToArray()};max.Validate();
        Require(ExternalCoolingProtocol.Encode(ExternalCoolingProtocol.Create("telemetry",f.SessionId,1,t with{ExternalConfiguration=max})).Length<4096,"Maximum external curve exceeds frame limit.");
        Console.WriteLine("PASS external curve schema/ranges, legacy settings, value equality and bounded wire size.");
    }
}
