namespace VictusFanControl.Runtime;

internal sealed record WmiFinalDemandFilterSettings(
    double RiseTimeConstantSeconds = 6,
    double FallTimeConstantSeconds = 20,
    double CpuThermalOverrideC = 85,
    double GpuThermalOverrideC = 78,
    string Scope = "final-max-of-six-curves;raw-safety-admission");

/// <summary>One temporal EMA on final demand, never on sensor readings.</summary>
internal sealed class WmiFinalDemandFilter
{
    internal static WmiFinalDemandFilterSettings Settings { get; } = new();
    private DateTimeOffset? _lastTimestamp;
    private double? _level;

    internal void Reset() { _lastTimestamp = null; _level = null; }

    internal double Evaluate(DateTimeOffset timestamp, double rawDemand,
        int minimum, int maximum, TimeSpan maximumGap, bool thermalOverride)
    {
        var elapsed = _lastTimestamp.HasValue ? timestamp - _lastTimestamp.Value : TimeSpan.Zero;
        if (!double.IsFinite(rawDemand) || rawDemand < minimum || rawDemand > maximum ||
            (_lastTimestamp.HasValue && (elapsed <= TimeSpan.Zero || elapsed > maximumGap)))
        {
            Reset();
            throw new InvalidOperationException("Final demand filter refused invalid or discontinuous telemetry.");
        }
        if (!_level.HasValue || thermalOverride)
            _level = rawDemand; // Initial demand is known; never assume a cold machine.
        else
        {
            var tau = rawDemand > _level.Value ? Settings.RiseTimeConstantSeconds : Settings.FallTimeConstantSeconds;
            var alpha = 1 - Math.Exp(-elapsed.TotalSeconds / tau);
            _level += alpha * (rawDemand - _level.Value);
        }
        _lastTimestamp = timestamp;
        return _level.Value;
    }
}
