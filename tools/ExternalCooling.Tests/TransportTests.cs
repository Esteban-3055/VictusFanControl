using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Esp32Simulator;

internal static partial class Program
{
    private sealed class Source : ITelemetryExportSource
    {
        public ExportTelemetry Capture(DateTimeOffset now)=>new(){CapturedAtUtc=now,CpuUsagePct=40,GpuUsagePct=80,RamUsagePct=50,
            ModoSolicitado="Manual",ModoEfectivo="Manual",Solicitud=54,SolicitudValida=true,CpuTargetLevel=54,GpuTargetLevel=52,RequestEvidence="WmiAcceptedSupervised"};
    }
    private static async Task Until(Func<bool> condition,int milliseconds=10000)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()) {if(clock.ElapsedMilliseconds>milliseconds)throw new TimeoutException("Test deadline expired.");await Task.Delay(20);}
    }
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _cancel=new();
        private readonly Task _run;
        public readonly List<(string Session,ExportTelemetry Telemetry)> Observations=[];
        public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
        public Server(byte[] key,SimulatorOptions options)
        {
            _listener.Start();
            _run=Task.Run(async()=>
            {
                try
                {
                    while(true)
                    {
                        using var tcp=await _listener.AcceptTcpClientAsync(_cancel.Token);
                        try{await Simulator.HandleAsync(tcp,key,options,(id,t)=>{lock(Observations)Observations.Add((id,t));},_cancel.Token);}
                        catch(Exception ex)when(ex is IOException or OperationCanceledException or System.Text.Json.JsonException or FormatException){}
                    }
                }
                catch(Exception ex)when(_cancel.IsCancellationRequested&&ex is OperationCanceledException or InvalidOperationException or SocketException){}
            });
        }
        public async ValueTask DisposeAsync(){_cancel.Cancel();_listener.Stop();await _run;_cancel.Dispose();}
    }
    private static async Task TransportTests()
    {
        var key=ExternalCoolingProtocol.ParsePairingKey(ExternalCoolingProtocol.NewPairingKey());
        // Coalesced NDJSON frames, exact size rejection and whole-message parsing.
        var f=ExternalCoolingProtocol.Create("hello",Guid.NewGuid().ToString("N"),0,new ClientHello{ClientNonce=ExternalCoolingProtocol.NewNonce()});
        var line=ExternalCoolingProtocol.Encode(f).Concat(new byte[]{10}).ToArray();
        await using(var wire=new FramedConnection(new MemoryStream(line.Concat(line).ToArray())))
        {Require((await wire.ReadAsync(default)).Type=="hello"&&(await wire.ReadAsync(default)).Type=="hello","Coalesced messages lost.");}
        foreach(var fault in new[]{SimulatorFault.Fragmented,SimulatorFault.Disconnect,SimulatorFault.Invalid,SimulatorFault.Partial,SimulatorFault.Delayed,SimulatorFault.Duplicate,SimulatorFault.Silence})
        {
            await using var server=new Server(key,new(fault,2));using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var client=new Esp32TcpClient();var task=client.RunAsync(new(){Mode=ExternalCoolingMode.MonitorOnly,UseSimulator=true,Port=server.Port,AutoReconnect=false},key.ToArray(),new Source(),cancel.Token);
            await Until(()=>client.Diagnostics.Received>=1);
            if(fault==SimulatorFault.Fragmented)
            {
                await Until(()=>client.Diagnostics.Received>=3);cancel.Cancel();
                lock(server.Observations)Require(server.Observations.All(o=>!o.Telemetry.SolicitudValida&&o.Telemetry.Solicitud is null),"MonitorOnly sent control.");
            }
            else await Until(()=>client.Diagnostics.Failures>0);
            cancel.Cancel();await task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(client.Diagnostics.LastSent?.Contains(Convert.ToBase64String(key),StringComparison.Ordinal)!=true,"Secret in diagnostics.");
            Console.WriteLine($"PASS TCP {fault}: sent={client.Diagnostics.Sent}, received={client.Diagnostics.Received}, failures={client.Diagnostics.Failures}");
        }
        await using(var server=new Server(key,new(SimulatorFault.None)))
        {
            var client=new Esp32TcpClient();using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var wrong=ExternalCoolingProtocol.ParsePairingKey(ExternalCoolingProtocol.NewPairingKey());
            var task=client.RunAsync(new(){Mode=ExternalCoolingMode.MonitorAndRequest,UseSimulator=true,Port=server.Port,AutoReconnect=false},wrong,new Source(),cancel.Token);
            await task.WaitAsync(TimeSpan.FromSeconds(7));
            Require(client.Diagnostics.Failures==1&&client.Diagnostics.Sent==0&&client.Diagnostics.Received==0,"Authentication failure enabled telemetry/control.");
        }
        await using(var server=new Server(key,new(SimulatorFault.Disconnect,2)))
        {
            var client=new Esp32TcpClient();using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var task=client.RunAsync(new(){Mode=ExternalCoolingMode.MonitorAndRequest,UseSimulator=true,Port=server.Port},key.ToArray(),new Source(),cancel.Token);
            await Until(()=>client.Diagnostics.Connections>=2);cancel.Cancel();await task.WaitAsync(TimeSpan.FromSeconds(2));
            lock(server.Observations)Require(server.Observations.Select(x=>x.Session).Distinct().Count()>=2,"Reconnect reused session.");
        }
        await using(var server=new Server(key,new()))
        await using(var manager=new ExternalCoolingManager(new Source(),()=>key.ToArray()))
        {
            var settings=new ExternalCoolingSettings{Mode=ExternalCoolingMode.MonitorAndRequest,UseSimulator=true,Port=server.Port};
            await manager.ConfigureAsync(settings);await Until(()=>manager.Diagnostics.Received>=1);
            var old=manager.Diagnostics.SessionId;
            var watch=Stopwatch.StartNew();manager.Suspend();Require(watch.ElapsedMilliseconds<100,"Suspend waited for network.");
            await manager.ResumeAsync();await Until(()=>manager.Diagnostics.State==ExternalCoolingConnectionState.Connected&&manager.Diagnostics.SessionId!=old);
            await manager.DisconnectAsync();Require(manager.Diagnostics.State==ExternalCoolingConnectionState.Disconnected,"Disconnect state wrong.");
            await Task.WhenAll(manager.ConfigureAsync(settings),manager.ConfigureAsync(settings));
            await Until(()=>manager.Diagnostics.State==ExternalCoolingConnectionState.Connected);
            await manager.ConfigureAsync(settings with{Mode=ExternalCoolingMode.Disabled});
            Require(manager.Diagnostics.State==ExternalCoolingConnectionState.Disabled,"Disable state wrong.");
        }
        var unused=new TcpListener(IPAddress.Loopback,0);unused.Start();var port=((IPEndPoint)unused.LocalEndpoint).Port;unused.Stop();
        var unavailable=new Esp32TcpClient();using(var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(7)))
        {await unavailable.RunAsync(new(){Mode=ExternalCoolingMode.MonitorOnly,UseSimulator=true,Port=port,AutoReconnect=false},key.ToArray(),new Source(),cancel.Token);Require(unavailable.Diagnostics.Failures==1,"Unreachable server not reported.");}
        Console.WriteLine("PASS authentication rejection, automatic reconnect/new sessions, manager concurrency, suspend/resume, clean shutdown, unavailable server.");
    }
}
