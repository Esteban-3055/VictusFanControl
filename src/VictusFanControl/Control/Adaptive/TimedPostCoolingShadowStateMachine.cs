namespace VictusFanControl.Control.Adaptive;

public enum TimedPostCoolingShadowState
{
    Idle,
    HeavyLoad,
    PostCooling
}

public sealed record TimedPostCoolingShadowConfig(
    TimeSpan HeavyLoadQualificationDuration,
    TimeSpan MinimumPostCoolingDuration,
    TimeSpan MaximumPostCoolingDuration,
    double ExitFilteredCpuTemperatureC,
    double ExitTrendCPerSecond,
    int EqualFanLevelFloor);

public sealed record TimedPostCoolingShadowInput(
    DateTimeOffset Timestamp,
    bool LifecycleReady,
    bool HeavyLoadActive,
    double? FilteredCpuTemperatureC,
    double? CpuTrendCPerSecond);

public sealed record TimedPostCoolingShadowDecision(
    TimedPostCoolingShadowState State,
    bool PostCoolingActive,
    int? EqualFanLevelFloor,
    string Detail);

/// <summary>
/// Hardware-independent timed post-cooling state machine. PostCooling is
/// reachable only after a temporally qualified heavy-load episode ends.
/// Ordinary light load never generates an express cooling pulse.
/// </summary>
public sealed class TimedPostCoolingShadowStateMachine
{
    private readonly TimedPostCoolingShadowConfig _config;

    private TimedPostCoolingShadowState _state;
    private DateTimeOffset? _lastTimestamp;
    private DateTimeOffset? _heavyLoadStarted;
    private DateTimeOffset? _postCoolingStarted;
    private bool _heavyLoadQualified;

    public TimedPostCoolingShadowStateMachine(
        TimedPostCoolingShadowConfig config)
    {
        ValidateConfig(config);
        _config = config;
    }

    public TimedPostCoolingShadowState State => _state;

    public void Reset()
    {
        _state = TimedPostCoolingShadowState.Idle;
        _lastTimestamp = null;
        _heavyLoadStarted = null;
        _postCoolingStarted = null;
        _heavyLoadQualified = false;
    }

    public TimedPostCoolingShadowDecision Evaluate(
        TimedPostCoolingShadowInput input)
    {
        if (_lastTimestamp.HasValue &&
            input.Timestamp <= _lastTimestamp.Value)
        {
            Reset();
            return Decision(
                "Post-cooling shadow reset on duplicate/out-of-order telemetry.");
        }

        _lastTimestamp = input.Timestamp;

        if (!input.LifecycleReady)
        {
            Reset();
            return Decision(
                "Post-cooling shadow reset because lifecycle/telemetry readiness was lost.");
        }

        if (input.HeavyLoadActive)
        {
            if (_state != TimedPostCoolingShadowState.HeavyLoad)
            {
                var cameFromPostCooling = _postCoolingStarted.HasValue;

                _state = TimedPostCoolingShadowState.HeavyLoad;
                _heavyLoadStarted = input.Timestamp;
                _heavyLoadQualified = cameFromPostCooling;
                _postCoolingStarted = null;
            }

            _heavyLoadStarted ??= input.Timestamp;

            if (input.Timestamp - _heavyLoadStarted.Value >=
                _config.HeavyLoadQualificationDuration)
            {
                _heavyLoadQualified = true;
            }

            return Decision(
                _heavyLoadQualified
                    ? "Heavy load is qualified; post-cooling will be eligible only after the load ends."
                    : "Heavy load observed but not yet temporally qualified.");
        }

        if (_state == TimedPostCoolingShadowState.HeavyLoad)
        {
            if (!_heavyLoadQualified)
            {
                Reset();
                return Decision(
                    "Heavy load ended before qualification; no post-cooling pulse is permitted.");
            }

            _state = TimedPostCoolingShadowState.PostCooling;
            _postCoolingStarted = input.Timestamp;
            _heavyLoadStarted = null;

            return Decision(
                "Qualified heavy load ended; timed shadow post-cooling started.");
        }

        if (_state != TimedPostCoolingShadowState.PostCooling)
        {
            return Decision(
                "Idle/light-load state; no express cooling pulse is generated.");
        }

        var started = _postCoolingStarted ?? input.Timestamp;
        var elapsed = input.Timestamp - started;

        if (elapsed >= _config.MaximumPostCoolingDuration)
        {
            Reset();
            return Decision(
                "Post-cooling reached its maximum duration and returned to Idle.");
        }

        if (elapsed < _config.MinimumPostCoolingDuration)
        {
            return Decision(
                "Post-cooling minimum hold time is still active.");
        }

        var coolEnough =
            input.FilteredCpuTemperatureC.HasValue &&
            input.CpuTrendCPerSecond.HasValue &&
            input.FilteredCpuTemperatureC.Value <=
                _config.ExitFilteredCpuTemperatureC &&
            input.CpuTrendCPerSecond.Value <=
                _config.ExitTrendCPerSecond;

        if (coolEnough)
        {
            Reset();
            return Decision(
                "Filtered temperature and trend satisfy the post-cooling exit condition.");
        }

        return Decision(
            "Post-cooling remains active while the timed/thermal exit condition is not met.");
    }

    private TimedPostCoolingShadowDecision Decision(
        string detail) =>
        new(
            _state,
            PostCoolingActive:
                _state == TimedPostCoolingShadowState.PostCooling,
            EqualFanLevelFloor:
                _state == TimedPostCoolingShadowState.PostCooling
                    ? _config.EqualFanLevelFloor
                    : null,
            Detail: detail);

    private static void ValidateConfig(
        TimedPostCoolingShadowConfig config)
    {
        if (config.HeavyLoadQualificationDuration < TimeSpan.Zero ||
            config.MinimumPostCoolingDuration < TimeSpan.Zero ||
            config.MaximumPostCoolingDuration <= TimeSpan.Zero ||
            config.MinimumPostCoolingDuration >
                config.MaximumPostCoolingDuration ||
            !double.IsFinite(config.ExitFilteredCpuTemperatureC) ||
            !double.IsFinite(config.ExitTrendCPerSecond) ||
            config.EqualFanLevelFloor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                "Timed post-cooling shadow configuration is invalid.");
        }
    }
}
