namespace VictusFanControl.Control.Adaptive;

public sealed record AdaptiveFanInertiaSettings(
    double IncreaseConfirmationSeconds = 2,
    int NormalMaximumUpStepLevels = 1,
    double DecreaseConfirmationSeconds = 12,
    string NormalDemandQuantization = "nearest-tenth;midpoint-down;integer-ceiling;no-extra-deadband",
    string ThermalIncrease = "raw-CPU85-or-GPU78;immediate-with-existing-up-step-limit");

public sealed record AdaptiveFanInertiaDecision(
    bool Accepted,
    int? EqualFanLevel,
    double? RawDemandLevel,
    double? SmoothedDemandLevel,
    double? ActuationDemandLevel,
    bool ThermalOverride,
    string Detail)
{
    public bool SustainedLoadCooling { get; init; }
    public double ObservedLoadSeconds { get; init; }
    public UnifiedDemandObservation? UnifiedDemand { get; init; }
}

/// <summary>
/// Shared final-demand inertia for the experiment and prepared Automatic path.
/// Raw sensors and admission checks remain outside this pure policy.
/// </summary>
public class AdaptiveFanInertiaPolicy
{
    public static AdaptiveFanInertiaSettings Settings { get; } = new();
    private readonly AdaptiveFanPolicyConfig _config;
    private readonly AdaptiveFanPolicyEngine _demand;
    private readonly AdaptiveFinalDemandFilter _finalFilter;
    private readonly AdaptiveFanTuning? _tuning;
    private readonly AdaptiveLoadHistory _loadHistory = new();
    private int? _current;
    private DateTimeOffset? _increaseSince;
    private int _increaseFloor;
    private DateTimeOffset? _decreaseSince;

    public AdaptiveFanInertiaPolicy(AdaptiveFanPolicyConfig config, AdaptiveFanTuning? tuning = null)
    {
        tuning?.Validate();
        _tuning = tuning;
        _finalFilter = new(tuning);
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

    public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input)
    {
        // Validate ORIGINAL input and continuity before smoothing. A filter
        // must not turn invalid raw sensors into an accepted policy decision.
        var demand = _demand.Evaluate(input);
        if (!demand.Accepted || !demand.EqualFanLevel.HasValue)
        {
            ClearConfirmation();
            _finalFilter.Reset();
            _loadHistory.BreakContinuity();
            return new(false, null, demand.RawDemandLevel, null, null, false, demand.Detail);
        }

        // Every normal route (CPU/GPU heat, power, load) passes through the
        // same final-demand filter. Raw sensors are used only for validation,
        // demand calculation and the separately identified thermal override.
        _loadHistory.Observe(input, _tuning);
        var decreaseSeconds = _tuning is { AdaptiveDescentEnabled: true } && !_loadHistory.SustainedLoadCooling
            ? _tuning.ShortLoadDecreaseConfirmationSeconds
            : _tuning?.DecreaseConfirmationSeconds ?? Settings.DecreaseConfirmationSeconds;
        var thermalOverride = (input.CpuRawControlTemperatureC ?? input.CpuEffectiveTemperatureC) >= (_tuning?.CpuThermalOverrideC ?? AdaptiveFinalDemandFilter.Settings.CpuThermalOverrideC) ||
            input.GpuTemperatureC >= (_tuning?.GpuThermalOverrideC ?? AdaptiveFinalDemandFilter.Settings.GpuThermalOverrideC);
        // Raw heat cannot be hidden by a cool core average or a low edited
        // curve. Keep the protected four-level rise and established descent.
        var thermalDemand = thermalOverride && input.CpuRawControlTemperatureC.HasValue
            ? Math.Max(demand.RawDemandLevel!.Value, Math.Min(44, _config.MaximumLevel)) : demand.RawDemandLevel!.Value;
        var smoothed = _finalFilter.Evaluate(input.Timestamp, thermalDemand,
            _config.MinimumLevel, _config.MaximumLevel, _config.MaximumSampleGap, thermalOverride, _loadHistory.SustainedLoadCooling);
        // Keep EMA history at full precision. Quantize only normal actuation;
        // raw thermal override must retain its existing conservative ceiling.
        var actuationDemand = thermalOverride ? smoothed : RoundNormalDemandToTenth(smoothed);
        var requested = Math.Clamp((int)Math.Ceiling(actuationDemand), _config.MinimumLevel, _config.MaximumLevel);
        AdaptiveFanInertiaDecision Accepted(string detail) =>
            new(true, _current, demand.RawDemandLevel, smoothed, actuationDemand, thermalOverride, detail)
            { SustainedLoadCooling = _loadHistory.SustainedLoadCooling, ObservedLoadSeconds = _loadHistory.ObservedLoadSeconds, UnifiedDemand=demand.UnifiedDemand };
        if (!_current.HasValue)
        {
            _current = requested;
            return Accepted($"Initial equal target {requested}/{requested}.");
        }

        var current = _current.Value;
        if (requested > current)
        {
            _decreaseSince = null;
            if (thermalOverride)
            {
                _current = Math.Min(requested, current + _config.MaximumUpStepPerSample);
                ClearConfirmation();
                return Accepted($"Thermal override increase to {_current}; requested={requested}.");
            }

            if (!_increaseSince.HasValue)
            {
                _increaseSince = input.Timestamp;
                _increaseFloor = requested;
            }
            _increaseFloor = Math.Min(_increaseFloor, requested);
            var elapsed = (input.Timestamp - _increaseSince.Value).TotalSeconds;
            if (elapsed >= (_tuning?.IncreaseConfirmationSeconds ?? Settings.IncreaseConfirmationSeconds))
            {
                // Raise only to the demand sustained throughout the window.
                _current = Math.Min(_increaseFloor, current + (_tuning?.NormalMaximumUpStepLevels ?? Settings.NormalMaximumUpStepLevels));
                ClearConfirmation();
                return Accepted($"Confirmed normal increase to {_current}; sustained={elapsed:0.00}s; requested={requested}.");
            }
            return Accepted($"Holding {current}; confirming normal increase {elapsed:0.00}/{_tuning?.IncreaseConfirmationSeconds ?? Settings.IncreaseConfirmationSeconds}s.");
        }

        _increaseSince = null;
        // Tenth-level quantization replaces the experimental full-level
        // deadband, which retained an extra level even at the exact floor.
        if (requested < current)
        {
            _decreaseSince ??= input.Timestamp;
            var elapsed = (input.Timestamp - _decreaseSince.Value).TotalSeconds;
            if (elapsed >= decreaseSeconds)
            {
                _current = Math.Max(requested, current - _config.MaximumDownStepPerSample);
                // Every subsequent step requires a new continuous window.
                ClearConfirmation();
                return Accepted($"Confirmed decrease to {_current}; sustained={elapsed:0.00}s; requested={requested}.");
            }
            return Accepted($"Holding {current}; confirming decrease {elapsed:0.00}/{decreaseSeconds}s.");
        }

        _decreaseSince = null;
        return Accepted($"Holding {current}; requested={requested}.");
    }

    internal void UpdateUnifiedDemand(UnifiedFanDemand demand)
    {
        // Preserve actuation, EMA, telemetry continuity and load history. A confirmation
        // earned with the previous curve must not authorize a step with the new curve.
        _demand.UpdateUnifiedDemand(demand);
        ClearConfirmation();
    }

    /// <summary>Consume real ACK acquisitions without changing the selected fan target.</summary>
    public bool ObserveDuringActuation(AdaptiveFanPolicyInput input, out string failure)
    {
        if (!_current.HasValue)
        {
            failure = "Actuation observation requires an existing policy target.";
            return false;
        }
        var demand = _demand.Evaluate(input);
        if (!demand.Accepted || !demand.RawDemandLevel.HasValue)
        {
            failure = demand.Detail;
            return false;
        }
        _loadHistory.Observe(input, _tuning);
        var thermalOverride = (input.CpuRawControlTemperatureC ?? input.CpuEffectiveTemperatureC) >= (_tuning?.CpuThermalOverrideC ?? AdaptiveFinalDemandFilter.Settings.CpuThermalOverrideC) ||
            input.GpuTemperatureC >= (_tuning?.GpuThermalOverrideC ?? AdaptiveFinalDemandFilter.Settings.GpuThermalOverrideC);
        var thermalDemand = thermalOverride && input.CpuRawControlTemperatureC.HasValue
            ? Math.Max(demand.RawDemandLevel.Value, Math.Min(44, _config.MaximumLevel)) : demand.RawDemandLevel.Value;
        _ = _finalFilter.Evaluate(input.Timestamp, thermalDemand,
            _config.MinimumLevel, _config.MaximumLevel, _config.MaximumSampleGap, thermalOverride, _loadHistory.SustainedLoadCooling);
        // Observations update demand/load history, never actuation or step counts.
        // Start the next confirmation window from a normal policy decision.
        ClearConfirmation();
        failure = string.Empty;
        return true;
    }

    public static double RoundNormalDemandToTenth(double demand)
    {
        if (!double.IsFinite(demand) || demand < 0 || demand > 255)
            throw new ArgumentOutOfRangeException(nameof(demand));
        // Decimal arithmetic expresses decimal ties exactly: 3.05 -> 3.0,
        // 3.06 -> 3.1, and 3.15 -> 3.1 (rather than midpoint-to-even).
        return (double)(Math.Ceiling((decimal)demand * 10m - 0.5m) / 10m);
    }

    public void Reset()
    {
        _demand.Reset();
        _finalFilter.Reset();
        _loadHistory.Reset();
        _current = null;
        ClearConfirmation();
    }

    private void ClearConfirmation()
    {
        _increaseSince = null;
        _decreaseSince = null;
    }
}
