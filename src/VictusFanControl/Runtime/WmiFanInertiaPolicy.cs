using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.Runtime;

internal sealed record WmiFanInertiaSettings(
    double SmallIncreaseConfirmationSeconds = 2,
    int SmallIncreaseMaximumLevels = 2,
    double DecreaseConfirmationSeconds = 12,
    string ThermalIncrease = "immediate-with-existing-up-step-limit");

internal sealed record WmiFanPolicyDecision(
    bool Accepted,
    int? EqualFanLevel,
    double? RawDemandLevel,
    double? CurveDemandLevel,
    double? CpuCurveTemperatureC,
    bool CpuTemperatureFilterBypassed,
    string Detail);

/// <summary>
/// Pure smoothing for the opt-in WMI experiment only. Raw sensors and all
/// admission/thermal handoff checks remain outside this policy, unchanged.
/// </summary>
internal sealed class WmiFanInertiaPolicy
{
    internal static WmiFanInertiaSettings Settings { get; } = new();
    private readonly AdaptiveFanPolicyConfig _config;
    private readonly AdaptiveFanPolicyEngine _demand;
    private readonly WmiCpuTemperatureFilter _cpuFilter = new();
    private int? _current;
    private DateTimeOffset? _increaseSince;
    private int _increaseFloor;
    private DateTimeOffset? _decreaseSince;

    internal WmiFanInertiaPolicy(AdaptiveFanPolicyConfig config)
    {
        _config = config;
        // Reuse curve interpolation and input/continuity validation, but obtain
        // instantaneous demand. Apply inertia exactly once, below.
        _demand = new(config with
        {
            MaximumUpStepPerSample = config.MaximumLevel,
            MaximumDownStepPerSample = config.MaximumLevel,
            DecreaseConfirmationSamples = 1,
            DecreaseDeadbandLevels = 0
        });
    }

    internal WmiFanPolicyDecision Evaluate(AdaptiveFanPolicyInput input)
    {
        // Validate ORIGINAL input and continuity before smoothing. A filter
        // must not turn invalid raw sensors into an accepted policy decision.
        var demand = _demand.Evaluate(input);
        if (!demand.Accepted || !demand.EqualFanLevel.HasValue)
        {
            ClearConfirmation();
            _cpuFilter.Reset();
            return new(false, null, demand.RawDemandLevel, null, null, false, demand.Detail);
        }

        var cpu = _cpuFilter.Evaluate(input.Timestamp, input.CpuEffectiveTemperatureC, _config.MaximumSampleGap);
        // Only CPU temperature is filtered. GPU heat, power and load remain
        // instantaneous and can independently win the maximum-demand rule.
        var curveDemand = new[]
        {
            AdaptiveFanPolicyEngine.Interpolate(_config.CpuTemperatureCurve, cpu.TemperatureC),
            AdaptiveFanPolicyEngine.Interpolate(_config.GpuTemperatureCurve, input.GpuTemperatureC),
            AdaptiveFanPolicyEngine.Interpolate(_config.CpuPowerCurve, input.CpuPackagePowerW),
            AdaptiveFanPolicyEngine.Interpolate(_config.GpuPowerCurve, input.GpuPowerW),
            AdaptiveFanPolicyEngine.Interpolate(_config.CpuLoadCurve, input.CpuLoadPercent),
            AdaptiveFanPolicyEngine.Interpolate(_config.GpuLoadCurve, input.GpuLoadPercent)
        }.Max();
        var requested = Math.Clamp((int)Math.Ceiling(curveDemand), _config.MinimumLevel, _config.MaximumLevel);
        WmiFanPolicyDecision Accepted(string detail) =>
            new(true, _current, demand.RawDemandLevel, curveDemand, cpu.TemperatureC, cpu.Bypassed, detail);
        if (!_current.HasValue)
        {
            _current = requested;
            return Accepted($"Initial equal target {requested}/{requested}.");
        }

        var current = _current.Value;
        if (requested > current)
        {
            _decreaseSince = null;
            var thermalDemand = Math.Max(
                AdaptiveFanPolicyEngine.Interpolate(_config.CpuTemperatureCurve, cpu.TemperatureC),
                AdaptiveFanPolicyEngine.Interpolate(_config.GpuTemperatureCurve, input.GpuTemperatureC));
            var thermalIncrease = Math.Ceiling(thermalDemand) > current;
            if (thermalIncrease || requested - current > Settings.SmallIncreaseMaximumLevels)
            {
                _current = Math.Min(requested, current + _config.MaximumUpStepPerSample);
                ClearConfirmation();
                return Accepted($"Immediate {(thermalIncrease ? "thermal" : "large-demand")} increase to {_current}; requested={requested}.");
            }

            if (!_increaseSince.HasValue)
            {
                _increaseSince = input.Timestamp;
                _increaseFloor = requested;
            }
            _increaseFloor = Math.Min(_increaseFloor, requested);
            var elapsed = (input.Timestamp - _increaseSince.Value).TotalSeconds;
            if (elapsed >= Settings.SmallIncreaseConfirmationSeconds)
            {
                // Raise only to the demand sustained throughout the window.
                _current = Math.Min(_increaseFloor, current + _config.MaximumUpStepPerSample);
                ClearConfirmation();
                return Accepted($"Confirmed small increase to {_current}; sustained={elapsed:0.00}s; requested={requested}.");
            }
            return Accepted($"Holding {current}; confirming small increase {elapsed:0.00}/{Settings.SmallIncreaseConfirmationSeconds}s.");
        }

        _increaseSince = null;
        if (requested < current - _config.DecreaseDeadbandLevels)
        {
            _decreaseSince ??= input.Timestamp;
            var elapsed = (input.Timestamp - _decreaseSince.Value).TotalSeconds;
            if (elapsed >= Settings.DecreaseConfirmationSeconds)
            {
                _current = Math.Max(requested, current - _config.MaximumDownStepPerSample);
                // Every subsequent step requires a new continuous window.
                ClearConfirmation();
                return Accepted($"Confirmed decrease to {_current}; sustained={elapsed:0.00}s; requested={requested}.");
            }
            return Accepted($"Holding {current}; confirming decrease {elapsed:0.00}/{Settings.DecreaseConfirmationSeconds}s.");
        }

        _decreaseSince = null;
        return Accepted($"Holding {current} inside deadband; requested={requested}.");
    }

    private void ClearConfirmation()
    {
        _increaseSince = null;
        _decreaseSince = null;
    }
}
