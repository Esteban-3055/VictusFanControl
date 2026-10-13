using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed record ProductFanReading(double Rpm, DateTimeOffset SampledAt, bool Held);

// Presentation cache only. Never writes a snapshot or makes cached data eligible
// for control, safety, demand history or the measured-RPM graph.
internal sealed class ProductFanReadouts
{
    private ProductFanReading? _cpu, _gpu;

    internal void Observe(ProductRuntimeState state, DateTimeOffset now)
    {
        if (CurrentFanSample(state,now) is not { } snapshot) return;
        var stamp = snapshot.FanSampledAtUtc ?? snapshot.Timestamp;
        Remember(ref _cpu,snapshot.CpuFanRpm,stamp);
        Remember(ref _gpu,snapshot.GpuFanRpm,stamp);
    }

    internal ProductFanReading? Cpu(ProductRuntimeState state, DateTimeOffset now) => Read(_cpu,state,now,true);
    internal ProductFanReading? Gpu(ProductRuntimeState state, DateTimeOffset now) => Read(_gpu,state,now,false);

    private static void Remember(ref ProductFanReading? last, double? rpm, DateTimeOffset stamp)
    {
        if (rpm is {} value && double.IsFinite(value) && value >= 0 && (last is null || stamp > last.SampledAt))
            last = new(value,stamp,false);
    }

    private static ProductFanReading? Read(ProductFanReading? last, ProductRuntimeState state, DateTimeOffset now, bool cpu)
    {
        if (last is null) return null;
        var snapshot = CurrentFanSample(state,now);
        var currentRpm = cpu ? snapshot?.CpuFanRpm : snapshot?.GpuFanRpm;
        var stamp = snapshot?.FanSampledAtUtc ?? snapshot?.Timestamp;
        return last with { Held = stamp != last.SampledAt || currentRpm != last.Rpm };
    }

    private static TelemetrySnapshot? CurrentFanSample(ProductRuntimeState state, DateTimeOffset now) =>
        state.Runtime == "Healthy" && state.Snapshot is {} s && now >= s.Timestamp &&
        now-s.Timestamp <= s.MaximumControlAge && s.IsFanTelemetryFreshAt(now,3000) &&
        (s.FanSampledAtUtc is null || s.FanSampledAtUtc <= now) ? s : null;
}
