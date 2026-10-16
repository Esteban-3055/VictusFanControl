using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Esp32Simulator;

internal static partial class Program
{
    private static void HardeningProtocolTests()
    {
        var now=DateTimeOffset.UtcNow;
        var request=new Source().Capture(now) with{ReplyToDeviceSequence=1};
        void Check(ExportTelemetry t)=>ExternalCoolingProtocol.Telemetry(ExternalCoolingProtocol.Create("telemetry",Guid.NewGuid().ToString("N"),1,t));
        Check(request);Check(request with{SampledAtUtc=now.AddMilliseconds(-4999)});
        foreach(var bad in new[]{
            request with{SampledAtUtc=null},request with{CpuTempSampledAtUtc=null},request with{GpuTempSampledAtUtc=null},
            request with{CpuTempC=null},request with{GpuTempC=null},
            request with{SampledAtUtc=now.AddMilliseconds(-5000)},
            request with{CpuTempSampledAtUtc=now.AddYears(-1)},request with{GpuTempSampledAtUtc=now.AddYears(-1)},
            request with{ModoSolicitado="Automatic",ModoEfectivo="Automatic",DemandaPct=60},
            request with{ModoSolicitado="Automatic",ModoEfectivo="Automatic",DecisionAtUtc=now,DemandaPct=null},
            request with{ModoSolicitado="Automatic",ModoEfectivo="Automatic",DecisionAtUtc=now.AddYears(-1),DemandaPct=60}})
            Reject(()=>Check(bad),"Missing/stale source evidence enabled a request.");
        Check(request with{ModoSolicitado="Automatic",ModoEfectivo="Automatic",DecisionAtUtc=now,DemandaPct=60});
        foreach(var days in new[]{-100,100})
        {
            var shifted=request with{CapturedAtUtc=now.AddDays(days),SampledAtUtc=now.AddDays(days),
                CpuTempSampledAtUtc=now.AddDays(days),GpuTempSampledAtUtc=now.AddDays(days)};
            Check(shifted); // Relative source age never depends on the receiver's wall clock.
        }
        var delays=Enumerable.Range(1,10).Select(n=>Esp32TcpClient.ReconnectDelay(n).TotalMilliseconds).ToArray();
        Require(delays.SequenceEqual(new double[]{500,1000,2000,4000,8000,16000,30000,30000,30000,30000}),"Backoff did not reach/bound 30 seconds.");
        Require(Esp32TcpClient.ReconnectDelay(int.MaxValue,249).TotalMilliseconds==30249,"Backoff overflow/jitter changed.");
        Console.WriteLine("PASS source-age evidence, Automatic demand/date, unsynchronized clocks, backoff saturation.");
    }

    private static void SimulatorInputTests()
    {
        var key=ExternalCoolingProtocol.NewPairingKey();
        var text=new StringBuilder();
        var events=new Queue<ConsoleKeyInfo>();
        void Add(string value)
        {
            foreach(var c in value)events.Enqueue(new(c,c=='\r'?ConsoleKey.Enter:c=='\b'?ConsoleKey.Backspace:ConsoleKey.NoName,false,false,false));
        }
        Add("not a key!\r"+Convert.ToBase64String(new byte[1])+"\r"+new string('x',10000)+"\r"+key+"x\b\r");
        var parsed=SimulatorConsole.ReadPairingKey(()=>events.Dequeue(),s=>text.Append(s));
        Require(parsed is not null&&CryptographicOperations.FixedTimeEquals(parsed,Convert.FromBase64String(key)),"Hidden input did not recover from invalid/pasted/long input.");
        Require(!text.ToString().Contains(key,StringComparison.Ordinal)&&text.ToString().Contains("Clave no válida"),"Input feedback leaked the key or omitted retry.");
        CryptographicOperations.ZeroMemory(parsed!);
        var canceled=SimulatorConsole.ReadPairingKey(()=>new ConsoleKeyInfo('\u0003',ConsoleKey.C,false,false,true),_=>{});
        Require(canceled is null,"Ctrl+C did not cancel key entry.");
        Require(SimulatorConsole.TryOptions([],out var defaults)&&defaults.Port==8765,"Default arguments changed.");
        Require(SimulatorConsole.TryOptions(["--port","12345","--fault","Fragmented","--after","2","--bind","127.0.0.1"],out _),"Valid arguments rejected.");
        foreach(var args in new[]{new[]{"--port"},new[]{"--port","0"},new[]{"--port","65536"},new[]{"--after","0"},
            new[]{"--fault","999"},new[]{"--bind","not-an-ip"},new[]{"--unknown","1"},new[]{"--port","8765","--port","8766"}})
            Require(!SimulatorConsole.TryOptions(args,out _),"Malformed command line admitted.");
        Console.WriteLine("PASS hidden input retries/bounds/backspace/cancellation, no key output, argument validation.");
    }

    private static async Task HardeningTransportTests()
    {
        // A cancellation must also stop a frame already buffered in memory.
        var hello=ExternalCoolingProtocol.Create("hello",Guid.NewGuid().ToString("N"),0,new ClientHello{ClientNonce=ExternalCoolingProtocol.NewNonce()});
        var bytes=ExternalCoolingProtocol.Encode(hello).Concat(new byte[]{10}).ToArray();
        await using(var wire=new FramedConnection(new MemoryStream(bytes.Concat(bytes).ToArray())))
        {
            await wire.ReadAsync(default);using var canceled=new CancellationTokenSource();canceled.Cancel();
            try{await wire.ReadAsync(canceled.Token);throw new InvalidOperationException("Canceled buffered read returned a frame.");}
            catch(OperationCanceledException){}
        }

        var key=ExternalCoolingProtocol.ParsePairingKey(ExternalCoolingProtocol.NewPairingKey());
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int errors=0,observed=0;var device=new SimulatorDeviceState();
        var server=SimulatorServer.RunAsync(listener,key,new(),(_,_)=>Interlocked.Increment(ref observed),_=>Interlocked.Increment(ref errors),cancel.Token,device);
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        async Task BadHello()
        {
            using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,port,cancel.Token);
            await using var wire=new FramedConnection(tcp.GetStream());
            await wire.WriteAsync(hello with{ProtocolVersion=99},cancel.Token);
            await Until(()=>Volatile.Read(ref errors)>=1);
        }
        try
        {
            await BadHello(); // Rejected before authentication; server must continue.
            using(var tcp=new TcpClient())
            {
                await tcp.ConnectAsync(IPAddress.Loopback,port,cancel.Token);
                await using var wire=new FramedConnection(tcp.GetStream());
                using var auth=await AuthenticatedSession.ClientAsync(wire,key,cancel.Token);
                var first=ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",auth.Id,1,new Source().Capture(DateTimeOffset.UtcNow).WithoutRequest()),auth.SendKey);
                await wire.WriteAsync(first,cancel.Token);var response=await wire.ReadAsync(cancel.Token);
                var request=new Source().Capture(DateTimeOffset.UtcNow) with{ReplyToDeviceSequence=response.Sequence};
                var frame=ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",auth.Id,2,request),auth.SendKey);
                await wire.WriteAsync(frame,cancel.Token);await wire.ReadAsync(cancel.Token);
                Require(device.Snapshot.Mode=="Remote","Fresh request did not reach simulated Remote.");
                await Until(()=>device.Snapshot is{Mode:"Local",Communication:"Timeout"},3000);
                Require(device.Snapshot.PwmPct==25,"Independent silence timer did not restore local simulated PWM.");
                // A newer sequence and recent reply cannot rescue stale acquisition dates.
                var stale=request with{CapturedAtUtc=DateTimeOffset.UtcNow,SampledAtUtc=DateTimeOffset.UtcNow.AddYears(-1)};
                await wire.WriteAsync(ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",auth.Id,3,stale),auth.SendKey),cancel.Token);
                await Until(()=>Volatile.Read(ref errors)>=2);
            }
            Require(device.Snapshot.Mode=="Local","Rejected data retained Remote.");
            // Authenticate again after the invalid session; replay must only close this connection.
            using(var tcp=new TcpClient())
            {
                await tcp.ConnectAsync(IPAddress.Loopback,port,cancel.Token);
                await using var wire=new FramedConnection(tcp.GetStream());
                using var auth=await AuthenticatedSession.ClientAsync(wire,key,cancel.Token);
                var f=ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("telemetry",auth.Id,1,new Source().Capture(DateTimeOffset.UtcNow).WithoutRequest()),auth.SendKey);
                await wire.WriteAsync(f,cancel.Token);await wire.ReadAsync(cancel.Token);
                await wire.WriteAsync(f,cancel.Token);await Until(()=>Volatile.Read(ref errors)>=3);
            }
            await using(var manager=new ExternalCoolingManager(new Source(),()=>key.ToArray()))
            {
                await manager.ConfigureAsync(new(){Mode=ExternalCoolingMode.MonitorAndRequest,UseSimulator=true,Port=port});
                await Until(()=>manager.Diagnostics.Received>=2);
                var session=manager.Diagnostics.SessionId;var connections=manager.Diagnostics.Connections;
                await Task.WhenAll(manager.ResumeAsync(),manager.ResumeAsync());
                Require(manager.Diagnostics.SessionId==session&&manager.Diagnostics.Connections==connections,"Duplicate resume restarted a live session.");
                await manager.DisconnectAsync();await Until(()=>device.Snapshot.Mode=="Local");
                await manager.ResumeAsync();await Task.Delay(100);
                Require(manager.Diagnostics.State==ExternalCoolingConnectionState.Disconnected,"Resume undid explicit disconnect.");
                await manager.DisposeAsync();await manager.DisconnectAsync();manager.Suspend();
                Require(manager.Diagnostics.State==ExternalCoolingConnectionState.Disabled,"A late lifecycle call revived disposed diagnostics.");
            }
            Require(Volatile.Read(ref observed)>=4,"Server did not survive malformed/replayed/expired sessions.");
        }
        finally{cancel.Cancel();listener.Stop();await server;CryptographicOperations.ZeroMemory(key);}
        Console.WriteLine("PASS production accept loop survives bad hello/stale evidence/replay; independent fallback, EOF, duplicate resume, explicit disconnect.");

        // A failed local pairing must be visible without exposing the exception contents.
        await using(var manager=new ExternalCoolingManager(new Source(),()=>throw new CryptographicException("sensitive test text")))
        {
            try{await manager.ConfigureAsync(new(){Mode=ExternalCoolingMode.MonitorOnly});throw new InvalidOperationException("Missing pairing admitted.");}
            catch(CryptographicException){}
            Require(manager.Diagnostics is{State:ExternalCoolingConnectionState.Error,LastFailureKind:ExternalCoolingFailureKind.Pairing,Failures:1}&&
                !manager.Diagnostics.Detail.Contains("sensitive"),"Pairing failure was hidden or leaked details.");
        }
    }
}
