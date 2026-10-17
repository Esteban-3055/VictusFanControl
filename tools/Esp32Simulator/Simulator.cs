using System.Net.Sockets;
using System.Text;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.Esp32Simulator;

public enum SimulatorFault { None, Disconnect, Invalid, Partial, Delayed, Duplicate, Silence, Fragmented }
public sealed record SimulatorOptions(SimulatorFault Fault=SimulatorFault.None,int AfterMessages=3);

/// <summary>Development tool only. PWM is simulated locally, never converted by Windows product code.</summary>
public static class Simulator
{
    public static async Task HandleAsync(TcpClient tcp,byte[] key,SimulatorOptions options,Action<string,ExportTelemetry>? observed,CancellationToken token,SimulatorDeviceState? device=null)
    {
        device??=new();device.Disconnect();
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        var watchdog=device.WatchAsync(lifetime.Token);
        try
        {
            await using var wire=new FramedConnection(tcp.GetStream());
            using var session=await AuthenticatedSession.ServerAsync(wire,key,token);
            long previous=0,sequence=0;ProtocolFrame? last=null;
            ExternalCurveConfiguration? curve=null;
            var replyTimes=new Dictionary<long,long>();
            while(true)
            {
                var frame=await wire.ReadAsync(token);
                ExternalCoolingProtocol.Verify(frame,session.ReceiveKey,session.Id,previous);
                var telemetry=ExternalCoolingProtocol.Telemetry(frame);
                if(telemetry.ExternalConfiguration is{} configuration)
                {
                    if(curve?.Revision==configuration.Revision&&curve!=configuration)throw new InvalidDataException("External revision reused.");
                    curve=configuration;
                }
                previous=frame.Sequence;observed?.Invoke(session.Id,telemetry);
                if(previous>=options.AfterMessages)
                {
                    switch(options.Fault)
                    {
                        case SimulatorFault.Disconnect:return;
                        case SimulatorFault.Invalid:await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{invalid}\n"),token);return;
                        case SimulatorFault.Partial:
                            await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"protocolVersion\":1"),token);
                            await Task.Delay(TimeSpan.FromSeconds(10),token);return;
                        case SimulatorFault.Delayed:await Task.Delay(TimeSpan.FromSeconds(6),token);break;
                        case SimulatorFault.Duplicate:if(last is not null){await wire.WriteAsync(last,token);continue;}break;
                        // Keep consuming input so EOF releases the one-client server; send no replies.
                        case SimulatorFault.Silence:continue;
                    }
                }
                var remote=telemetry.SolicitudValida&&telemetry.ReplyToDeviceSequence is{} referencedReply&&replyTimes.TryGetValue(referencedReply,out var replyAt)&&
                    System.Diagnostics.Stopwatch.GetElapsedTime(replyAt).TotalMilliseconds<telemetry.ValidForMs;
                device.Apply(remote,telemetry.Solicitud,curve,telemetry.DemandaPct,telemetry.ModoSolicitado=="Manual");
                var operating=device.Snapshot;
                var status=new DeviceStatus
                {
                    AckSequence=previous,AmbientTempC=23.0+Math.Sin(previous/30.0),
                    ExternalCurveVersion=1,AppliedConfigRevision=operating.Revision,AppliedDuty=operating.Duty,ExternalRequest=operating.Request,
                    // An illustrative simulator policy, not a firmware requirement or Windows mapping.
                    AppliedPwmPct=operating.PwmPct,
                    Mode=operating.Mode,Communication=operating.Communication,Extensions=new(){Slot1=null,Slot2=null}
                };
                var reply=ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("deviceStatus",session.Id,++sequence,status),session.SendKey);last=reply;
                replyTimes[sequence]=System.Diagnostics.Stopwatch.GetTimestamp();
                foreach(var old in replyTimes.Keys.Where(k=>k<sequence-15).ToArray())replyTimes.Remove(old);
                if(options.Fault==SimulatorFault.Fragmented)
                {
                    var bytes=ExternalCoolingProtocol.Encode(reply).Concat(new byte[]{10}).ToArray();
                    for(int i=0;i<bytes.Length;i+=7)await tcp.GetStream().WriteAsync(bytes.AsMemory(i,Math.Min(7,bytes.Length-i)),token);
                }
                else await wire.WriteAsync(reply,token);
            }
        }
        finally{device.Disconnect();lifetime.Cancel();await watchdog.ConfigureAwait(false);}
    }
}
