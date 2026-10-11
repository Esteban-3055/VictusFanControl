namespace VictusFanControl.Control.Adaptive;

public sealed record CpuThermalDemandObservation(DateTimeOffset Timestamp, double RawDemandC, double RawControlC,
    double DemandC, double ControlC, bool SpikeSuppressed, bool ExtremeBypass)
{
    public long FreshAcquisitions { get; init; }
    public long SuppressedFreshAcquisitions { get; init; }
    public long ExtremeBypassAcquisitions { get; init; }
}

/// <summary>Demand only. Raw telemetry, thermal admission and firmware remain independent.</summary>
internal sealed class CpuThermalDemandFilter
{
    internal const double ConfirmationSeconds = 2;
    internal const double ImmediateTemperatureC = 99;
    private readonly Queue<AdaptiveFanPolicyInput> _history = new();
    private DateTimeOffset? _last;
    private AdaptiveFanPolicyInput? _lastFiltered;
    private bool _restartConfirmation;
    private long _freshAcquisitions, _suppressedAcquisitions, _extremeAcquisitions;
    internal CpuThermalDemandObservation? Observation { get; private set; }

    internal bool TryApply(AdaptiveFanPolicyInput input, TimeSpan maximumGap,
        out AdaptiveFanPolicyInput filtered, out string failure)
    {
        filtered = input;
        if (!AdaptiveFanPolicyEngine.ValidateInput(input, out failure)) { Reset(); return false; }
        if (_last.HasValue && (input.Timestamp <= _last || input.Timestamp - _last > maximumGap))
        {
            failure = "CPU demand filter refused duplicate, out-of-order or discontinuous raw telemetry.";
            Reset(); return false;
        }
        _last = input.Timestamp;
        var rawControl = input.CpuRawControlTemperatureC ?? input.CpuEffectiveTemperatureC;
        var extreme = rawControl >= ImmediateTemperatureC;
        if (input.CpuThermalRetained)
        {
            // A retained value is not another hot acquisition. Break continuity,
            // keep the accepted demand and restart confirmation on fresh recovery.
            _history.Clear();
            _restartConfirmation = true;
        }
        else
        {
            if (_restartConfirmation && _lastFiltered is not null)
                _history.Enqueue(_lastFiltered with { Timestamp = input.Timestamp });
            _restartConfirmation = false;
            _history.Enqueue(input);
        }
        var cutoff = input.Timestamp.AddSeconds(-ConfirmationSeconds);
        // Keep the acquisition preceding the window. Otherwise an irregular 2.5s
        // poll could discard the cold baseline and qualify a 0.5s hot interval.
        while (_history.Count > 1 && _history.ElementAt(1).Timestamp <= cutoff) _history.Dequeue();
        var demand = extreme ? input.CpuEffectiveTemperatureC : input.CpuThermalRetained
            ? _lastFiltered?.CpuEffectiveTemperatureC ?? input.CpuEffectiveTemperatureC
            : _history.Min(s => s.CpuEffectiveTemperatureC);
        var control = extreme ? rawControl : input.CpuThermalRetained
            ? _lastFiltered?.CpuRawControlTemperatureC ?? rawControl
            : _history.Min(s => s.CpuRawControlTemperatureC ?? s.CpuEffectiveTemperatureC);
        // A first hot acquisition has no cold evidence: never invent a cold start.
        filtered = input with { CpuEffectiveTemperatureC = demand, CpuRawControlTemperatureC = control };
        _lastFiltered = filtered;
        var suppressed = !extreme && (demand < input.CpuEffectiveTemperatureC || control < rawControl);
        if (!input.CpuThermalRetained)
        {
            _freshAcquisitions++;
            if (suppressed) _suppressedAcquisitions++;
            if (extreme) _extremeAcquisitions++;
        }
        Observation = new(input.Timestamp, input.CpuEffectiveTemperatureC, rawControl, demand, control, suppressed, extreme)
        { FreshAcquisitions = _freshAcquisitions, SuppressedFreshAcquisitions = _suppressedAcquisitions, ExtremeBypassAcquisitions = _extremeAcquisitions };
        failure = string.Empty;
        return true;
    }
    internal void Reset() { _history.Clear(); _last = null; _lastFiltered = null; _restartConfirmation = false; Observation = null;
        _freshAcquisitions = _suppressedAcquisitions = _extremeAcquisitions = 0; }
}
