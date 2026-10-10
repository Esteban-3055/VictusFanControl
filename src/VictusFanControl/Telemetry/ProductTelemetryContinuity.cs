namespace VictusFanControl.Telemetry;

/// <summary>Retains valid critical fields for strictly less than five seconds without renewing their acquisition dates.</summary>
internal sealed class ProductTelemetryContinuity
{
    private TelemetrySnapshot? _last;
    internal void Reset() => _last = null;
    internal TelemetrySnapshot Observe(TelemetrySnapshot raw)
    {
        var held = new List<string>();
        var previous = _last;
        bool Recent(DateTimeOffset? at) => at.HasValue && raw.Timestamp >= at && raw.Timestamp - at < TimeSpan.FromSeconds(5);
        static bool Valid(double? v, double min, double max) => v.HasValue && double.IsFinite(v.Value) && v >= min && v <= max;
        var cpu = Valid(raw.CpuTemperatureC, 10, 110) && raw.CpuCoreTelemetryComplete;
        var gpu = Valid(raw.GpuTemperatureC, 10, 105);
        var cp = Valid(raw.CpuPackagePowerW, 0, 500);
        var gp = Valid(raw.GpuPowerW, 0, 300);
        if (!cpu && previous is not null && Recent(previous.CpuThermalSampledAtUtc))
        {
            raw = raw with { CpuTemperatureC = previous.CpuTemperatureC, CpuCoreTemperatures = previous.CpuCoreTemperatures,
                CpuExpectedPhysicalCoreCount = previous.CpuExpectedPhysicalCoreCount, CpuThermalSampledAtUtc = previous.CpuThermalSampledAtUtc };
            held.Add("temperatura CPU");
        }
        else raw = raw with { CpuThermalSampledAtUtc = cpu ? raw.Timestamp : null };
        if (!gpu && previous is not null && Recent(previous.GpuThermalSampledAtUtc))
        {
            raw = raw with { GpuName = raw.GpuName ?? previous.GpuName, GpuTemperatureC = previous.GpuTemperatureC, GpuThermalSampledAtUtc = previous.GpuThermalSampledAtUtc };
            held.Add("temperatura GPU");
        }
        else raw = raw with { GpuThermalSampledAtUtc = gpu ? raw.Timestamp : null };
        if (!cp && previous is not null && Recent(previous.CpuPowerSampledAtUtc))
        { raw = raw with { CpuPackagePowerW = previous.CpuPackagePowerW, CpuPowerSampledAtUtc = previous.CpuPowerSampledAtUtc }; held.Add("potencia CPU"); }
        else raw = raw with { CpuPowerSampledAtUtc = cp ? raw.Timestamp : null };
        if (!gp && previous is not null && Recent(previous.GpuPowerSampledAtUtc))
        { raw = raw with { GpuPowerW = previous.GpuPowerW, GpuPowerSampledAtUtc = previous.GpuPowerSampledAtUtc }; held.Add("potencia GPU"); }
        else raw = raw with { GpuPowerSampledAtUtc = gp ? raw.Timestamp : null };
        _last = raw with { ProductTelemetryTolerance = true, RetainedTelemetry = held.Count == 0 ? null : string.Join(", ", held) };
        return _last;
    }
}
