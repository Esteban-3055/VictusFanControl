using VictusFanControl.Control.Adaptive;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Product;

/// <summary>Visual observations only: draft interpolation is never an executed fan request.</summary>
public sealed record ProductCurveMarker(AdaptiveCurveAxis Axis, double Input, double Level, bool IsApplied);
public sealed record ProductDemandMarker(double Input,double Level,bool IsApplied,UnifiedDemandObservation Observation);

public static class ProductCurveMarkers
{
    public static UnifiedDemandObservation? Demand(FanConfiguration configuration,TelemetrySnapshot snapshot)
    {
        if(configuration.UnifiedDemand is null||CpuDemandTemperature.Select(snapshot,configuration.Tuning.CpuTemperatureSource,configuration.Tuning.HottestPerformanceCoreCount) is not { } cpu||
            snapshot.CpuPackagePowerW is not { } cpuW||snapshot.CpuLoadPercent is not { } cpuLoad||snapshot.GpuTemperatureC is not { } gpu||snapshot.GpuPowerW is not { } gpuW||snapshot.GpuLoadPercent is not { } gpuLoad||snapshot.CpuControlTemperatureC is not { } rawCpu)return null;
        var input=new AdaptiveFanPolicyInput(snapshot.Timestamp,cpu,cpuW,cpuLoad,gpu,gpuW,gpuLoad){CpuRawControlTemperatureC=rawCpu};
        if(!AdaptiveFanPolicyEngine.ValidateInput(input,out _))return null;
        return configuration.UnifiedDemand.Evaluate(input);
    }
    public static IReadOnlyList<ProductDemandMarker> BuildUnified(FanConfiguration draft,TelemetrySnapshot? live,FanConfiguration? applied,TelemetrySnapshot? decisionSnapshot,int? requestedLevel)
    {
        var markers=new List<ProductDemandMarker>();
        if(live is not null&&Demand(draft,live) is { } preview)markers.Add(new(preview.Percent,preview.Level,false,preview));
        if(applied is not null&&decisionSnapshot is not null&&requestedLevel is >=10 and <=50&&Demand(applied,decisionSnapshot) is { } accepted)
            markers.Add(new(accepted.Percent,requestedLevel.Value,true,accepted));
        return markers;
    }
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
