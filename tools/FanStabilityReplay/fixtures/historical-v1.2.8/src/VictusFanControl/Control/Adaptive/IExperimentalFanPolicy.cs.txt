namespace VictusFanControl.Control.Adaptive;

/// <summary>Explicit research hook. Dispatch, thermal admission and authority remain in the production controller.</summary>
public interface IExperimentalFanPolicy
{
    AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input, AdaptiveFanInertiaDecision baseline);
    void EnsureDispatchAllowed(DateTimeOffset snapshotTimestamp, DateTimeOffset now);
    void ObserveDuringActuation(AdaptiveFanPolicyInput input);
    void Reset();
}

/// <summary>Optional floor, evaluated before the single production filter. Never owns hardware.</summary>
public interface IExperimentalFanSupplement : IExperimentalFanPolicy
{
    bool Enabled { get; }
    double? GetSupplement(AdaptiveFanPolicyInput input, double baselineRawDemand, int? lastAcknowledgedLevel);
}
