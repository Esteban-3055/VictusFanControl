using System.Runtime.InteropServices;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal interface IPhysicalMemoryUsage { double? ReadPercent(); }

internal sealed class WindowsPhysicalMemoryUsage : IPhysicalMemoryUsage
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public double? ReadPercent()
    {
        var status = new MemoryStatus { Length=(uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0 && status.AvailPhys <= status.TotalPhys
            ? 100.0 * (status.TotalPhys-status.AvailPhys)/status.TotalPhys : null;
    }
}

/// <summary>Read-only adapter. No controller reference, hardware reader or retained presentation value.</summary>
internal sealed class TelemetryExportService(Func<ProductRuntimeState> state, IPhysicalMemoryUsage memory) : ITelemetryExportSource
{
    public ExportTelemetry Capture(DateTimeOffset now)
    {
        var s=state(); var sample=s.Snapshot;
        bool Fresh(DateTimeOffset? at, TimeSpan age) => at is {} t && now >= t && now-t <= age;
        var live=sample is not null && Fresh(sample.Timestamp, sample.MaximumControlAge);
        static bool NonNegative(double? value)=>value is{} v&&double.IsFinite(v)&&v>=0;
        var control=live && sample!.IsComplete && sample.RetainedTelemetry is null &&
            sample.HasFreshControlSensorsAt(now) && sample.IsFanTelemetryFreshAt(now) &&
            ExportTelemetry.Range(sample.CpuControlTemperatureC,-20,130).HasValue &&
            ExportTelemetry.Range(sample.GpuTemperatureC,-20,130).HasValue &&
            NonNegative(sample.CpuPackagePowerW) && NonNegative(sample.GpuPowerW) &&
            sample.CpuCoreTemperatures.All(c=>ExportTelemetry.Range(c.TemperatureC,-20,130).HasValue);
        var ready=s.Runtime=="Healthy" && !s.LifecycleBlocked && !s.AutomaticPreparing &&
            s.AutomaticSourceTransition is null && !s.PerformanceUpdating && s.PerformanceRecovery?.Pending!=true;
        var accepted=ready && control && s.FanAuthority=="Custom" && s.FanLevel is >=0 &&
            s.CommandedCpuLevel is >=0 && s.CommandedGpuLevel is >=0;
        double? demand=null; DateTimeOffset? decisionAt=null;
        if (s.FanMode=="Automatic")
        {
            var d=s.AutomaticDecision; var ds=s.AutomaticDecisionSnapshot;
            accepted &= s.AutomaticAuthorized && !string.IsNullOrWhiteSpace(s.AutomaticSessionId) &&
                s.Source is "Ac" or "Battery" && s.AppliedFanProfile==s.Source && s.AppliedAutomaticConfiguration is not null &&
                ds is { IsComplete:true, RetainedTelemetry:null } && Fresh(ds.Timestamp,ds.MaximumControlAge) &&
                ds.HasFreshControlSensorsAt(now) && ds.IsFanTelemetryFreshAt(now) &&
                d is { ExecutionAuthorized:true, Mode:VictusFanControl.Control.Adaptive.AdaptiveFanProductionMode.Automatic,
                    Authority:VictusFanControl.Control.FanAuthority.Custom, EqualFanLevel:not null } && d.EqualFanLevel==s.FanLevel;
            if (accepted)
            {
                demand=ExportTelemetry.Range(d!.UnifiedDemand?.Percent,0,100); decisionAt=ds!.Timestamp;
                accepted &= demand.HasValue;
            }
        }
        else if (s.FanMode=="Manual") accepted &= s.ManualAuthorized && s.ManualAppliedLevel.HasValue && s.ManualAppliedLevel==s.FanLevel;
        else accepted=false;
        var ram=ExportTelemetry.Range(memory.ReadPercent(),0,100);
        var cpuAt=sample?.CpuThermalSampledAtUtc ?? sample?.Timestamp;
        var gpuAt=sample?.GpuThermalSampledAtUtc ?? sample?.Timestamp;
        return new()
        {
            CapturedAtUtc=now, SampledAtUtc=live?sample!.Timestamp:null, DecisionAtUtc=decisionAt,
            CpuTempSampledAtUtc=live&&Fresh(cpuAt,sample!.MaximumControlAge)?cpuAt:null,
            GpuTempSampledAtUtc=live&&Fresh(gpuAt,sample!.MaximumControlAge)?gpuAt:null,
            RamSampledAtUtc=ram.HasValue?now:null, RamUsagePct=ram,
            CpuUsagePct=live?ExportTelemetry.Range(sample!.CpuLoadPercent,0,100):null,
            GpuUsagePct=live?ExportTelemetry.Range(sample!.GpuLoadPercent,0,100):null,
            CpuTempC=live&&Fresh(cpuAt,sample!.MaximumControlAge)?ExportTelemetry.Range(sample.CpuControlTemperatureC,-20,130):null,
            GpuTempC=live&&Fresh(gpuAt,sample!.MaximumControlAge)?ExportTelemetry.Range(sample.GpuTemperatureC,-20,130):null,
            DemandaPct=demand, Solicitud=accepted?s.FanLevel:null, SolicitudValida=accepted,
            CpuTargetLevel=accepted?s.CommandedCpuLevel:null, GpuTargetLevel=accepted?s.CommandedGpuLevel:null,
            ModoSolicitado=s.AutomaticPreparing?"Automatic":s.FanMode,
            ModoEfectivo=s.LifecycleBlocked?"Interrupted":s.AutomaticPreparing||s.AutomaticSourceTransition is not null||s.PerformanceUpdating?"Transition":
                s.FanMode=="Firmware"&&s.FanAuthority=="Firmware"?"Firmware":accepted?s.FanMode:"Unavailable",
            PowerSource=!live?"Unknown":s.Source=="Ac"?"AC":s.Source=="Battery"?"Battery":"Unknown",
            RequestEvidence=accepted?"WmiAcceptedSupervised":"Unavailable"
        };
    }
}
