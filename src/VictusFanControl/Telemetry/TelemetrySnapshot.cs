namespace VictusFanControl.Telemetry;

public sealed record TelemetrySnapshot(
    DateTimeOffset Timestamp,
    string? CpuName,
    double? CpuTemperatureC,
    double? CpuPackagePowerW,
    double? CpuLoadPercent,
    string? GpuName,
    double? GpuTemperatureC,
    double? GpuPowerW,
    double? GpuLoadPercent,
    double? CpuFanRpm,
    double? GpuFanRpm)
{
    public bool IsComplete =>
        CpuTemperatureC.HasValue &&
        CpuPackagePowerW.HasValue &&
        CpuLoadPercent.HasValue &&
        GpuTemperatureC.HasValue &&
        GpuPowerW.HasValue &&
        GpuLoadPercent.HasValue &&
        CpuFanRpm.HasValue &&
        GpuFanRpm.HasValue;
}
