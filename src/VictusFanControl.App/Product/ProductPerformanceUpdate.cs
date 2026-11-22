using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;
using VictusFanControl.Safety;

namespace VictusFanControl.App;

internal static class ProductPerformanceUpdate
{
    internal static (bool Cpu,bool Gpu) ChangedActiveDomains(PerformanceGuiSessionConfiguration? before,PerformanceGuiSessionConfiguration next)=>
        (next.CpuEnabled&&(before is null||!before.CpuEnabled||before.AcPl1Watts!=next.AcPl1Watts||before.AcPl2Watts!=next.AcPl2Watts||
            before.BatteryPl1Watts!=next.BatteryPl1Watts||before.BatteryPl2Watts!=next.BatteryPl2Watts),
         next.GpuEnabled&&(before is null||!before.GpuEnabled||before.AcGpuMaximumMHz!=next.AcGpuMaximumMHz||before.BatteryGpuMaximumMHz!=next.BatteryGpuMaximumMHz));
    internal static bool RequestedDomainsReady(PerformanceGuiSessionConfiguration? before,PerformanceGuiSessionConfiguration next,PerformanceGuardianResponse status)
    {
        var changed=ChangedActiveDomains(before,next);
        return (!changed.Cpu||status.CpuState=="Active")&&(!changed.Gpu||status.GpuState=="ActiveUnverified");
    }
    internal static bool HasIndependentTelemetry(TelemetrySnapshot? snapshot,DateTimeOffset now,
        PerformanceGuiSessionConfiguration? before,PerformanceGuiSessionConfiguration next)
    {
        var needed=ChangedActiveDomains(before,next);
        if(!needed.Cpu&&!needed.Gpu)return true; // Withdrawal is not admission of new control.
        if(snapshot is not{} s||now<s.Timestamp||now-s.Timestamp>=s.MaximumControlAge)return false;
        bool Fresh(DateTimeOffset? at)=>now>=(at??s.Timestamp)&&now-(at??s.Timestamp)<s.MaximumControlAge&&
            (s.RetainedTelemetry is null||(at??s.Timestamp)==s.Timestamp);
        bool Range(double? v,double low,double high)=>v is{} d&&double.IsFinite(d)&&d>=low&&d<=high;
        return (!needed.Cpu||s.CpuCoreTelemetryComplete&&s.CpuCoreTemperatures.Select(c=>c.CoreIndex).Distinct().Count()==s.CpuCoreTemperatures.Count&&
            s.CpuCoreTemperatures.All(c=>c.CoreIndex>=0&&c.LogicalProcessorIndex>=0&&double.IsFinite(c.TemperatureC)&&c.TemperatureC is >=0 and <=125)&&
            Range(s.CpuControlTemperatureC,10,110)&&Range(s.CpuPackagePowerW,0,500)&&Fresh(s.CpuThermalSampledAtUtc)&&Fresh(s.CpuPowerSampledAtUtc))&&
            (!needed.Gpu||s.GpuName==VictusFanControl.Hardware.Hp.Hp8C40TargetProfile.ExpectedGpuName&&Range(s.GpuTemperatureC,10,105)&&
                Range(s.GpuPowerW,0,300)&&Fresh(s.GpuThermalSampledAtUtc)&&Fresh(s.GpuPowerSampledAtUtc));
    }
    internal static bool HasCurrentTelemetry(TelemetrySnapshot? snapshot, DateTimeOffset now) => snapshot is { IsComplete: true, RetainedTelemetry: null } &&
        now >= snapshot.Timestamp && now - snapshot.Timestamp <= SafetyGate.MaximumTelemetryAge &&
        snapshot.HasFreshControlSensorsAt(now) && snapshot.IsFanTelemetryFreshAt(now);

    internal static void EnsureWaitAllowed(long started,long now,string actualSource,string selectedSource,double? cpuC,double? gpuC)
    {
        if(now<started||now-started>=4000||actualSource!=selectedSource||actualSource is not ("Ac" or "Battery")||
            cpuC is null||gpuC is null||!double.IsFinite(cpuC.Value)||!double.IsFinite(gpuC.Value)||cpuC>=85||gpuC>ProductProtectionSettings.HabitualGpuHandoffC)
            throw new InvalidOperationException("Actualización CPU/GPU pendiente con plazo, fuente o temperatura incompatible; volver a Firmware.");
    }
    internal static ProductProfiles WithPerformance(ProductProfiles profiles, PerformanceGuiSessionConfiguration configuration) => profiles with
    {
        CpuEnabled=configuration.CpuEnabled,GpuEnabled=configuration.GpuEnabled,
        Ac=profiles.Ac with{CpuPl1Watts=configuration.AcPl1Watts,CpuPl2Watts=configuration.AcPl2Watts,GpuMaximumMHz=configuration.AcGpuMaximumMHz},
        Battery=profiles.Battery with{CpuPl1Watts=configuration.BatteryPl1Watts,CpuPl2Watts=configuration.BatteryPl2Watts,GpuMaximumMHz=configuration.BatteryGpuMaximumMHz}
    };
}
