namespace VictusFanControl.Control.Adaptive;

/// <summary>Prepared Automatic envelope; Manual and stored curve schemas retain 10..50.</summary>
public static class Hp8C40AutomaticPolicy
{
    public const int MinimumLevel = 30;
    public const int MaximumLevel = 50;

    public static AdaptiveFanPolicyConfig Create(AdaptiveFanPolicyConfig? candidate = null)
    {
        candidate ??= Hp8C40AdaptiveCandidateV1.Create();
        // Validate the original curves before clamping; invalid profiles must not
        // become valid merely because the prepared envelope hides their values.
        _ = new AdaptiveFanPolicyEngine(candidate);
        IReadOnlyList<AdaptiveFanCurvePoint> Clamp(IReadOnlyList<AdaptiveFanCurvePoint> points) =>
            points.Select(p => p with { Level = Math.Clamp(p.Level, MinimumLevel, MaximumLevel) }).ToArray();
        return candidate with
        {
            MinimumLevel = MinimumLevel, MaximumLevel = MaximumLevel,
            MaximumUpStepPerSample = 4, MaximumDownStepPerSample = 1,
            MaximumSampleGap = TimeSpan.FromSeconds(3),
            CpuTemperatureCurve = Clamp(candidate.CpuTemperatureCurve),
            GpuTemperatureCurve = Clamp(candidate.GpuTemperatureCurve),
            CpuPowerCurve = Clamp(candidate.CpuPowerCurve),
            GpuPowerCurve = Clamp(candidate.GpuPowerCurve),
            CpuLoadCurve = Clamp(candidate.CpuLoadCurve),
            GpuLoadCurve = Clamp(candidate.GpuLoadCurve)
        };
    }
}
