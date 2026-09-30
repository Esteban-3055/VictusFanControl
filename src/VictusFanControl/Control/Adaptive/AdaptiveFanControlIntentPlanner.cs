namespace VictusFanControl.Control.Adaptive;

public enum AdaptiveFanControlIntentKind
{
    HoldFirmware = 0,
    EnterCustomAndApply = 1,
    ApplyChangedLevel = 2,
    HoldCustom = 3,
    ReleaseToFirmware = 4
}

public sealed record AdaptiveFanControlIntent(
    AdaptiveFanControlIntentKind Kind,
    int? EqualFanLevel,
    string Detail);

/// <summary>
/// Pure notional authority planner used by shadow/replay evaluation.
///
/// It never owns real fan authority and cannot execute an intent. It only
/// converts accepted policy decisions into the minimal action a future
/// production adapter would need to perform.
/// </summary>
public sealed class AdaptiveFanControlIntentPlanner
{
    private bool _notionalCustom;
    private int? _lastAppliedLevel;

    public bool NotionalCustom => _notionalCustom;
    public int? LastAppliedLevel => _lastAppliedLevel;

    public void Reset()
    {
        _notionalCustom = false;
        _lastAppliedLevel = null;
    }

    public AdaptiveFanControlIntent Plan(
        bool controlPreconditionsReady,
        AdaptiveFanPolicyDecision? decision)
    {
        if (!controlPreconditionsReady ||
            decision is null ||
            !decision.Accepted ||
            !decision.EqualFanLevel.HasValue)
        {
            if (_notionalCustom)
            {
                Reset();

                return new AdaptiveFanControlIntent(
                    AdaptiveFanControlIntentKind.ReleaseToFirmware,
                    EqualFanLevel: null,
                    Detail:
                        "Shadow planner would release Custom authority because " +
                        "safety/policy admission is unavailable.");
            }

            return new AdaptiveFanControlIntent(
                AdaptiveFanControlIntentKind.HoldFirmware,
                EqualFanLevel: null,
                Detail:
                    "Shadow planner would remain firmware-owned because " +
                    "safety/policy admission is unavailable.");
        }

        var level = decision.EqualFanLevel.Value;

        if (!_notionalCustom)
        {
            _notionalCustom = true;
            _lastAppliedLevel = level;

            return new AdaptiveFanControlIntent(
                AdaptiveFanControlIntentKind.EnterCustomAndApply,
                level,
                $"Shadow planner would enter Custom and apply equal {level}/{level}.");
        }

        if (_lastAppliedLevel != level)
        {
            _lastAppliedLevel = level;

            return new AdaptiveFanControlIntent(
                AdaptiveFanControlIntentKind.ApplyChangedLevel,
                level,
                $"Shadow planner would apply changed equal target {level}/{level}.");
        }

        return new AdaptiveFanControlIntent(
            AdaptiveFanControlIntentKind.HoldCustom,
            level,
            $"Shadow planner would hold equal {level}/{level} without retransmission.");
    }
}
