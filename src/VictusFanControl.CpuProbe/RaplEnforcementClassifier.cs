namespace VictusFanControl.CpuProbe;

internal sealed record RaplEnforcementResult(
    string Classification,
    string Method,
    int? HighWindowStartSample,
    int? LowWindowStartSample,
    double? HighWindowPowerW,
    double? HighWindowLoadPercent,
    double? LowWindowPowerW,
    double? LowWindowLoadPercent)
{
    internal static RaplEnforcementResult Inconclusive() => new(
        RaplEnforcementClassifier.Inconclusive,
        "NONE", null, null, null, null, null, null);
}

internal static class RaplEnforcementClassifier
{
    internal const string OrderedHighLoadPhases =
        "POWER_LIMIT_PHASES_OBSERVED_UNDER_COMPARABLE_HIGH_LOAD__CAUSAL_REVIEW_REQUIRED";
    internal const string ComparableBaselineDrop =
        "POWER_DROP_OBSERVED_UNDER_COMPARABLE_LOAD__CAUSAL_REVIEW_REQUIRED";
    internal const string Inconclusive =
        "INCONCLUSIVE__REVIEW_SAMPLES_WORKLOAD_AND_TAU";

    private const int WindowSize = 4;
    private const double MinimumHighLoadPercent = 90.0;
    private const double MaximumComparableLoadDelta = 5.0;

    internal static RaplEnforcementResult Assess(
        IReadOnlyList<CpuObservation> baselineSamples,
        IReadOnlyList<CpuObservation> limitedSamples,
        VictusFanControl.Hardware.Intel.IntelRaplPackageLimit target)
    {
        if (!double.IsFinite(target.Pl1.PowerWatts) ||
            !double.IsFinite(target.Pl2.PowerWatts) ||
            target.Pl1.PowerWatts <= 0 ||
            target.Pl2.PowerWatts <= target.Pl1.PowerWatts)
        {
            return RaplEnforcementResult.Inconclusive();
        }

        var windows = BuildHighLoadWindows(limitedSamples);
        var highMinimum = Math.Max(
            target.Pl1.PowerWatts * 1.5,
            target.Pl2.PowerWatts * 0.75);
        var highMaximum = target.Pl2.PowerWatts * 1.15;
        var lowMinimum = target.Pl1.PowerWatts * 0.80;
        var lowMaximum = target.Pl1.PowerWatts * 1.20;

        foreach (var high in windows)
        {
            if (high.PowerW < highMinimum || high.PowerW > highMaximum)
                continue;

            foreach (var low in windows)
            {
                if (low.StartSample <= high.StartSample + WindowSize - 1)
                    continue;
                if (Math.Abs(high.LoadPercent - low.LoadPercent) > MaximumComparableLoadDelta)
                    continue;
                if (low.PowerW < lowMinimum || low.PowerW > lowMaximum)
                    continue;
                if (low.PowerW > high.PowerW * 0.70)
                    continue;

                return new RaplEnforcementResult(
                    OrderedHighLoadPhases,
                    "ORDERED_LIMITED_HIGH_LOAD_WINDOWS",
                    high.StartSample,
                    low.StartSample,
                    high.PowerW,
                    high.LoadPercent,
                    low.PowerW,
                    low.LoadPercent);
            }
        }

        var baseline = AverageComparable(baselineSamples);
        var late = AverageComparable(
            limitedSamples.Skip(Math.Max(0, limitedSamples.Count - 10)).ToArray());
        if (baseline.HasValue && late.HasValue &&
            baseline.Value.LoadPercent >= 60 &&
            late.Value.LoadPercent >= 60 &&
            Math.Abs(baseline.Value.LoadPercent - late.Value.LoadPercent) <= 10 &&
            late.Value.PowerW < baseline.Value.PowerW * 0.90)
        {
            return new RaplEnforcementResult(
                ComparableBaselineDrop,
                "BASELINE_VS_LATE_LIMITED",
                null, null,
                baseline.Value.PowerW,
                baseline.Value.LoadPercent,
                late.Value.PowerW,
                late.Value.LoadPercent);
        }

        return RaplEnforcementResult.Inconclusive();
    }

    private static IReadOnlyList<Window> BuildHighLoadWindows(
        IReadOnlyList<CpuObservation> samples)
    {
        var windows = new List<Window>();
        for (var start = 0; start + WindowSize <= samples.Count; start++)
        {
            var power = 0.0;
            var load = 0.0;
            var valid = true;
            for (var offset = 0; offset < WindowSize; offset++)
            {
                var sample = samples[start + offset];
                if (!sample.PowerW.HasValue ||
                    !sample.LoadPercent.HasValue ||
                    !double.IsFinite(sample.PowerW.Value) ||
                    !double.IsFinite(sample.LoadPercent.Value) ||
                    sample.LoadPercent.Value < MinimumHighLoadPercent)
                {
                    valid = false;
                    break;
                }
                power += sample.PowerW.Value;
                load += sample.LoadPercent.Value;
            }

            if (valid)
            {
                windows.Add(new Window(
                    start + 1,
                    power / WindowSize,
                    load / WindowSize));
            }
        }
        return windows;
    }

    private static (double PowerW, double LoadPercent)? AverageComparable(
        IReadOnlyList<CpuObservation> samples)
    {
        var valid = samples
            .Where(sample =>
                sample.PowerW.HasValue &&
                sample.LoadPercent.HasValue &&
                double.IsFinite(sample.PowerW.Value) &&
                double.IsFinite(sample.LoadPercent.Value))
            .ToArray();
        if (valid.Length == 0)
            return null;

        return (
            valid.Average(sample => sample.PowerW!.Value),
            valid.Average(sample => sample.LoadPercent!.Value));
    }

    private readonly record struct Window(
        int StartSample,
        double PowerW,
        double LoadPercent);
}
