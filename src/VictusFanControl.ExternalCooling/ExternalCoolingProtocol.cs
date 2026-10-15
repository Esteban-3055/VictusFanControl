using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VictusFanControl.ExternalCooling;

public sealed record ProtocolFrame
{
    public required int ProtocolVersion { get; init; }
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public required long Sequence { get; init; }
    public required JsonElement Payload { get; init; }
    public required string Mac { get; init; }
}
public sealed record ClientHello { public required string ClientNonce { get; init; } }
public sealed record ServerChallenge
{
    public required string ServerNonce { get; init; }
    public required string Proof { get; init; }
}
public sealed record AuthenticationProof { public required string Proof { get; init; } }
public sealed record DeviceExtensions
{
    // Reserved, opaque and inert, including when future peers populate them.
    public required JsonElement? Slot1 { get; init; }
    public required JsonElement? Slot2 { get; init; }
}
public sealed record DeviceStatus
{
    public required long AckSequence { get; init; }
    public required double? AmbientTempC { get; init; }
    public required double? AppliedPwmPct { get; init; }
    public required string Mode { get; init; }
    public required string Communication { get; init; }
    public required DeviceExtensions Extensions { get; init; }
}

public static class ExternalCoolingProtocol
{
    public const int Version=1, MaximumFrameBytes=4096, RequestValidForMilliseconds=2000;
    public static JsonSerializerOptions Json { get; }=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase, MaxDepth=8,
        Converters={new JsonStringEnumConverter()}
    };
    public static string NewNonce()=>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static byte[] ParsePairingKey(string key)
    {
        var bytes=Convert.FromBase64String(key);
        if(bytes.Length!=32)throw new ArgumentException("El emparejamiento requiere una clave aleatoria de 32 bytes en Base64.");
        return bytes;
    }
    public static string NewPairingKey()=>NewNonce();
    public static ProtocolFrame Create<T>(string type,string session,long sequence,T payload)=>new()
    {ProtocolVersion=Version,Type=type,SessionId=session,Sequence=sequence,Payload=JsonSerializer.SerializeToElement(payload,Json),Mac=""};
    public static byte[] Encode(ProtocolFrame frame)
    {
        // Preserve the exact payload bytes used by the MAC (including JSON escaping).
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();writer.WriteNumber("protocolVersion",frame.ProtocolVersion);
            writer.WriteString("type",frame.Type);writer.WriteString("sessionId",frame.SessionId);
            writer.WriteNumber("sequence",frame.Sequence);writer.WritePropertyName("payload");
            writer.WriteRawValue(frame.Payload.GetRawText());writer.WriteString("mac",frame.Mac);writer.WriteEndObject();
        }
        var bytes=stream.ToArray();
        if(bytes.Length>MaximumFrameBytes)throw new InvalidDataException("Frame too large.");
        return bytes;
    }
    public static ProtocolFrame Decode(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length is 0 or >MaximumFrameBytes)throw new InvalidDataException("Frame size invalid.");
        using var doc=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=8});
        CheckDuplicates(doc.RootElement);
        var f=doc.RootElement.Deserialize<ProtocolFrame>(Json)??throw new InvalidDataException("Missing frame.");
        if(f.ProtocolVersion!=Version||!Guid.TryParseExact(f.SessionId,"N",out _)||f.Sequence<0||
            f.Type is not("hello" or "challenge" or "authenticate" or "authenticated" or "telemetry" or "deviceStatus")||
            f.Payload.ValueKind!=JsonValueKind.Object||f.Mac is null||f.Mac.Length>44)
            throw new InvalidDataException("Unsupported or invalid envelope.");
        return f;
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in element.EnumerateObject())
            {if(!keys.Add(p.Name))throw new InvalidDataException("Duplicate JSON field.");CheckDuplicates(p.Value);}
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())CheckDuplicates(item);
    }
    public static T Payload<T>(ProtocolFrame frame)=>frame.Payload.Deserialize<T>(Json)??throw new InvalidDataException("Missing payload.");
    public static ExportTelemetry Telemetry(ProtocolFrame frame)
    {
        if(frame.Type!="telemetry")throw new InvalidDataException("Expected telemetry.");
        foreach(var key in new[]{"validForMs","capturedAtUtc","sampledAtUtc","decisionAtUtc","cpuUsagePct","gpuUsagePct","ramUsagePct","demandaPct",
            "solicitud","solicitudValida","modoSolicitado","modoEfectivo","cpuTempC","gpuTempC","powerSource","requestEvidence",
            "cpuTargetLevel","gpuTargetLevel","cpuTempSampledAtUtc","gpuTempSampledAtUtc","ramSampledAtUtc"})
            if(!frame.Payload.TryGetProperty(key,out _))throw new InvalidDataException("Missing telemetry field.");
        var t=Payload<ExportTelemetry>(frame);
        if(t.ValidForMs!=RequestValidForMilliseconds||t.CapturedAtUtc==default||t.ModoSolicitado is not("Firmware" or "Manual" or "Automatic")||
            t.ModoEfectivo is not("Firmware" or "Manual" or "Automatic" or "Transition" or "Interrupted" or "Unavailable")||
            t.PowerSource is not("AC" or "Battery" or "Unknown")||t.RequestEvidence is not("Unavailable" or "MonitorOnly" or "WmiAcceptedSupervised"))
            throw new InvalidDataException("Invalid telemetry enum/date.");
        void Range(double? value,double min,double max)
        {if(value.HasValue&&ExportTelemetry.Range(value,min,max) is null)throw new InvalidDataException("Invalid measurement.");}
        foreach(var value in new[]{t.CpuUsagePct,t.GpuUsagePct,t.RamUsagePct,t.DemandaPct})Range(value,0,100);
        Range(t.CpuTempC,-20,130);Range(t.GpuTempC,-20,130);
        foreach(var level in new[]{t.Solicitud,t.CpuTargetLevel,t.GpuTargetLevel})
            if(level is <0 or >65535)throw new InvalidDataException("Invalid logical/target level.");
        if(t.SolicitudValida != t.Solicitud.HasValue || (!t.SolicitudValida&&(t.CpuTargetLevel.HasValue||t.GpuTargetLevel.HasValue))||
            (t.SolicitudValida&&(t.ModoSolicitado is not("Manual" or "Automatic")||t.ModoEfectivo!=t.ModoSolicitado||
                t.CpuTargetLevel is null||t.GpuTargetLevel is null||t.RequestEvidence!="WmiAcceptedSupervised"))||
            (t.ModoSolicitado!="Automatic"&&t.DemandaPct.HasValue))
            throw new InvalidDataException("Inconsistent control validity.");
        foreach(var at in new[]{t.SampledAtUtc,t.DecisionAtUtc,t.CpuTempSampledAtUtc,t.GpuTempSampledAtUtc,t.RamSampledAtUtc})
            if(at>t.CapturedAtUtc)throw new InvalidDataException("Future acquisition.");
        return t;
    }
    public static DeviceStatus Status(ProtocolFrame frame)
    {
        if(frame.Type!="deviceStatus")throw new InvalidDataException("Expected deviceStatus.");
        var d=Payload<DeviceStatus>(frame);
        if(d.AckSequence<=0||d.Mode is not("Local" or "Remote")||d.Communication is not("Ready" or "Timeout" or "Fault")||d.Extensions is null||
            d.AmbientTempC.HasValue&&ExportTelemetry.Range(d.AmbientTempC,-40,100) is null||
            d.AppliedPwmPct.HasValue&&ExportTelemetry.Range(d.AppliedPwmPct,0,100) is null)
            throw new InvalidDataException("Invalid device status.");
        return d;
    }
    public static void ValidateNonce(string nonce)
    {if(Convert.FromBase64String(nonce).Length!=32)throw new InvalidDataException("Invalid nonce.");}
    public static string Proof(byte[] key,string role,string session,string clientNonce,string serverNonce)
        =>Mac(key,$"VictusExternalCooling/v1/auth\n{role}\n{session}\n{clientNonce}\n{serverNonce}");
    public static byte[] SessionKey(byte[] key,string direction,string session,string clientNonce,string serverNonce)
        =>Convert.FromBase64String(Proof(key,direction,session,clientNonce,serverNonce));
    private static string Mac(byte[] key,string data)=>Convert.ToBase64String(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(data)));
    private static string Transcript(ProtocolFrame f)=>$"VictusExternalCooling/v1/frame\n{f.Type}\n{f.SessionId}\n{f.Sequence.ToString(CultureInfo.InvariantCulture)}\n{f.Payload.GetRawText()}";
    public static ProtocolFrame Sign(ProtocolFrame frame,byte[] key)=>frame with{Mac=Mac(key,Transcript(frame))};
    public static bool EqualProof(string expected,string received)
    {
        try { var a=Convert.FromBase64String(expected);var b=Convert.FromBase64String(received);return a.Length==32&&b.Length==32&&CryptographicOperations.FixedTimeEquals(a,b); }
        catch(FormatException){return false;}
    }
    public static void Verify(ProtocolFrame frame,byte[] key,string session,long previousSequence)
    {
        if(frame.SessionId!=session||frame.Sequence<=previousSequence||!EqualProof(Mac(key,Transcript(frame)),frame.Mac))
            throw new InvalidDataException("Session, sequence or authentication invalid.");
    }
}
