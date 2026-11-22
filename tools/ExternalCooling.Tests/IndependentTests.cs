using VictusFanControl.ExternalCooling;
using VictusFanControl.Esp32Simulator;

internal static partial class Program
{
    private static void IndependentTests()
    {
        var now=DateTimeOffset.UtcNow;
        var telemetry=new ExportTelemetry{CapturedAtUtc=now,ReplyToDeviceSequence=1,
            IndependentExternal=new("Fixed",0,null,null)};
        void Validate(ExportTelemetry t)=>ExternalCoolingProtocol.Telemetry(ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,t));
        Validate(telemetry);Validate(telemetry with{IndependentExternal=new("Fixed",255,null,null)});
        Validate(telemetry with{IndependentExternal=new("Curve",null,50,now)});
        foreach(var bad in new[]{new IndependentExternalRequest("Fixed",256,null,null),new("Fixed",null,null,null),
            new("Fixed",30,1,null),new("Curve",null,double.NaN,now),new("Curve",null,50,now.AddSeconds(-5)),
            new("Curve",null,50,now.AddTicks(1)),new("Curve",null,50,null),new("Other",0,null,null)})
            Reject(()=>Validate(telemetry with{IndependentExternal=bad}),"Invalid independent request accepted.");
        Reject(()=>Validate(telemetry with{ReplyToDeviceSequence=null}),"Independent request lacked a device reply.");
        Require(telemetry.WithoutRequest().IndependentExternal is null,"MonitorOnly retained independent authority.");
        var device=new SimulatorDeviceState();
        device.Apply(true,null,new(),independent:new("Fixed",0,null,null));
        Require(device.Snapshot is{Mode:"Remote",Duty:0},"Fixed zero was confused with remote withdrawal.");
        device.Apply(false,null,new());Require(device.Snapshot.Mode=="Local","Remote withdrawal did not return to Local.");
    }
    private sealed class IndependentSource : ITelemetryExportSource
    {public ExportTelemetry Capture(DateTimeOffset now)=>new(){CapturedAtUtc=now,IndependentExternal=new("Fixed",87,null,null)};}
    private static async Task IndependentTransportTests()
    {
        var key=ExternalCoolingProtocol.ParsePairingKey(ExternalCoolingProtocol.NewPairingKey());
        await using var server=new Server(key,new(SimulatorFault.None,int.MaxValue));
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(12));var client=new Esp32TcpClient();
        var task=client.RunAsync(new(){Mode=ExternalCoolingMode.MonitorAndRequest,UseSimulator=true,Port=server.Port,AutoReconnect=false},key,new IndependentSource(),stop.Token);
        try
        {
            await Until(()=>client.Diagnostics.Device is{Mode:"Remote",IndependentControlVersion:1,AppliedDuty:87});
            lock(server.Observations)Require(server.Observations.All(o=>!o.Telemetry.SolicitudValida)&&
                server.Observations.Any(o=>o.Telemetry.IndependentExternal is not null),"Independent transport fabricated internal fan authority.");
        }
        finally{stop.Cancel();await task;}
        Console.WriteLine("PASS independent protocol and authenticated TCP control; fixed zero, full duty, source age, device capability and monitor withdrawal.");
    }
}
