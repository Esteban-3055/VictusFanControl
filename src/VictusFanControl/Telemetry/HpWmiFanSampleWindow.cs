namespace VictusFanControl.Telemetry;

internal sealed record HpWmiFanWindowEntry(
    HpWmiFanTelemetrySample Sample, long PublicationSequence, HpWmiFanAcquisitionPurpose Purpose);

internal sealed record HpWmiFanWindowSnapshot(
    HpWmiFanTelemetrySample RawLatest,
    double StableCpuRpm,
    double StableGpuRpm,
    long LatestAgeMilliseconds,
    IReadOnlyList<HpWmiFanWindowEntry> Entries)
{
    internal int WindowCount => Entries.Count;
}

/// <summary>
/// Up to five accepted real acquisitions within one telemetry reader epoch.
/// Caller holds the reader lock. Publication identity prevents replay; no
/// cache reads are acquisitions. Median is descriptive, never command proof.
/// Older entries are history, not independently fresh safety observations.
/// </summary>
internal sealed class HpWmiFanSampleWindow
{
    internal const int Capacity = 5;
    private readonly Queue<HpWmiFanWindowEntry> _entries = new(Capacity);
    private long _lastSequence;

    internal bool TryAppend(HpWmiFanTelemetrySample sample, long sequence,
        HpWmiFanAcquisitionPurpose purpose, long now)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var age = now - sample.StartedAtMilliseconds;
        if (sequence <= _lastSequence || sample.CpuSpeedLevel > 100 || sample.GpuSpeedLevel > 100 ||
            age < 0 || age >= HpWmiFanTelemetryReader.MaximumSampleAgeMilliseconds)
            return false;
        if (_entries.Count > 0)
        {
            var latest = _entries.Last().Sample;
            if (sample.StartedAtMilliseconds < latest.StartedAtMilliseconds) return false;
            // A real freshness gap starts a new window, even when nobody read
            // the old window during that gap. Equal start ticks are allowed:
            // distinct queries can start within the same millisecond.
            var latestAge = now - latest.StartedAtMilliseconds;
            if (latestAge < 0 || latestAge >= HpWmiFanTelemetryReader.MaximumSampleAgeMilliseconds)
                Clear();
        }
        if (_entries.Count == Capacity) _entries.Dequeue();
        _entries.Enqueue(new(sample, sequence, purpose));
        _lastSequence = sequence;
        return true;
    }

    internal HpWmiFanWindowSnapshot? ReadSnapshot(long now)
    {
        if (_entries.Count == 0) return null;
        var latest = _entries.Last().Sample;
        var age = now - latest.StartedAtMilliseconds;
        if (age < 0 || age >= HpWmiFanTelemetryReader.MaximumSampleAgeMilliseconds)
        {
            Clear();
            return null;
        }
        // Copy before exposing: a later append/clear cannot mutate this view.
        var entries = Array.AsReadOnly(_entries.ToArray());
        return new(latest, Median(entries.Select(entry => entry.Sample.CpuNominalRpm)),
            Median(entries.Select(entry => entry.Sample.GpuNominalRpm)), age, entries);
    }

    internal void Clear()
    {
        _entries.Clear();
        // Keep the replay watermark across clear; lifecycle epochs are guarded
        // by the adapter and publication identities continue increasing.
    }

    private static double Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}
