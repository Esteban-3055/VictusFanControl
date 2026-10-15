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
        Require(exporter.Capture(now) is{Solicitud:54,CpuTargetLevel:54,GpuTargetLevel:52,DemandaPct:null},"Manual logical request/physical targets lost or historical cap used.");
        state=state with{FanMode="Firmware"};
        Require(exporter.Capture(now) is{Solicitud:null,DemandaPct:null},"Firmware manufactured control.");
        var ram=new WindowsPhysicalMemoryUsage().ReadPercent();
        Require(ram is >=0 and <=100,"GlobalMemoryStatusEx did not return real RAM usage.");
        Console.WriteLine($"PASS export: modes, authority, retention, transition, nulls, age, 54/52; real Windows physical RAM={ram:0.0}%.");
        await Task.CompletedTask;
    }
}
