using System.Net.Sockets;
using System.Text;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.Esp32Simulator;

public enum SimulatorFault { None, Disconnect, Invalid, Partial, Delayed, Duplicate, Silence, Fragmented }
public sealed record SimulatorOptions(SimulatorFault Fault=SimulatorFault.None,int AfterMessages=3);

/// <summary>Development tool only. PWM is simulated locally, never converted by Windows product code.</summary>
public static class Simulator
{
    public static async Task HandleAsync(TcpClient tcp,byte[] key,SimulatorOptions options,Action<string,ExportTelemetry>? observed,CancellationToken token)
    {
        await using var wire=new FramedConnection(tcp.GetStream());
        using var session=await AuthenticatedSession.ServerAsync(wire,key,token);
        long previous=0,sequence=0;ProtocolFrame? last=null;
        while(true)
        {
            var frame=await wire.ReadAsync(token);
            ExternalCoolingProtocol.Verify(frame,session.ReceiveKey,session.Id,previous);
            var telemetry=ExternalCoolingProtocol.Telemetry(frame);previous=frame.Sequence;observed?.Invoke(session.Id,telemetry);
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
                    case SimulatorFault.Silence:await Task.Delay(Timeout.InfiniteTimeSpan,token);return;
                }
            }
            var status=new DeviceStatus
            {
                AckSequence=previous,AmbientTempC=23.0+Math.Sin(previous/30.0),
                // An illustrative simulator policy, not a firmware requirement or Windows mapping.
                AppliedPwmPct=telemetry.SolicitudValida?Math.Clamp(telemetry.Solicitud!.Value,0,100):25,
                Mode=telemetry.SolicitudValida?"Remote":"Local",Communication="Ready",Extensions=new(){Slot1=null,Slot2=null}
            };
            var reply=ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("deviceStatus",session.Id,++sequence,status),session.SendKey);last=reply;
            if(options.Fault==SimulatorFault.Fragmented)
            {
                var bytes=ExternalCoolingProtocol.Encode(reply).Concat(new byte[]{10}).ToArray();
                for(int i=0;i<bytes.Length;i+=7)await tcp.GetStream().WriteAsync(bytes.AsMemory(i,Math.Min(7,bytes.Length-i)),token);
            }
            else await wire.WriteAsync(reply,token);
        }
    }
}
