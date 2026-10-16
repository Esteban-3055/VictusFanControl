using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace VictusFanControl.ExternalCooling;

public enum ExternalCoolingFailureKind { Connection, Authentication, Timeout, Protocol, Pairing, Internal }

public sealed record ExternalCoolingDiagnostics
{
    public ExternalCoolingConnectionState State { get; init; }=ExternalCoolingConnectionState.Disabled;
    public string Detail { get; init; }="Deshabilitado";
    public string? SessionId { get; init; }
    public long Sent { get; init; }
    public long Received { get; init; }
    public long Failures { get; init; }
    public long Connections { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public DateTimeOffset? LastSentUtc { get; init; }
    public DateTimeOffset? LastValidReceivedUtc { get; init; }
    public string? LastSent { get; init; }
    public string? LastReceived { get; init; }
    public DeviceStatus? Device { get; init; }
    public ExternalCoolingFailureKind? LastFailureKind { get; init; }
    public DateTimeOffset? LastFailureUtc { get; init; }
}

/// <summary>Independent network worker. No hardware/control dependencies, no UI callbacks.</summary>
public sealed class Esp32TcpClient
{
    private readonly object _sync=new();
    private ExternalCoolingDiagnostics _diagnostics=new();
    public ExternalCoolingDiagnostics Diagnostics {get{lock(_sync)return _diagnostics;}}
    private void Update(Func<ExternalCoolingDiagnostics,ExternalCoolingDiagnostics> change) {lock(_sync)_diagnostics=change(_diagnostics);}
    public void SetIdle(bool disabled)=>Update(d=>d with{State=disabled?ExternalCoolingConnectionState.Disabled:ExternalCoolingConnectionState.Disconnected,
        Detail=disabled?"Deshabilitado":"Desconectado",SessionId=null,Device=null});
    public void SetPairingFailure()=>Fail(ExternalCoolingFailureKind.Pairing);
    private void Fail(ExternalCoolingFailureKind kind)=>Update(d=>d with{State=ExternalCoolingConnectionState.Error,
        Detail="No se pudo comunicar; consulta el último fallo",Failures=d.Failures+1,Device=null,SessionId=null,
        LastFailureKind=kind,LastFailureUtc=DateTimeOffset.UtcNow});
    public static TimeSpan ReconnectDelay(int consecutiveFailures,int jitterMilliseconds=0)
    {
        if(consecutiveFailures<1||jitterMilliseconds is <0 or >249)throw new ArgumentOutOfRangeException();
        return TimeSpan.FromMilliseconds(Math.Min(30000,500*Math.Pow(2,Math.Min(consecutiveFailures,7)-1))+jitterMilliseconds);
    }

    public async Task RunAsync(ExternalCoolingSettings settings,byte[] pairingKey,ITelemetryExportSource source,CancellationToken token)
    {
        var failures=0;
        try
        {
            settings.Validate();
            if(settings.Mode==ExternalCoolingMode.Disabled){SetIdle(true);return;}
            while(!token.IsCancellationRequested)
            {
                Update(d=>d with{State=failures==0?ExternalCoolingConnectionState.Connecting:ExternalCoolingConnectionState.Reconnecting,
                    Detail="Conectando",Device=null,SessionId=null});
                var authenticating=false;
                try
                {
                    using var tcp=new TcpClient{NoDelay=true};
                    using(var connect=CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(5));
                        await tcp.ConnectAsync(settings.EffectiveHost,settings.Port,connect.Token).ConfigureAwait(false);
                    }
                    await using var wire=new FramedConnection(tcp.GetStream());
                    Update(d=>d with{State=ExternalCoolingConnectionState.Authenticating,Detail="Autenticando"});
                    authenticating=true;
                    using var session=await AuthenticatedSession.ClientAsync(wire,pairingKey,token).ConfigureAwait(false);
                    authenticating=false;
                    Update(d=>d with{Connections=d.Connections+1,SessionId=session.Id,Detail="Autenticado; esperando respuesta válida"});
                    await CommunicateAsync(wire,session,settings,source,token).ConfigureAwait(false);
                }
                catch(Exception ex)
                {
                    if(token.IsCancellationRequested)break;
                    if(Diagnostics.State==ExternalCoolingConnectionState.Connected)failures=0;
                    // Deliberately exclude peer data, keys, proofs, host strings and exception messages.
                    var kind=ex is OperationCanceledException?ExternalCoolingFailureKind.Timeout:
                        authenticating?ExternalCoolingFailureKind.Authentication:
                        ex is InvalidDataException or JsonException or FormatException?ExternalCoolingFailureKind.Protocol:
                        ex is SocketException or IOException?ExternalCoolingFailureKind.Connection:ExternalCoolingFailureKind.Internal;
                    Fail(kind);
                }
                if(!settings.AutoReconnect)break;
                failures=Diagnostics.State==ExternalCoolingConnectionState.Connected?1:Math.Min(failures+1,7);
                var delay=ReconnectDelay(failures,Random.Shared.Next(0,250));
                Update(d=>d with{State=ExternalCoolingConnectionState.Reconnecting,Detail="Esperando reconexión"});
                await Task.Delay(delay,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested) { }
        finally {CryptographicOperations.ZeroMemory(pairingKey);if(token.IsCancellationRequested)SetIdle(settings.Mode==ExternalCoolingMode.Disabled);}
    }
    private async Task CommunicateAsync(FramedConnection wire,AuthenticatedSession session,ExternalCoolingSettings settings,ITelemetryExportSource source,CancellationToken token)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        var sync=new object();var sentAt=new Dictionary<long,long>();long next=0,lastDeviceSequence=0,lastDeviceReceived=0;
        async Task Send()
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                // Pull the newest snapshot only at the instant of sending. Capacity is one in flight,
                // zero pending: a slow write never accumulates old telemetry.
                var value=source.Capture(DateTimeOffset.UtcNow);
                lock(sync)
                {
                    value=value with{ReplyToDeviceSequence=lastDeviceSequence>0?lastDeviceSequence:null};
                    if(lastDeviceSequence==0||Stopwatch.GetElapsedTime(lastDeviceReceived).TotalMilliseconds>=ExternalCoolingProtocol.RequestValidForMilliseconds)
                        value=value.WithoutRequest("AwaitingDevice");
                }
                if(settings.Mode!=ExternalCoolingMode.MonitorAndRequest)value=value.WithoutRequest();
                var frame=ExternalCoolingProtocol.Create("telemetry",session.Id,++next,value);
                ExternalCoolingProtocol.Telemetry(frame);
                frame=ExternalCoolingProtocol.Sign(frame,session.SendKey);
                lock(sync)
                {
                    sentAt[next]=Stopwatch.GetTimestamp();
                    foreach(var old in sentAt.Keys.Where(x=>x<next-15).ToArray())sentAt.Remove(old);
                }
                await wire.WriteAsync(frame,lifetime.Token).ConfigureAwait(false);
                Update(d=>d with{Sent=d.Sent+1,BytesSent=d.BytesSent+ExternalCoolingProtocol.Encode(frame).Length+1,
                    LastSentUtc=DateTimeOffset.UtcNow,LastSent=JsonSerializer.Serialize(value,ExternalCoolingProtocol.Json)});
            }while(await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false));
        }
        async Task Receive()
        {
            long previous=0,ack=0;
            while(true)
            {
                var frame=await wire.ReadAsync(lifetime.Token).ConfigureAwait(false);
                ExternalCoolingProtocol.Verify(frame,session.ReceiveKey,session.Id,previous);
                var status=ExternalCoolingProtocol.Status(frame);previous=frame.Sequence;
                long stamp;
                lock(sync)
                {
                    if(status.AckSequence<=ack||!sentAt.TryGetValue(status.AckSequence,out stamp))throw new InvalidDataException("Invalid acknowledgement.");
                }
                if(Stopwatch.GetElapsedTime(stamp)>TimeSpan.FromSeconds(5))throw new InvalidDataException("Expired acknowledgement.");
                ack=status.AckSequence;
                lifetime.Token.ThrowIfCancellationRequested();
                lock(sync){lastDeviceSequence=frame.Sequence;lastDeviceReceived=Stopwatch.GetTimestamp();}
                // Diagnostics hold only a projection of operational fields, never opaque peer extensions.
                var safe=status with{Extensions=new(){Slot1=null,Slot2=null}};
                Update(d=>d with{State=ExternalCoolingConnectionState.Connected,Detail="Autenticado; recepción válida",Device=safe,
                    Received=d.Received+1,BytesReceived=d.BytesReceived+ExternalCoolingProtocol.Encode(frame).Length+1,
                    LastValidReceivedUtc=DateTimeOffset.UtcNow,LastReceived=JsonSerializer.Serialize(safe,ExternalCoolingProtocol.Json)});
            }
        }
        var send=Send();var receive=Receive();
        try {await await Task.WhenAny(send,receive).ConfigureAwait(false);}
        finally
        {
            lifetime.Cancel();
            try{await Task.WhenAll(send,receive).ConfigureAwait(false);}catch(Exception)when(send.IsFaulted||receive.IsFaulted||lifetime.IsCancellationRequested){}
        }
    }
}
