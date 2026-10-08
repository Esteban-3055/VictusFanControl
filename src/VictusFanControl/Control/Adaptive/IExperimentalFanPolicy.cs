namespace VictusFanControl.Control.Adaptive;

/// <summary>Explicit research hook. Dispatch, thermal admission and authority remain in the production controller.</summary>
public interface IExperimentalFanPolicy
{
    AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input, AdaptiveFanInertiaDecision baseline);
    void EnsureDispatchAllowed(DateTimeOffset snapshotTimestamp, DateTimeOffset now);
    void ObserveDuringActuation(AdaptiveFanPolicyInput input);
    void Reset();
}
