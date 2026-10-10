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
        var invalidCores=raw.CpuCoreTemperatures.Any(c=>c.CoreIndex<0||c.LogicalProcessorIndex<0||
            !double.IsFinite(c.TemperatureC)||c.TemperatureC is <0 or >125)||
            raw.CpuCoreTemperatures.Select(c=>c.CoreIndex).Distinct().Count()!=raw.CpuCoreTemperatures.Count;
        var cpu = Valid(raw.CpuTemperatureC, 10, 110) && raw.CpuCoreTelemetryComplete && !invalidCores;
        var gpu = Valid(raw.GpuTemperatureC, 10, 105);
        var cp = Valid(raw.CpuPackagePowerW, 0, 500);
        var gp = Valid(raw.GpuPowerW, 0, 300);
        if (!cpu && !invalidCores && (raw.CpuTemperatureC is null || Valid(raw.CpuTemperatureC,10,110)) &&
            previous is not null && Recent(previous.CpuThermalSampledAtUtc) &&
            (raw.CpuExpectedPhysicalCoreCount is null || raw.CpuExpectedPhysicalCoreCount==previous.CpuExpectedPhysicalCoreCount) &&
            raw.CpuCoreTemperatures.All(c=>previous.CpuCoreTemperatures.Any(p=>p.CoreIndex==c.CoreIndex&&
                p.LogicalProcessorIndex==c.LogicalProcessorIndex&&p.CoreType==c.CoreType)))
        {
            // Preserve every new valid reading, particularly a hotter package/core. Only missing readings are carried.
            var cores=previous.CpuCoreTemperatures.Select(p=>raw.CpuCoreTemperatures.FirstOrDefault(c=>c.CoreIndex==p.CoreIndex)??p).ToArray();
            raw = raw with { CpuTemperatureC = raw.CpuTemperatureC ?? previous.CpuTemperatureC, CpuCoreTemperatures = cores,
                CpuExpectedPhysicalCoreCount = previous.CpuExpectedPhysicalCoreCount, CpuThermalSampledAtUtc = previous.CpuThermalSampledAtUtc };
            held.Add("temperatura CPU");
        }
        else raw = raw with { CpuThermalSampledAtUtc = cpu ? raw.Timestamp : null };
        if (!gpu && raw.GpuTemperatureC is null && previous is not null && Recent(previous.GpuThermalSampledAtUtc))
        {
            raw = raw with { GpuName = raw.GpuName ?? previous.GpuName, GpuTemperatureC = previous.GpuTemperatureC, GpuThermalSampledAtUtc = previous.GpuThermalSampledAtUtc };
            held.Add("temperatura GPU");
        }
        else raw = raw with { GpuThermalSampledAtUtc = gpu ? raw.Timestamp : null };
        if (!cp && raw.CpuPackagePowerW is null && previous is not null && Recent(previous.CpuPowerSampledAtUtc))
        { raw = raw with { CpuPackagePowerW = previous.CpuPackagePowerW, CpuPowerSampledAtUtc = previous.CpuPowerSampledAtUtc }; held.Add("potencia CPU"); }
        else raw = raw with { CpuPowerSampledAtUtc = cp ? raw.Timestamp : null };
        if (!gp && raw.GpuPowerW is null && previous is not null && Recent(previous.GpuPowerSampledAtUtc))
        { raw = raw with { GpuPowerW = previous.GpuPowerW, GpuPowerSampledAtUtc = previous.GpuPowerSampledAtUtc }; held.Add("potencia GPU"); }
        else raw = raw with { GpuPowerSampledAtUtc = gp ? raw.Timestamp : null };
        _last = raw with { ProductTelemetryTolerance = true, RetainedTelemetry = held.Count == 0 ? null : string.Join(", ", held) };
        return _last;
    }
}
