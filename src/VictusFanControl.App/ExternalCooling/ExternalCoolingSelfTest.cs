using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal static class ExternalCoolingSelfTest
{
    private sealed class Memory(double? value) : IPhysicalMemoryUsage { public double? ReadPercent()=>value; }
    internal static void Require(bool condition,string detail) { if(!condition)throw new InvalidOperationException(detail); }
    internal static async Task RunAsync()
    {
        var now=DateTimeOffset.UtcNow;
        var state=ProductHomeTelemetrySelfTest.StateAt(now) with{CommandedCpuLevel=30,CommandedGpuLevel=30};
        var exporter=new TelemetryExportService(()=>state,new Memory(54.3));
        var t=exporter.Capture(now);
        Require(t is { DemandaPct:60, Solicitud:30, SolicitudValida:true, RamUsagePct:54.3, CpuUsagePct:35, GpuUsagePct:45 },"Exported demand/request/usage differ from sources.");
        foreach(var invalid in new[]{state with{FanAuthority="Firmware"},state with{FanMode="Firmware"},state with{LifecycleBlocked=true},
            state with{AutomaticPreparing=true},state with{AutomaticSourceTransition="Battery"},state with{PerformanceUpdating=true},
            state with{Runtime="Degraded"},state with{FanLevel=31},state with{AppliedFanProfile="Battery"},state with{AutomaticDecisionSnapshot=null},
            state with{Snapshot=state.Snapshot! with{RetainedTelemetry="CPU"}},state with{CommandedGpuLevel=null},
            state with{AutomaticDecision=state.AutomaticDecision! with{UnifiedDemand=state.AutomaticDecision!.UnifiedDemand! with{Percent=double.NaN}}}})
        {
            var result=new TelemetryExportService(()=>invalid,new Memory(null)).Capture(now);
            Require(result.Solicitud is null&&!result.SolicitudValida,"Inactive/unaccepted request escaped.");
        }
        Require(exporter.Capture(now.AddSeconds(6)) is{Solicitud:null,CpuUsagePct:null,CpuTempC:null},"Expired snapshot renewed.");
        Require(exporter.Capture(now.AddSeconds(-1)) is{Solicitud:null,CpuUsagePct:null},"Future snapshot accepted.");
        state=state with{FanMode="Manual",FanLevel=54,CommandedCpuLevel=54,CommandedGpuLevel=52};
        Require(exporter.Capture(now).Solicitud is null,"Retained Automatic target was treated as an explicit Manual Apply.");
        state=state with{ManualAppliedLevel=54};
        Require(exporter.Capture(now) is{Solicitud:54,CpuTargetLevel:54,GpuTargetLevel:52,DemandaPct:null},"Manual logical request/physical targets lost or historical cap used.");
        state=state with{FanMode="Firmware"};
        Require(exporter.Capture(now) is{Solicitud:null,DemandaPct:null},"Firmware manufactured control.");
        var ram=new WindowsPhysicalMemoryUsage().ReadPercent();
        Require(ram is >=0 and <=100,"GlobalMemoryStatusEx did not return real RAM usage.");
        Console.WriteLine($"PASS export: modes, authority, retention, transition, nulls, age, 54/52; real Windows physical RAM={ram:0.0}%.");
        await ExportOverTcpAsync();
    }
    private static async Task ExportOverTcpAsync()
    {
        var state=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow) with{CommandedCpuLevel=30,CommandedGpuLevel=30};
        var key=ExternalCoolingProtocol.ParsePairingKey(ExternalCoolingProtocol.NewPairingKey());
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var observed=new List<ExportTelemetry>();
        var server=Task.Run(async()=>
        {
            using var tcp=await listener.AcceptTcpClientAsync(cancel.Token);
            await using var wire=new FramedConnection(tcp.GetStream());
            using var session=await AuthenticatedSession.ServerAsync(wire,key,cancel.Token);
            long previous=0;
            for(int i=0;i<4;i++)
            {
                var frame=await wire.ReadAsync(cancel.Token);ExternalCoolingProtocol.Verify(frame,session.ReceiveKey,session.Id,previous);previous=frame.Sequence;
                var t=ExternalCoolingProtocol.Telemetry(frame);lock(observed)observed.Add(t);
                var fresh=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow) with{CommandedCpuLevel=54,CommandedGpuLevel=52,FanMode="Manual",FanLevel=54,ManualAppliedLevel=54};
                if(i>0)Volatile.Write(ref state,i==1?fresh:fresh with{FanMode="Firmware",FanAuthority="Firmware",FanLevel=null});
                await wire.WriteAsync(ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("deviceStatus",session.Id,i+1,new DeviceStatus
                {AckSequence=previous,AmbientTempC=24,AppliedPwmPct=30,Mode="Local",Communication="Ready",Extensions=new(){Slot1=null,Slot2=null}}),session.SendKey),cancel.Token);
            }
        });
        await using var manager=new ExternalCoolingManager(new TelemetryExportService(()=>Volatile.Read(ref state),new WindowsPhysicalMemoryUsage()),()=>key.ToArray());
        try
        {
            await manager.ConfigureAsync(new(){Mode=ExternalCoolingMode.MonitorAndRequest,UseSimulator=true,Port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port,AutoReconnect=false});
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            lock(observed)Require(observed.Count==4&&observed[0] is{Solicitud:null,RequestEvidence:"AwaitingDevice"}&&observed[1] is{Solicitud:30,DemandaPct:60}&&observed[2] is{Solicitud:54,DemandaPct:null}&&observed[3] is{Solicitud:null,ModoSolicitado:"Firmware"},"Exporter TCP mode transition mismatch.");
            await manager.DisconnectAsync();
            Console.WriteLine("PASS real exporter -> authenticated loopback: Automatic demand/request, Manual 54/52, Firmware null, received device status; zero hardware commands.");
        }
        finally{cancel.Cancel();listener.Stop();}
    }
}
