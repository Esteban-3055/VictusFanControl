namespace VictusFanControl.Control.Adaptive;

public sealed record AdaptiveFinalDemandFilterSettings(
    double RiseTimeConstantSeconds = 6,
    double FallTimeConstantSeconds = 20,
    double CpuThermalOverrideC = 85,
    double GpuThermalOverrideC = 78,
    string Scope = "final-max-of-six-curves;raw-safety-admission");

/// <summary>One temporal EMA on final demand, never on sensor readings.</summary>
public class AdaptiveFinalDemandFilter
{
    public static AdaptiveFinalDemandFilterSettings Settings { get; } = new();
    private AdaptiveFanTuning? _tuning;
    internal void UpdateTuning(AdaptiveFanTuning tuning) { tuning.Validate(); _tuning = tuning; }
    public AdaptiveFinalDemandFilter(AdaptiveFanTuning? tuning = null)
    {
        tuning?.Validate();
        _tuning = tuning;
    }
    private DateTimeOffset? _lastTimestamp;
    private double? _level;

    public void Reset() { _lastTimestamp = null; _level = null; }

    public double Evaluate(DateTimeOffset timestamp, double rawDemand,
        int minimum, int maximum, TimeSpan maximumGap, bool thermalOverride, bool sustainedLoadCooling = false)
    {
        var elapsed = _lastTimestamp.HasValue ? timestamp - _lastTimestamp.Value : TimeSpan.Zero;
        if (!double.IsFinite(rawDemand) || rawDemand < minimum || rawDemand > maximum ||
            (_lastTimestamp.HasValue && (elapsed <= TimeSpan.Zero || elapsed > maximumGap)))
        {
            Reset();
            throw new InvalidOperationException("Final demand filter refused invalid or discontinuous telemetry.");
        }
        if (thermalOverride && _tuning is { RememberThermalDemand: false })
        {
            // Act on raw heat immediately without seeding normal EMA history
            // with the spike. Normal demand resumes from its own history.
            _lastTimestamp = timestamp;
            return rawDemand;
        }
        if (!_level.HasValue || thermalOverride)
            _level = rawDemand; // Initial demand is known; never assume a cold machine.
        else
        {
            var tau = rawDemand > _level.Value
                ? _tuning?.RiseTimeConstantSeconds ?? Settings.RiseTimeConstantSeconds
                : _tuning is { AdaptiveDescentEnabled: true } && !sustainedLoadCooling
                    ? _tuning.ShortLoadFallTimeConstantSeconds
                    : _tuning?.FallTimeConstantSeconds ?? Settings.FallTimeConstantSeconds;
            var alpha = 1 - Math.Exp(-elapsed.TotalSeconds / tau);
            _level += alpha * (rawDemand - _level.Value);
        }
        _lastTimestamp = timestamp;
        return _level.Value;
    }
}
