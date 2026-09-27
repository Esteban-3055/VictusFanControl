namespace VictusFanControl.Telemetry;

public sealed record CpuCoreTemperatureSample(
    int CoreIndex,
    int LogicalProcessorIndex,
    string CoreType,
    double TemperatureC);

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
    /// <summary>
    /// Read-only physical-core temperature telemetry. The collection is kept
    /// outside the positional constructor so existing synthetic tests remain
    /// source-compatible while the new sensor path is qualified.
    /// </summary>
    public IReadOnlyList<CpuCoreTemperatureSample> CpuCoreTemperatures { get; init; } =
        Array.Empty<CpuCoreTemperatureSample>();

    public int? CpuExpectedPhysicalCoreCount { get; init; }

    public bool CpuCoreTelemetryComplete =>
        CpuExpectedPhysicalCoreCount is > 0 &&
        CpuCoreTemperatures.Count == CpuExpectedPhysicalCoreCount.Value;

    public double? CpuCoreMaxTemperatureC =>
        CpuCoreTemperatures.Count == 0
            ? null
            : CpuCoreTemperatures.Max(sample => sample.TemperatureC);

    public double? CpuCoreAverageTemperatureC =>
        CpuCoreTemperatures.Count == 0
            ? null
            : CpuCoreTemperatures.Average(sample => sample.TemperatureC);

    /// <summary>
    /// CPU temperature used by safety/control decisions: whichever is hotter
    /// between Intel package telemetry and the hottest physical core.
    /// </summary>
    public double? CpuControlTemperatureC
    {
        get
        {
            if (!CpuTemperatureC.HasValue)
            {
                return CpuCoreMaxTemperatureC;
            }

            return CpuCoreMaxTemperatureC.HasValue
                ? Math.Max(CpuTemperatureC.Value, CpuCoreMaxTemperatureC.Value)
                : CpuTemperatureC.Value;
        }
    }

    public bool IsComplete =>
        CpuTemperatureC.HasValue &&
        CpuCoreTelemetryComplete &&
        CpuPackagePowerW.HasValue &&
        CpuLoadPercent.HasValue &&
        GpuTemperatureC.HasValue &&
        GpuPowerW.HasValue &&
        GpuLoadPercent.HasValue &&
        CpuFanRpm.HasValue &&
        GpuFanRpm.HasValue;
}
