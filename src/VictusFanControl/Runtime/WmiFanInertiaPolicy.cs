using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.Runtime;

// Compatibility facade; no experiment-specific copy of the control algorithm.
internal sealed class WmiFanInertiaPolicy : AdaptiveFanInertiaPolicy
{
    internal WmiFanInertiaPolicy(AdaptiveFanPolicyConfig config, AdaptiveFanTuning? tuning = null) : base(config, tuning) { }
    internal new static AdaptiveFanInertiaSettings Settings => AdaptiveFanInertiaPolicy.Settings;
    internal new AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input) => base.Evaluate(input);
    internal new static double RoundNormalDemandToTenth(double demand) =>
        AdaptiveFanInertiaPolicy.RoundNormalDemandToTenth(demand);
}
