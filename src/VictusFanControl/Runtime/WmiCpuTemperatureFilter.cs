namespace VictusFanControl.Runtime;

internal sealed record WmiCpuTemperatureFilterSettings(
    double RiseTimeConstantSeconds = 1,
    double FallTimeConstantSeconds = 5,
    double BypassAtCpuTemperatureC = 85,
    string Input = "max-package-and-hottest-core",
    string Scope = "experimental-curve-only;raw-safety-admission");

internal readonly record struct WmiCpuTemperatureFilterSample(double TemperatureC, bool Bypassed);

/// <summary>Time-based asymmetric EMA. Never changes a telemetry snapshot.</summary>
internal sealed class WmiCpuTemperatureFilter
{
    internal static WmiCpuTemperatureFilterSettings Settings { get; } = new();
    private DateTimeOffset? _lastTimestamp;
    private double? _temperature;

    internal void Reset()
    {
        _lastTimestamp = null;
        _temperature = null;
    }

    internal WmiCpuTemperatureFilterSample Evaluate(
        DateTimeOffset timestamp, double rawTemperatureC, TimeSpan maximumSampleGap)
    {
        var elapsed = _lastTimestamp.HasValue ? timestamp - _lastTimestamp.Value : TimeSpan.Zero;
        if (!double.IsFinite(rawTemperatureC) || rawTemperatureC is < 0 or > 125 ||
            (_lastTimestamp.HasValue && (elapsed <= TimeSpan.Zero || elapsed > maximumSampleGap)))
        {
            Reset();
            throw new InvalidOperationException("CPU curve filter refused invalid or discontinuous raw telemetry.");
        }

        var bypass = rawTemperatureC >= Settings.BypassAtCpuTemperatureC;
        if (!_temperature.HasValue || bypass)
            _temperature = rawTemperatureC; // No cold-start ramp; high heat ignores history.
        else
        {
            var tau = rawTemperatureC > _temperature.Value
                ? Settings.RiseTimeConstantSeconds : Settings.FallTimeConstantSeconds;
            var alpha = 1 - Math.Exp(-elapsed.TotalSeconds / tau);
            _temperature += alpha * (rawTemperatureC - _temperature.Value);
        }
        _lastTimestamp = timestamp;
        return new(_temperature.Value, bypass);
    }
}
