using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Esp32Simulator;

internal static partial class Program
{
    private sealed class Source : ITelemetryExportSource
    {
        public ExportTelemetry Capture(DateTimeOffset now)=>new(){CapturedAtUtc=now,SampledAtUtc=now,CpuTempSampledAtUtc=now,GpuTempSampledAtUtc=now,CpuTempC=55,GpuTempC=50,CpuUsagePct=40,GpuUsagePct=80,RamUsagePct=50,
            ModoSolicitado="Manual",ModoEfectivo="Manual",Solicitud=54,SolicitudValida=true,CpuTargetLevel=54,GpuTargetLevel=52,RequestEvidence="WmiAcceptedSupervised"};
    }
    private static async Task Until(Func<bool> condition,int milliseconds=10000)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()) {if(clock.ElapsedMilliseconds>milliseconds)throw new TimeoutException("Test deadline expired.");await Task.Delay(20);}
    }
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancel=new();
        private readonly Task _run;
        public readonly List<(string Session,ExportTelemetry Telemetry)> Observations=[];
        public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
        public Server(byte[] key,SimulatorOptions options,int port=0)
        {
            _listener=new(IPAddress.Loopback,port);
            _listener.Start();
            _run=SimulatorServer.RunAsync(_listener,key,options,(id,t)=>{lock(Observations)Observations.Add((id,t));},_=>{},_cancel.Token);
        }
        public async ValueTask DisposeAsync(){_cancel.Cancel();_listener.Stop();await _run;_cancel.Dispose();}
    }
    private static async Task TransportTests()
    {
        var key=ExternalCoolingProtocol.ParsePairingKey(ExternalCoolingProtocol.NewPairingKey());
        var disabled=new Esp32TcpClient();await disabled.RunAsync(new(){Mode=ExternalCoolingMode.Disabled},key.ToArray(),new Source(),default);
        Require(disabled.Diagnostics is{State:ExternalCoolingConnectionState.Disabled,Sent:0,Connections:0},"Disabled client opened a connection.");
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
            else
            {
                await Until(()=>client.Diagnostics.Failures>0);
                var expected=fault is SimulatorFault.Partial or SimulatorFault.Delayed or SimulatorFault.Silence?ExternalCoolingFailureKind.Timeout:
                    fault==SimulatorFault.Disconnect?ExternalCoolingFailureKind.Connection:ExternalCoolingFailureKind.Protocol;
                Require(client.Diagnostics.LastFailureKind==expected&&client.Diagnostics.LastFailureUtc.HasValue,"Failure category/time missing or incorrect.");
            }
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
            Require(client.Diagnostics.Failures==1&&client.Diagnostics.Sent==0&&client.Diagnostics.Received==0&&client.Diagnostics.LastFailureKind==ExternalCoolingFailureKind.Authentication,"Authentication failure enabled telemetry/control or lost its category.");
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
        await RemoteLeaseTest(key);
        await RestartServerTest(key);
        await ResourceTest(key);
    }
    private static async Task RemoteLeaseTest(byte[] key)
    {
        await using var server=new Server(key,new());using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,server.Port,cancel.Token);
        await using var wire=new FramedConnection(tcp.GetStream());using var auth=await AuthenticatedSession.ClientAsync(wire,key,cancel.Token);
        var source=new Source();
        await wire.WriteAsync(ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",auth.Id,1,source.Capture(DateTimeOffset.UtcNow).WithoutRequest("AwaitingDevice")),auth.SendKey),cancel.Token);
        var first=await wire.ReadAsync(cancel.Token);ExternalCoolingProtocol.Verify(first,auth.ReceiveKey,auth.Id,0);
        Require(ExternalCoolingProtocol.Status(first).Mode=="Local","First request enabled remote before device confirmation.");
        await Task.Delay(2200,cancel.Token);
        await wire.WriteAsync(ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",auth.Id,2,source.Capture(DateTimeOffset.UtcNow) with{ReplyToDeviceSequence=first.Sequence}),auth.SendKey),cancel.Token);
        var old=await wire.ReadAsync(cancel.Token);ExternalCoolingProtocol.Verify(old,auth.ReceiveKey,auth.Id,first.Sequence);
        Require(ExternalCoolingProtocol.Status(old).Mode=="Local","Stale reply reference enabled remote control.");
        await wire.WriteAsync(ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",auth.Id,3,source.Capture(DateTimeOffset.UtcNow) with{ReplyToDeviceSequence=old.Sequence}),auth.SendKey),cancel.Token);
        var fresh=await wire.ReadAsync(cancel.Token);ExternalCoolingProtocol.Verify(fresh,auth.ReceiveKey,auth.Id,old.Sequence);
        Require(ExternalCoolingProtocol.Status(fresh).Mode=="Remote","Fresh confirmed request not accepted by simulator.");
        Console.WriteLine("PASS device monotonic 2 s lease: first/unconfirmed and delayed request Local; fresh request Remote.");
    }
    private static async Task RestartServerTest(byte[] key)
    {
        var server=new Server(key,new());var port=server.Port;
        await using var manager=new ExternalCoolingManager(new Source(),()=>key.ToArray());
        try
        {
            await manager.ConfigureAsync(new(){Mode=ExternalCoolingMode.MonitorOnly,UseSimulator=true,Port=port});await Until(()=>manager.Diagnostics.Received>=1);
            var session=manager.Diagnostics.SessionId;await server.DisposeAsync();
            server=new Server(key,new(),port);
            await Until(()=>manager.Diagnostics.State==ExternalCoolingConnectionState.Connected&&manager.Diagnostics.SessionId!=session);
            Console.WriteLine("PASS actual server stop/restart on same TCP port and new authenticated session.");
        }
        finally{await manager.DisconnectAsync();await server.DisposeAsync();}
    }
    private static async Task ResourceTest(byte[] key)
    {
        await using var server=new Server(key,new());await using var manager=new ExternalCoolingManager(new Source(),()=>key.ToArray());
        await manager.ConfigureAsync(new(){Mode=ExternalCoolingMode.MonitorOnly,UseSimulator=true,Port=server.Port});await Until(()=>manager.Diagnostics.Received>=2);
        using var process=Process.GetCurrentProcess();process.Refresh();var cpu=process.TotalProcessorTime;var memory=GC.GetTotalMemory(true);var start=manager.Diagnostics;
        var timer=Stopwatch.StartNew();await Task.Delay(TimeSpan.FromSeconds(10));process.Refresh();var elapsed=timer.Elapsed.TotalSeconds;
        var end=manager.Diagnostics;var cpuSeconds=(process.TotalProcessorTime-cpu).TotalSeconds;var memoryDelta=GC.GetTotalMemory(true)-memory;
        var metrics=new{seconds=elapsed,cpuOneCorePct=100*cpuSeconds/elapsed,managedBytesDelta=memoryDelta,bytesPerSecond=(end.BytesSent-start.BytesSent+end.BytesReceived-start.BytesReceived)/elapsed,
            framesSent=end.Sent-start.Sent,framesReceived=end.Received-start.Received,scope="10-second in-process localhost simulator; includes client/server test process; not a physical benchmark"};
        Directory.CreateDirectory("logs/external-cooling-self-test");File.WriteAllText("logs/external-cooling-self-test/performance.json",System.Text.Json.JsonSerializer.Serialize(metrics,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Require(metrics.framesSent is >=8 and <=12&&metrics.bytesPerSecond<8192&&cpuSeconds<5&&memoryDelta<32*1024*1024,"Network worker cadence/resources exceeded test bounds.");
        Console.WriteLine($"PASS resources {elapsed:0.0}s: one-core CPU={metrics.cpuOneCorePct:0.00}%, managed delta={memoryDelta} B, traffic={metrics.bytesPerSecond:0} B/s.");
    }
}
