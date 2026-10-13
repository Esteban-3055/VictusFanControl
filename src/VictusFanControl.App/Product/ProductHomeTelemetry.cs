using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed record ProductHomeDemandSample(DateTimeOffset Timestamp, double Percent, string Source, string Session, bool StartsSegment);

/// <summary>Presentation history of accepted controller observations. Never evaluates a draft or issues commands.</summary>
internal sealed class ProductHomeTelemetry
{
    private readonly List<ProductHomeDemandSample> _samples = [];
    private bool _breakNext = true;
    internal IReadOnlyList<ProductHomeDemandSample> Samples => _samples;

    internal static TelemetrySnapshot? FanSnapshot(ProductRuntimeState state, DateTimeOffset now) =>
        state.Runtime == "Healthy" && state.Snapshot is { IsComplete:true } s && now >= s.Timestamp &&
        now-s.Timestamp <= s.MaximumControlAge && s.HasFreshControlSensorsAt(now) && s.IsFanTelemetryFreshAt(now,3000) ? s : null;

    internal static ProductHomeDemandSample? CurrentDemand(ProductRuntimeState state, DateTimeOffset now)
        => ReadDemand(state,now,requireLiveSnapshot:true);

    // Keep the last accepted decision between acquisitions. A delayed/missing live RPM
    // must not restart presentation inertia; the decision's own age still expires it.
    internal static ProductHomeDemandSample? AnimationDemand(ProductRuntimeState state, DateTimeOffset now)
        => ReadDemand(state,now,requireLiveSnapshot:false);

    // The request is the level already accepted by the controller, not the demand
    // percentage or a draft curve. Keep the existing decision/lifecycle admission.
    internal static int? AnimationRequest(ProductRuntimeState state, DateTimeOffset now)
        => AnimationDemand(state,now) is not null && state.FanLevel is >=10 and <=50 ? state.FanLevel : null;

    private static ProductHomeDemandSample? ReadDemand(ProductRuntimeState state, DateTimeOffset now, bool requireLiveSnapshot)
    {
        if (state.Runtime != "Healthy" || state.FanMode != "Automatic" || state.FanAuthority != "Custom" ||
            state.LifecycleBlocked || state.AutomaticPreparing || state.AutomaticSourceTransition is not null ||
            state.Source is not ("Ac" or "Battery") || state.AppliedFanProfile != state.Source ||
            string.IsNullOrWhiteSpace(state.AutomaticSessionId) || state.AppliedAutomaticConfiguration is null ||
            (requireLiveSnapshot && (state.Snapshot is not { IsComplete:true } live || now < live.Timestamp ||
                now-live.Timestamp > live.MaximumControlAge || !live.HasFreshControlSensorsAt(now))) ||
            state.AutomaticDecisionSnapshot is not { IsComplete:true } sample ||
            now < sample.Timestamp || now-sample.Timestamp > sample.MaximumControlAge || !sample.HasFreshControlSensorsAt(now) ||
            state.AutomaticDecision is not { ExecutionAuthorized:true, EqualFanLevel:not null, UnifiedDemand: {} demand } decision ||
            decision.EqualFanLevel != state.FanLevel || !double.IsFinite(demand.Percent) || demand.Percent is <0 or >100) return null;
        return new(sample.Timestamp,demand.Percent,state.Source,state.AutomaticSessionId,false);
    }

    internal void Observe(ProductRuntimeState state, DateTimeOffset now)
    {
        _samples.RemoveAll(s => now-s.Timestamp > TimeSpan.FromMinutes(5));
        var sample = CurrentDemand(state,now);
        if (sample is null) { _breakNext=true; return; }
        var last = _samples.LastOrDefault();
        if (last is not null && sample.Timestamp <= last.Timestamp) return;
        sample = sample with { StartsSegment = _breakNext || last is null || sample.Session != last.Session ||
            sample.Source != last.Source || sample.Timestamp-last.Timestamp > TimeSpan.FromSeconds(3) };
        _samples.Add(sample); _breakNext=false;
        if (_samples.Count > 600) _samples.RemoveRange(0,_samples.Count-600);
    }
}
