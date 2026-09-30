namespace VictusFanControl.Control.Adaptive;

public sealed record AdaptiveFanCurvePoint(
    double Input,
    double Level);

public sealed record AdaptiveFanPolicyConfig(
    int MinimumLevel,
    int MaximumLevel,
    int MaximumUpStepPerSample,
    int MaximumDownStepPerSample,
    int DecreaseConfirmationSamples,
    double DecreaseDeadbandLevels,
    TimeSpan MaximumSampleGap,
    IReadOnlyList<AdaptiveFanCurvePoint> CpuTemperatureCurve,
    IReadOnlyList<AdaptiveFanCurvePoint> GpuTemperatureCurve,
    IReadOnlyList<AdaptiveFanCurvePoint> CpuPowerCurve,
    IReadOnlyList<AdaptiveFanCurvePoint> GpuPowerCurve,
    IReadOnlyList<AdaptiveFanCurvePoint> CpuLoadCurve,
    IReadOnlyList<AdaptiveFanCurvePoint> GpuLoadCurve);

public sealed record AdaptiveFanPolicyInput(
    DateTimeOffset Timestamp,
    double CpuEffectiveTemperatureC,
    double CpuPackagePowerW,
    double CpuLoadPercent,
    double GpuTemperatureC,
    double GpuPowerW,
    double GpuLoadPercent);

public sealed record AdaptiveFanPolicyDecision(
    bool Accepted,
    int? EqualFanLevel,
    double? RawDemandLevel,
    string Detail);

/// <summary>
/// Pure, hardware-independent adaptive fan-policy engine.
///
/// It produces one equal CPU/GPU fan level. It has no backend, watchdog,
/// WMI, EC or lifecycle authority and is intentionally not wired into the
/// production runtime while M8 remains physically open.
/// </summary>
public sealed class AdaptiveFanPolicyEngine
{
    private readonly AdaptiveFanPolicyConfig _config;

    private int? _currentLevel;
    private DateTimeOffset? _lastTimestamp;
    private int _consecutiveDecreaseSamples;

    public AdaptiveFanPolicyEngine(
        AdaptiveFanPolicyConfig config)
    {
        ValidateConfig(config);
        _config = config;
    }

    public int? CurrentLevel => _currentLevel;

    public void Reset()
    {
        _currentLevel = null;
        _lastTimestamp = null;
        _consecutiveDecreaseSamples = 0;
    }

    public AdaptiveFanPolicyDecision Evaluate(
        AdaptiveFanPolicyInput input)
    {
        if (!ValidateInput(input, out var inputFailure))
        {
            _consecutiveDecreaseSamples = 0;
            return new AdaptiveFanPolicyDecision(
                Accepted: false,
                EqualFanLevel: null,
                RawDemandLevel: null,
                Detail: inputFailure);
        }

        if (_lastTimestamp.HasValue)
        {
            if (input.Timestamp <= _lastTimestamp.Value)
            {
                _consecutiveDecreaseSamples = 0;
                return new AdaptiveFanPolicyDecision(
                    Accepted: false,
                    EqualFanLevel: null,
                    RawDemandLevel: null,
                    Detail:
                        "Adaptive policy refused duplicate/out-of-order telemetry.");
            }

            if (input.Timestamp - _lastTimestamp.Value >
                _config.MaximumSampleGap)
            {
                _lastTimestamp = input.Timestamp;
                _consecutiveDecreaseSamples = 0;

                return new AdaptiveFanPolicyDecision(
                    Accepted: false,
                    EqualFanLevel: null,
                    RawDemandLevel: null,
                    Detail:
                        "Adaptive policy refused a telemetry continuity gap.");
            }
        }

        _lastTimestamp = input.Timestamp;

        var rawDemand = new[]
        {
            Interpolate(
                _config.CpuTemperatureCurve,
                input.CpuEffectiveTemperatureC),
            Interpolate(
                _config.GpuTemperatureCurve,
                input.GpuTemperatureC),
            Interpolate(
                _config.CpuPowerCurve,
                input.CpuPackagePowerW),
            Interpolate(
                _config.GpuPowerCurve,
                input.GpuPowerW),
            Interpolate(
                _config.CpuLoadCurve,
                input.CpuLoadPercent),
            Interpolate(
                _config.GpuLoadCurve,
                input.GpuLoadPercent)
        }.Max();

        rawDemand = Math.Clamp(
            rawDemand,
            _config.MinimumLevel,
            _config.MaximumLevel);

        var requested =
            Math.Clamp(
                (int)Math.Ceiling(rawDemand),
                _config.MinimumLevel,
                _config.MaximumLevel);

        if (!_currentLevel.HasValue)
        {
            _currentLevel = requested;
            _consecutiveDecreaseSamples = 0;

            return Accepted(
                rawDemand,
                $"Initial equal target {_currentLevel}/{_currentLevel}.");
        }

        var current = _currentLevel.Value;

        if (requested > current)
        {
            _consecutiveDecreaseSamples = 0;
            _currentLevel = Math.Min(
                requested,
                current + _config.MaximumUpStepPerSample);

            return Accepted(
                rawDemand,
                $"Rising equal target {_currentLevel}/{_currentLevel}; " +
                $"requested={requested}.");
        }

        if (requested <
            current - _config.DecreaseDeadbandLevels)
        {
            _consecutiveDecreaseSamples++;

            if (_consecutiveDecreaseSamples >=
                _config.DecreaseConfirmationSamples)
            {
                _currentLevel = Math.Max(
                    requested,
                    current - _config.MaximumDownStepPerSample);

                _consecutiveDecreaseSamples = 0;

                return Accepted(
                    rawDemand,
                    $"Confirmed falling equal target {_currentLevel}/{_currentLevel}; " +
                    $"requested={requested}.");
            }

            return Accepted(
                rawDemand,
                $"Holding equal target {current}/{current} while confirming decrease " +
                $"({_consecutiveDecreaseSamples}/{_config.DecreaseConfirmationSamples}).");
        }

        _consecutiveDecreaseSamples = 0;

        return Accepted(
            rawDemand,
            $"Holding equal target {current}/{current} inside decrease deadband.");
    }

    internal static double Interpolate(
        IReadOnlyList<AdaptiveFanCurvePoint> curve,
        double input)
    {
        if (input <= curve[0].Input)
        {
            return curve[0].Level;
        }

        for (var index = 1; index < curve.Count; index++)
        {
            var right = curve[index];
            if (input > right.Input)
            {
                continue;
            }

            var left = curve[index - 1];
            var span = right.Input - left.Input;
            var position = (input - left.Input) / span;

            return left.Level +
                   ((right.Level - left.Level) * position);
        }

        return curve[^1].Level;
    }

    private AdaptiveFanPolicyDecision Accepted(
        double rawDemand,
        string detail) =>
        new(
            Accepted: true,
            EqualFanLevel: _currentLevel,
            RawDemandLevel: rawDemand,
            Detail: detail);

    private static void ValidateConfig(
        AdaptiveFanPolicyConfig config)
    {
        if (config.MinimumLevel <= 0 ||
            config.MaximumLevel < config.MinimumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                "Adaptive policy fan-level envelope is invalid.");
        }

        if (config.MaximumUpStepPerSample <= 0 ||
            config.MaximumDownStepPerSample <= 0 ||
            config.DecreaseConfirmationSamples <= 0 ||
            config.DecreaseDeadbandLevels < 0 ||
            config.MaximumSampleGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                "Adaptive policy smoothing parameters are invalid.");
        }

        foreach (var curve in EnumerateCurves(config))
        {
            ValidateCurve(
                curve,
                config.MinimumLevel,
                config.MaximumLevel);
        }
    }

    private static IEnumerable<IReadOnlyList<AdaptiveFanCurvePoint>>
        EnumerateCurves(AdaptiveFanPolicyConfig config)
    {
        yield return config.CpuTemperatureCurve;
        yield return config.GpuTemperatureCurve;
        yield return config.CpuPowerCurve;
        yield return config.GpuPowerCurve;
        yield return config.CpuLoadCurve;
        yield return config.GpuLoadCurve;
    }

    private static void ValidateCurve(
        IReadOnlyList<AdaptiveFanCurvePoint> curve,
        int minimumLevel,
        int maximumLevel)
    {
        if (curve.Count < 2)
        {
            throw new ArgumentException(
                "Every adaptive policy curve requires at least two points.");
        }

        var previousInput = double.NegativeInfinity;
        var previousLevel = double.NegativeInfinity;

        foreach (var point in curve)
        {
            if (!double.IsFinite(point.Input) ||
                !double.IsFinite(point.Level) ||
                point.Input <= previousInput ||
                point.Level < previousLevel ||
                point.Level < minimumLevel ||
                point.Level > maximumLevel)
            {
                throw new ArgumentException(
                    "Adaptive policy curves must be finite, strictly increasing by input, " +
                    "nondecreasing by fan level and contained in the configured fan envelope.");
            }

            previousInput = point.Input;
            previousLevel = point.Level;
        }
    }

    private static bool ValidateInput(
        AdaptiveFanPolicyInput input,
        out string failure)
    {
        if (!double.IsFinite(input.CpuEffectiveTemperatureC) ||
            input.CpuEffectiveTemperatureC is < 0 or > 125 ||
            !double.IsFinite(input.CpuPackagePowerW) ||
            input.CpuPackagePowerW is < 0 or > 500 ||
            !double.IsFinite(input.CpuLoadPercent) ||
            input.CpuLoadPercent is < 0 or > 100 ||
            !double.IsFinite(input.GpuTemperatureC) ||
            input.GpuTemperatureC is < 0 or > 110 ||
            !double.IsFinite(input.GpuPowerW) ||
            input.GpuPowerW is < 0 or > 300 ||
            !double.IsFinite(input.GpuLoadPercent) ||
            input.GpuLoadPercent is < 0 or > 100)
        {
            failure =
                "Adaptive policy refused invalid or implausible telemetry.";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}
