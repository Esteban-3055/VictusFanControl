using System.Runtime.InteropServices;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Telemetry;
using VictusFanControl.Product;

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
internal sealed class TelemetryExportService(Func<ProductRuntimeState> state, IPhysicalMemoryUsage memory,Func<HyperXSnapshot>? hyperx=null,Func<HyperxDisplayConfiguration>? hyperxDisplay=null) : ITelemetryExportSource
{
    internal static bool IndependentTemperaturesReady(TelemetrySnapshot? sample,DateTimeOffset now)
    {
        if(sample is not{} s||!s.CpuCoreTelemetryComplete||now<s.Timestamp||now-s.Timestamp>=s.MaximumControlAge)return false;
        bool Fresh(DateTimeOffset? at)=>now>=(at??s.Timestamp)&&now-(at??s.Timestamp)<s.MaximumControlAge&&
            (s.RetainedTelemetry is null||(at??s.Timestamp)==s.Timestamp);
        return s.CpuCoreTemperatures.Select(c=>c.CoreIndex).Distinct().Count()==s.CpuCoreTemperatures.Count&&
            s.CpuCoreTemperatures.All(c=>c.CoreIndex>=0&&double.IsFinite(c.TemperatureC)&&c.TemperatureC is >=10 and <=125)&&
            ExportTelemetry.Range(s.CpuControlTemperatureC,10,125).HasValue&&ExportTelemetry.Range(s.GpuTemperatureC,10,105).HasValue&&
            Fresh(s.CpuThermalSampledAtUtc)&&Fresh(s.GpuThermalSampledAtUtc);
    }
    public ExportTelemetry Capture(DateTimeOffset now)
    {
        var s=state(); var sample=s.Snapshot;
        var headset=hyperx?.Invoke();
        bool headsetFresh=headset?.Fresh(now)==true&&headset.Percent.HasValue;
        bool headsetRetained=!headsetFresh&&headset?.LastKnownPercent is >=0 and <=100&&
            headset.LastKnownSampledAt is{} lastAt&&lastAt!=default&&lastAt<=now;
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
        IndependentExternalRequest? independent=null;
        if(s.FunctionCoordinationExplicit)
        {
            var binding=s.FunctionPolicies.ExternalCooling.Binding;
            var enabled=s.FunctionRequests.ExternalCooling&&!s.PerformanceLifecycleBlocked&&
                (binding!=ProductFunctionBinding.FollowAutomatic||s.FanMode=="Automatic"&&!s.LifecycleBlocked)&&
                (binding!=ProductFunctionBinding.FollowInternalFans||s.FanMode is "Automatic" or "Manual"&&!s.LifecycleBlocked);
            if(!enabled||s.FunctionPolicies.ExternalOutputMode!=ProductExternalOutputMode.FollowInternalFans)accepted=false;
            if(enabled&&s.FunctionPolicies.ExternalOutputMode==ProductExternalOutputMode.Fixed)
                independent=new("Fixed",s.FunctionPolicies.ExternalFixedOutput,null,null);
            else if(enabled&&s.FunctionPolicies.ExternalOutputMode==ProductExternalOutputMode.TemperatureCurve&&
                IndependentTemperaturesReady(sample,now)&&sample is{} thermal)
            {
                var cpu=thermal.CpuControlTemperatureC!.Value;var gpu=thermal.GpuTemperatureC!.Value;
                var at=new[]{thermal.CpuThermalSampledAtUtc??thermal.Timestamp,thermal.GpuThermalSampledAtUtc??thermal.Timestamp}.Min();
                independent=new("Curve",null,Math.Max(Math.Clamp((cpu-40)/50*100,0,100),Math.Clamp((gpu-35)/46*100,0,100)),at);
            }
        }
        var ram=ExportTelemetry.Range(memory.ReadPercent(),0,100);
        var cpuAt=sample?.CpuThermalSampledAtUtc ?? sample?.Timestamp;
        var gpuAt=sample?.GpuThermalSampledAtUtc ?? sample?.Timestamp;
        return new()
        {
            CapturedAtUtc=now, SampledAtUtc=live?sample!.Timestamp:null, DecisionAtUtc=decisionAt,
            IndependentExternal=independent,
            HyperxDisplay=hyperxDisplay?.Invoke(),
            // Legacy LCD firmware has no retained-value marker while the PC is
            // online. Project the stored number for display at this frame time;
            // carry its real acquisition separately and never alter the monitor.
            HyperxBatteryPct=headsetFresh?headset!.Percent:headsetRetained?headset!.LastKnownPercent:null,
            HyperxSampledAtUtc=headsetFresh?headset!.SampledAt:headsetRetained?now:null,
            HyperxDisplayRetained=headsetRetained,
            HyperxOriginalSampledAtUtc=headsetRetained?headset!.LastKnownSampledAt:null,
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
