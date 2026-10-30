using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;
using VictusFanControl.Safety;

namespace VictusFanControl.App;

internal static class ProductPerformanceUpdate
{
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
