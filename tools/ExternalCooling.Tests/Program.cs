using System.Text;
using System.Text.Json;
using VictusFanControl.ExternalCooling;

internal static partial class Program
{
    internal static void Require(bool ok,string message) { if(!ok)throw new InvalidOperationException(message); }
    internal static void Reject(Action action,string message)
    {try{action();}catch(Exception ex)when(ex is InvalidDataException or JsonException or FormatException or ArgumentException){return;}throw new InvalidOperationException(message);}
    private static async Task Main()
    {
        ProtocolTests();
        await TransportTests();
        Console.WriteLine("PASS ExternalCooling tests (protocol and real loopback TCP).");
    }
    private static void ProtocolTests()
    {
        var session=Guid.NewGuid().ToString("N");var key=ExternalCoolingProtocol.ParsePairingKey(ExternalCoolingProtocol.NewPairingKey());
        var t=new ExportTelemetry{CapturedAtUtc=DateTimeOffset.UtcNow,CpuUsagePct=0,RamUsagePct=100};
        var f=ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",session,1,t),key);
        var bytes=ExternalCoolingProtocol.Encode(f);var decoded=ExternalCoolingProtocol.Decode(bytes);
        ExternalCoolingProtocol.Verify(decoded,key,session,0);
        Require(ExternalCoolingProtocol.Telemetry(decoded)==t,"Roundtrip changed nulls or real zeroes.");
        Reject(()=>ExternalCoolingProtocol.Verify(decoded,key,session,1),"Duplicate sequence accepted.");
        Reject(()=>ExternalCoolingProtocol.Verify(decoded,key,Guid.NewGuid().ToString("N"),0),"Old session accepted.");
        Reject(()=>ExternalCoolingProtocol.Verify(decoded with{Type="deviceStatus"},key,session,0),"Tampered type accepted.");
        Reject(()=>ExternalCoolingProtocol.Verify(decoded with{Sequence=2},key,session,0),"Tampered sequence accepted.");
        Reject(()=>ExternalCoolingProtocol.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"protocolVersion\":1","\"protocolVersion\":2"))),"Unknown version accepted.");
        Reject(()=>ExternalCoolingProtocol.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"protocolVersion\":1","\"protocolVersion\":1,\"protocolVersion\":1"))),"Duplicate field accepted.");
        Reject(()=>ExternalCoolingProtocol.Decode(new byte[4097]),"Oversized input accepted.");
        Reject(()=>ExternalCoolingProtocol.Decode(bytes[..^1]),"Incomplete JSON accepted.");
        Reject(()=>ExternalCoolingProtocol.Decode(ExternalCoolingProtocol.Encode(f with{Mac=null!})),"Null MAC accepted.");
        Reject(()=>ExternalCoolingProtocol.ValidateNonce(null!),"Null nonce accepted.");
        Require(!ExternalCoolingProtocol.EqualProof("x",null!),"Null proof accepted.");
        Reject(()=>ExternalCoolingProtocol.Telemetry(f with{Payload=JsonSerializer.SerializeToElement(new{capturedAtUtc=DateTimeOffset.UtcNow})}),"Missing fields accepted.");
        foreach(var bad in new[]{t with{CpuUsagePct=101},t with{GpuUsagePct=-1},t with{CpuTempC=999},t with{Solicitud=30},
            t with{ModoSolicitado="other"},t with{PowerSource="other"},t with{DemandaPct=30},t with{SampledAtUtc=t.CapturedAtUtc.AddSeconds(1)}})
            Reject(()=>ExternalCoolingProtocol.Telemetry(ExternalCoolingProtocol.Create("telemetry",session,1,bad)),"Invalid telemetry accepted.");
        var status=new DeviceStatus{AckSequence=1,AmbientTempC=23,AppliedPwmPct=0,Mode="Local",Communication="Ready",Extensions=new(){Slot1=null,Slot2=null}};
        var df=ExternalCoolingProtocol.Create("deviceStatus",session,1,status);
        Require(ExternalCoolingProtocol.Status(ExternalCoolingProtocol.Decode(ExternalCoolingProtocol.Encode(df))).Extensions.Slot1 is null,"Reserved null lost.");
        var future=status with{Extensions=new(){Slot1=JsonSerializer.SerializeToElement(new{future=42}),Slot2=null}};
        Require(ExternalCoolingProtocol.Status(ExternalCoolingProtocol.Create("deviceStatus",session,1,future)).Extensions.Slot1.HasValue,"Opaque extension rejected.");
        Reject(()=>ExternalCoolingProtocol.Status(ExternalCoolingProtocol.Create("deviceStatus",session,1,status with{AppliedPwmPct=101})),"Invalid PWM accepted.");
        var cn=ExternalCoolingProtocol.NewNonce();var sn=ExternalCoolingProtocol.NewNonce();
        Require(!ExternalCoolingProtocol.EqualProof(ExternalCoolingProtocol.Proof(key,"server",session,cn,sn),ExternalCoolingProtocol.Proof(key,"client",session,cn,sn)),"Reflection accepted.");
        Require(!Encoding.UTF8.GetString(bytes).Contains(Convert.ToBase64String(key),StringComparison.Ordinal),"Pairing key leaked.");
    }
}
