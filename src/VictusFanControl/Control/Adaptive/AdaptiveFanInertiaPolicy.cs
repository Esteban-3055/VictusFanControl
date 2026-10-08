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
    private AdaptiveFanTuning? _tuning;
    private readonly AdaptiveLoadHistory _loadHistory = new();
    private int? _current;
    private DateTimeOffset? _increaseSince;
    private int _increaseFloor;
    private DateTimeOffset? _decreaseSince;
    private DateTimeOffset? _lastThermalResponse;
    private double _normalFloor;

    public AdaptiveFanInertiaPolicy(AdaptiveFanPolicyConfig config, AdaptiveFanTuning? tuning = null)
    {
        tuning?.Validate();
        _tuning = tuning;
        _finalFilter = new(tuning);
        _config = config;
        _normalFloor = Math.Clamp(config.UnifiedDemand?.Curve[0].Level ?? config.MinimumLevel, config.MinimumLevel, config.MaximumLevel);
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

    public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input) => Evaluate(input, null);

    internal AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input, double? supplementalDemandLevel)
    {
        // Validate ORIGINAL input and continuity before smoothing. A filter
        // must not turn invalid raw sensors into an accepted policy decision.
        var demand = _demand.Evaluate(input, supplementalDemandLevel);
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
        if (thermalOverride) _lastThermalResponse = input.Timestamp;
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
        if (requested < current)
        {
            // Cooling hold never delays a protected rise. Renew only on fresh raw heat.
            var hold = _tuning?.ThermalDecreaseHoldSeconds ?? 0;
            if (hold > 0 && _lastThermalResponse.HasValue &&
                (input.Timestamp - _lastThermalResponse.Value).TotalSeconds < hold)
            {
                _decreaseSince = null;
                return Accepted($"Holding {current}; thermal cooling hold {hold}s.");
            }
            // Schmitt band prevents boundary hunting; release at the actual cold
            // curve floor so a settled machine can reach its quiet idle level.
            if (requested > _normalFloor && actuationDemand > current - 1 - (_tuning?.NormalDecreaseHysteresisLevels ?? 0))
            {
                _decreaseSince = null;
                return Accepted($"Holding {current}; normal descent hysteresis.");
            }
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
        _normalFloor = Math.Clamp(demand.Curve[0].Level, _config.MinimumLevel, _config.MaximumLevel);
        ClearConfirmation();
    }

    internal void UpdateTuning(AdaptiveFanTuning tuning)
    {
        tuning.Validate();
        if (_tuning is null || tuning.MinimumLevel != _tuning.MinimumLevel || tuning.MaximumLevel != _tuning.MaximumLevel ||
            tuning.MaximumDownStepLevels != _tuning.MaximumDownStepLevels || tuning.ThermalMaximumUpStepLevels != _tuning.ThermalMaximumUpStepLevels)
            throw new InvalidOperationException("El cambio en vivo conserva el rango físico y los pasos protegidos.");
        // Load counted under another definition cannot qualify the new threshold.
        if (tuning.AdaptiveDescentEnabled != _tuning.AdaptiveDescentEnabled || tuning.SustainedLoadSeconds != _tuning.SustainedLoadSeconds ||
            tuning.LoadThresholdPercent != _tuning.LoadThresholdPercent || tuning.CpuLoadPowerThresholdW != _tuning.CpuLoadPowerThresholdW ||
            tuning.GpuLoadPowerThresholdW != _tuning.GpuLoadPowerThresholdW)
            _loadHistory.Reset();
        _finalFilter.UpdateTuning(tuning);
        _tuning = tuning;
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
        if (thermalOverride) _lastThermalResponse = input.Timestamp;
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
        _lastThermalResponse = null;
        ClearConfirmation();
    }

    private void ClearConfirmation()
    {
        _increaseSince = null;
        _decreaseSince = null;
    }
}
