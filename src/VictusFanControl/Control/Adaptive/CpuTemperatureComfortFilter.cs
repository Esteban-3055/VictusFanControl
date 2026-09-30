using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

public sealed record CpuTemperatureComfortFilterResult(
    bool Accepted,
    bool Ready,
    int SamplesCollected,
    double? InstantaneousEffectiveTemperatureC,
    double? FilteredTemperatureC,
    double? TrendCPerSecond,
    string Detail);

/// <summary>
/// Comfort-only CPU temperature preprocessing for adaptive-policy shadow work.
/// SafetyGate must evaluate raw telemetry before this type is consulted.
/// </summary>
public sealed class CpuTemperatureComfortFilter
{
    public const int WindowSize = 5;

    private readonly TimeSpan _maximumSampleGap;
    private readonly TimeSpan _maximumSampleAge;
    private readonly Queue<Sample> _window = new();
    private DateTimeOffset? _lastTimestamp;

    public CpuTemperatureComfortFilter(
        TimeSpan maximumSampleGap,
        TimeSpan? maximumSampleAge = null)
    {
        if (maximumSampleGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleGap));
        }

        var resolvedAge = maximumSampleAge ?? maximumSampleGap;

        if (resolvedAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleAge));
        }

        _maximumSampleGap = maximumSampleGap;
        _maximumSampleAge = resolvedAge;
    }

    public int SamplesCollected => _window.Count;

    public void Reset()
    {
        _window.Clear();
        _lastTimestamp = null;
    }

    public CpuTemperatureComfortFilterResult Evaluate(
        TelemetrySnapshot snapshot,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var effective = snapshot.CpuControlTemperatureC;

        if (!snapshot.CpuCoreTelemetryComplete ||
            !effective.HasValue ||
            !double.IsFinite(effective.Value))
        {
            Reset();
            return Rejected(
                effective,
                "CPU comfort filter refused incomplete or invalid raw CPU telemetry.");
        }

        var age = evaluatedAt - snapshot.Timestamp;

        if (age < TimeSpan.FromSeconds(-1) ||
            age > _maximumSampleAge)
        {
            Reset();
            return Rejected(
                effective,
                "CPU comfort filter refused a stale or future-dated snapshot.");
        }

        if (_lastTimestamp.HasValue)
        {
            if (snapshot.Timestamp == _lastTimestamp.Value)
            {
                return Rejected(
                    effective,
                    "CPU comfort filter refused a duplicate snapshot timestamp without consuming it.");
            }

            if (snapshot.Timestamp < _lastTimestamp.Value)
            {
                Reset();
                return Rejected(
                    effective,
                    "CPU comfort filter refused out-of-order telemetry and reset its temporal window.");
            }
        }

        var continuityReset =
            _lastTimestamp.HasValue &&
            snapshot.Timestamp - _lastTimestamp.Value > _maximumSampleGap;

        if (continuityReset)
        {
            Reset();
        }

        _lastTimestamp = snapshot.Timestamp;

        _window.Enqueue(
            new Sample(
                snapshot.Timestamp,
                effective.Value));

        while (_window.Count > WindowSize)
        {
            _window.Dequeue();
        }

        var trend = CalculateTrendCPerSecond(_window);

        if (_window.Count < WindowSize)
        {
            return new CpuTemperatureComfortFilterResult(
                Accepted: true,
                Ready: false,
                SamplesCollected: _window.Count,
                InstantaneousEffectiveTemperatureC: effective.Value,
                FilteredTemperatureC: null,
                TrendCPerSecond: trend,
                Detail:
                    continuityReset
                        ? "CPU comfort filter continuity reset; collecting a new five-snapshot window."
                        : "CPU comfort filter is collecting five fresh unique snapshots.");
        }

        var ordered =
            _window
                .Select(sample => sample.EffectiveTemperatureC)
                .OrderBy(value => value)
                .ToArray();

        var median = ordered[WindowSize / 2];

        return new CpuTemperatureComfortFilterResult(
            Accepted: true,
            Ready: true,
            SamplesCollected: WindowSize,
            InstantaneousEffectiveTemperatureC: effective.Value,
            FilteredTemperatureC: median,
            TrendCPerSecond: trend,
            Detail:
                "CPU comfort signal ready: temporal median of five fresh effective-temperature snapshots.");
    }

    private CpuTemperatureComfortFilterResult Rejected(
        double? instantaneous,
        string detail) =>
        new(
            Accepted: false,
            Ready: false,
            SamplesCollected: _window.Count,
            InstantaneousEffectiveTemperatureC: instantaneous,
            FilteredTemperatureC: null,
            TrendCPerSecond: null,
            Detail: detail);

    private static double? CalculateTrendCPerSecond(
        IEnumerable<Sample> samples)
    {
        var data = samples.ToArray();

        if (data.Length < 2)
        {
            return null;
        }

        var origin = data[0].Timestamp;
        var x =
            data.Select(
                    sample =>
                        (sample.Timestamp - origin).TotalSeconds)
                .ToArray();
        var y =
            data.Select(
                    sample =>
                        sample.EffectiveTemperatureC)
                .ToArray();

        var meanX = x.Average();
        var meanY = y.Average();
        var numerator = 0.0;
        var denominator = 0.0;

        for (var index = 0; index < data.Length; index++)
        {
            var dx = x[index] - meanX;
            numerator += dx * (y[index] - meanY);
            denominator += dx * dx;
        }

        return denominator <= 0
            ? null
            : numerator / denominator;
    }

    private readonly record struct Sample(
        DateTimeOffset Timestamp,
        double EffectiveTemperatureC);
}
