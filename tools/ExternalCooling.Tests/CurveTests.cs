using System.Text.Json;
using VictusFanControl.ExternalCooling;
internal static partial class Program
{
    private static void CurveTests()
    {
        var c=new ExternalCurveConfiguration();c.Validate();
        var smooth=c.WithMaximumSmoothing();smooth.Validate();
        Require(smooth==c with{Rise=1,Fall=1,FilterMs=10000,Hysteresis=3,DownHoldMs=30000,StartMs=0,StartOutput=0},"Maximum smoothing changed the curve, calibration or Manual output.");
        Require(smooth.WithMaximumSmoothing()==smooth&&c.StartOutput==255,"Smoothing must be opt-in and idempotent.");
        var copy=JsonSerializer.Deserialize<ExternalCurveConfiguration>(JsonSerializer.Serialize(c,ExternalCoolingProtocol.Json),ExternalCoolingProtocol.Json)!;
        Require(copy==c,"Curve preferences did not roundtrip by value.");
        var zero=c with{Points=[new(0,0),new(20,0),new(100,255)],ManualOutput=0,StartOutput=0};zero.Validate();
        Require(zero.Evaluate(0)==0&&zero.Evaluate(20)==0&&zero.Evaluate(100)==255,"Zero plateau preview mapping.");
        Require(zero.RequiresFullOutputRange&&!zero.CanSendTo(null),"Zero sent without negotiated capability.");
        var legacyDevice=new DeviceStatus{ExternalCurveVersion=1,AckSequence=1,AmbientTempC=24,AppliedPwmPct=0,
            Mode="Remote",Communication="Ready",Extensions=new(){Slot1=null,Slot2=null}};
        Require(c.CanSendTo(legacyDevice)&&!zero.CanSendTo(legacyDevice),"Legacy firmware range negotiation.");
        var fullDevice=legacyDevice with{ExternalMinimumOutput=0,AppliedDuty=0,ExternalRequest=0};
        Require(zero.CanSendTo(fullDevice),"Full range firmware not recognized.");
        Require(ExternalCoolingProtocol.Status(ExternalCoolingProtocol.Create("deviceStatus",Guid.NewGuid().ToString("N"),1,fullDevice)).AppliedDuty==0,"Zero status became null.");
        Reject(()=>ExternalCoolingProtocol.Status(ExternalCoolingProtocol.Create("deviceStatus",Guid.NewGuid().ToString("N"),1,fullDevice with{ExternalMinimumOutput=-1})),"Invalid capability accepted.");
        foreach(var bad in new[]{c with{Rise=0},c with{StartMs=3001},c with{RequestMaximum=0},c with{Points=[new(0,30),new(0,255)]},
            c with{Points=[new(1,30),new(100,255)]},c with{Points=[new(0,256),new(100,255)]},c with{Points=[new(0,-1),new(100,255)]},
            c with{ManualOutput=-1},c with{StartOutput=-1},c with{Revision="bad"},c with{Points=null!}})
            Reject(bad.Validate,"Bad external config accepted.");
        var legacy=JsonSerializer.Deserialize<ExternalCoolingSettings>("{\"mode\":\"Disabled\",\"host\":\"victus-base.local\"}",ExternalCoolingProtocol.Json)!;
        legacy.Validate();Require(legacy.Curve==c,"Legacy settings did not acquire safe default curve.");
        var t=new ExportTelemetry{CapturedAtUtc=DateTimeOffset.UtcNow,ExternalConfiguration=c};
        var f=ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,t);
        Require(ExternalCoolingProtocol.Encode(f).Length<4096&&ExternalCoolingProtocol.Telemetry(f).ExternalConfiguration==c,"External config changed on wire.");
        foreach(var key in new[]{"schemaVersion","revision","points","input","requestMinimum","requestMaximum","manualOutput","rise","fall","filterMs","hysteresis","downHoldMs","startMs","startOutput"})
        {
            var payload=System.Text.Json.Nodes.JsonNode.Parse(f.Payload.GetRawText())!;
            payload["externalConfiguration"]!.AsObject().Remove(key);
            Reject(()=>ExternalCoolingProtocol.Telemetry(f with{Payload=JsonSerializer.SerializeToElement(payload)}),"Missing required config field accepted: "+key);
        }
        var invalid=f with{Payload=JsonSerializer.SerializeToElement(t with{ExternalConfiguration=c with{Rise=0}},ExternalCoolingProtocol.Json)};
        try{ExternalCoolingProtocol.Telemetry(invalid);throw new InvalidOperationException("Invalid config admitted");}
        catch(InvalidDataException){} // Network decoders must reject without escaping the production server loop.
        var max=c with{Points=Enumerable.Range(0,16).Select(i=>new ExternalCurvePoint(i==15?100:i*6,30+i*15)).ToArray()};max.Validate();
        Require(ExternalCoolingProtocol.Encode(ExternalCoolingProtocol.Create("telemetry",f.SessionId,1,t with{ExternalConfiguration=max})).Length<4096,"Maximum external curve exceeds frame limit.");
        Console.WriteLine("PASS external curve schema/ranges, legacy settings, value equality and bounded wire size.");
    }
}
