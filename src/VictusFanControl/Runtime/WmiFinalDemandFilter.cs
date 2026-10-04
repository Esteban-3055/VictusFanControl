using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.Runtime;

// Compatibility facade: experiment and prepared production use one filter.
internal sealed class WmiFinalDemandFilter : AdaptiveFinalDemandFilter
{
    internal new static AdaptiveFinalDemandFilterSettings Settings => AdaptiveFinalDemandFilter.Settings;
}
