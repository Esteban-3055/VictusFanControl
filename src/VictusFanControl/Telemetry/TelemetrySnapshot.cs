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

    // Fan acquisition metadata stays separate from the fast CPU/GPU timestamp.
    // WMI level L denotes [100L, 100L+99] RPM, not an exact tachometer count.
    public string? FanTelemetrySource { get; init; }
    public int? FanRpmResolution { get; init; }
    public DateTimeOffset? FanSampledAtUtc { get; init; }
    public long? FanSampleAgeMilliseconds { get; init; }
    // Epoch at which the monotonic fan age was captured, independent of the
    // CPU/GPU sampling start. Missing on older/synthetic snapshots.
    public DateTimeOffset? FanAgeCapturedAtUtc { get; init; }
    public byte? CpuFanSpeedLevel { get; init; }
    public byte? GpuFanSpeedLevel { get; init; }

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
        (FanTelemetrySource != "HP-WMI-ACPI-2D" ||
         (FanSampleAgeMilliseconds is >= 0 and < HpWmiFanTelemetryReader.MaximumSampleAgeMilliseconds &&
          FanSampledAtUtc.HasValue && FanRpmResolution == HpWmiFanTelemetrySample.ResolutionRpm)) &&
        CpuTemperatureC.HasValue &&
        CpuCoreTelemetryComplete &&
        CpuPackagePowerW.HasValue &&
        CpuLoadPercent.HasValue &&
        GpuTemperatureC.HasValue &&
        GpuPowerW.HasValue &&
        GpuLoadPercent.HasValue &&
        CpuFanRpm.HasValue &&
        GpuFanRpm.HasValue;

    /// <summary>
    /// A cached fan sample can expire after this immutable snapshot was built.
    /// Add time spent holding the snapshot to its captured monotonic fan age;
    /// a fresh CPU/GPU timestamp must not renew older fan acquisition data.
    /// Add elapsed time only from the epoch at which that age was captured.
    /// Using CPU/GPU sampling start would count hardware-sampling time twice.
    /// Legacy snapshots without that epoch keep the conservative old behavior.
    /// </summary>
    public bool IsFanTelemetryFreshAt(DateTimeOffset now) =>
        FanTelemetrySource != "HP-WMI-ACPI-2D" ||
        (FanSampleAgeMilliseconds is >= 0 &&
         FanSampledAtUtc.HasValue &&
         FanRpmResolution == HpWmiFanTelemetrySample.ResolutionRpm &&
         (FanAgeCapturedAtUtc ?? Timestamp) >= Timestamp &&
         now >= (FanAgeCapturedAtUtc ?? Timestamp) &&
         FanSampleAgeMilliseconds.Value + (now - (FanAgeCapturedAtUtc ?? Timestamp)).TotalMilliseconds <
             HpWmiFanTelemetryReader.MaximumSampleAgeMilliseconds);
}
