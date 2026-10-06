using VictusFanControl.Control.Adaptive;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Product;

/// <summary>Visual observations only: draft interpolation is never an executed fan request.</summary>
public sealed record ProductCurveMarker(AdaptiveCurveAxis Axis, double Input, double Level, bool IsApplied);

public static class ProductCurveMarkers
{
    public static double? Input(TelemetrySnapshot snapshot, AdaptiveCurveAxis axis, AdaptiveFanTuning tuning) => axis switch
    {
        AdaptiveCurveAxis.CpuTemperature => CpuDemandTemperature.Select(snapshot, tuning.CpuTemperatureSource, tuning.HottestPerformanceCoreCount),
        AdaptiveCurveAxis.GpuTemperature => snapshot.GpuTemperatureC,
        AdaptiveCurveAxis.CpuPower => snapshot.CpuPackagePowerW,
        AdaptiveCurveAxis.GpuPower => snapshot.GpuPowerW,
        AdaptiveCurveAxis.CpuLoad => snapshot.CpuLoadPercent,
        AdaptiveCurveAxis.GpuLoad => snapshot.GpuLoadPercent,
        _ => null
    };

    public static IReadOnlyList<ProductCurveMarker> Build(FanConfiguration draft, TelemetrySnapshot? live,
        FanConfiguration? applied, TelemetrySnapshot? decisionSnapshot, int? requestedLevel, params AdaptiveCurveAxis[] axes)
    {
        var markers = new List<ProductCurveMarker>();
        var policy = draft.BuildPolicy();
        foreach (var axis in axes.Distinct())
        {
            if (live is not null && Input(live, axis, draft.Tuning) is { } previewInput && double.IsFinite(previewInput) && previewInput >= 0)
                markers.Add(new(axis, previewInput, AdaptiveCurveProfiles.Interpolate(AdaptiveCurveProfiles.Curve(policy, axis), previewInput), false));
            if (applied is not null && decisionSnapshot is not null && requestedLevel is >= 10 and <= 50 &&
                Input(decisionSnapshot, axis, applied.Tuning) is { } appliedInput && double.IsFinite(appliedInput) && appliedInput >= 0)
                markers.Add(new(axis, appliedInput, requestedLevel.Value, true));
        }
        return markers;
    }
}
