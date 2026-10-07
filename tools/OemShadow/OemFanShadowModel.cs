namespace VictusFanControl.OemShadow;

public enum OemState { Unknown, A, B, C, D, Transition }
public enum Domain { Unknown, CPU, GPU, Shared }
public sealed record Source(double? Value = null, DateTimeOffset? SampledAtUtc = null)
{
    public double? AgeMs(DateTimeOffset now) => SampledAtUtc is { } at ? (now - at).TotalMilliseconds : null;
    public bool Fresh(DateTimeOffset now, double maxAge) => Value is { } v && double.IsFinite(v) &&
        v is >= 0 and <= 120 && AgeMs(now) is { } age && age >= 0 && age < maxAge;
}
public sealed record Frame(DateTimeOffset TimestampUtc, Source CpuPackage, Source CpuCoreMax,
    Source Gpu, Source Tz01, Source Dtt3, Source? Dtt1 = null, Source? Dtt2 = null,
    int? ActualCpuLevel = null, int? ActualGpuLevel = null, DateTimeOffset? FanSampledAtUtc = null,
    double? CpuPowerW = null, double? GpuPowerW = null, double? CpuLoadPercent = null, double? GpuLoadPercent = null);
public sealed record LevelRange(int Min, int Max);
public sealed record Prediction(OemState State, LevelRange? CpuRange, LevelRange? GpuRange,
    Domain DominantDomain, double DwellMs, string Hysteresis, string ReasonCode, string Confidence);

// Hypotheses for observation only. These parameters are not an OEM specification.
public sealed record Parameters
{
    public double MaxSourceAgeMs { get; init; } = 3000;
    public double MaxGapMs { get; init; } = 5000;
    public double MaxHistoryGapMs { get; init; } = 30000;
    public double BootstrapSeconds { get; init; } = 10;
    public double CpuUpTemp { get; init; } = 95;
    public double CpuUpTz01 { get; init; } = 85;
    public double CpuUpSeconds { get; init; } = 45;
    public double GpuUpSeconds { get; init; } = 60;
    public double MinStateSeconds { get; init; } = 60;
    public double DownSeconds { get; init; } = 30;
    public double RampSeconds { get; init; } = 10;
    public double ActualDebounceSeconds { get; init; } = 3;
    public double TransitionMatchWindowSeconds { get; init; } = 120;
    public double[] GpuUpTemp { get; init; } = [45, 55, 75];
    public double[] GpuUpDtt3 { get; init; } = [50, 55, 67];
    public double[] BootstrapMax { get; init; } = [75, 60, 45, 49]; // CPU, TZ01, GPU, DTT3
    public double[][] DownMax { get; init; } = [[55, 45, 40, 42], [65, 55, 48, 48], [70, 65, 55, 52]];

    public void Validate()
    {
        var durations = new[] { MaxSourceAgeMs, MaxGapMs, MaxHistoryGapMs, BootstrapSeconds, CpuUpSeconds,
            GpuUpSeconds, MinStateSeconds, DownSeconds, RampSeconds, ActualDebounceSeconds, TransitionMatchWindowSeconds };
        if (durations.Any(v => !double.IsFinite(v) || v < 0) || MaxSourceAgeMs <= 0 || MaxGapMs <= 0 ||
            MaxHistoryGapMs < MaxGapMs) throw new ArgumentException("Invalid shadow timing parameters.");
        var arrays = new[] { GpuUpTemp, GpuUpDtt3, BootstrapMax };
        if (arrays.Any(a => a is null) || DownMax is null || GpuUpTemp.Length != 3 || GpuUpDtt3.Length != 3 ||
            BootstrapMax.Length != 4 || DownMax.Length != 3 || DownMax.Any(a => a is null || a.Length != 4))
            throw new ArgumentException("Invalid threshold dimensions.");
        var temps = arrays.Concat(DownMax).SelectMany(a => a).Concat([CpuUpTemp, CpuUpTz01]);
        if (temps.Any(v => !double.IsFinite(v) || v < 0 || v > 120) ||
            !GpuUpTemp.SequenceEqual(GpuUpTemp.Order()) || !GpuUpDtt3.SequenceEqual(GpuUpDtt3.Order()))
            throw new ArgumentException("Invalid or unordered temperature thresholds.");
        for (int i = 0; i < 3; i++)
            if (DownMax[i][2] >= GpuUpTemp[i] || DownMax[i][3] >= GpuUpDtt3[i])
                throw new ArgumentException("Down thresholds must be lower than corresponding up thresholds.");
        if (DownMax[0][0] >= CpuUpTemp || DownMax[0][1] >= CpuUpTz01)
            throw new ArgumentException("CPU hysteresis must have distinct up/down thresholds.");
    }
}

public sealed class OemFanShadowModel
{
    public Parameters Settings { get; }
    private OemState _stable, _gpuCandidate, _rampFrom;
    private DateTimeOffset? _previous, _lastValid, _stateSince, _cpuHot, _gpuHot, _cold, _mild, _rampUntil;
    private bool _wasValid;
    private Domain _domain;
    public OemFanShadowModel(Parameters? parameters = null)
    {
        Settings = parameters ?? new(); Settings.Validate();
    }
    public static bool Plateau(OemState s) => s is >= OemState.A and <= OemState.D;
    public static (LevelRange Cpu, LevelRange Gpu) Ranges(OemState s) => s switch
    {
        OemState.A => (new(21,22), new(19,20)), OemState.B => (new(25,26), new(23,24)),
        OemState.C => (new(33,34), new(28,29)), OemState.D => (new(39,40), new(35,36)),
        _ => throw new ArgumentException("State has no plateau range.")
    };
    public static OemState Classify(int? cpu, int? gpu)
    {
        if (cpu is null or < 0 or > 100 || gpu is null or < 0 or > 100) return OemState.Unknown;
        foreach (var s in new[] { OemState.A, OemState.B, OemState.C, OemState.D })
        {
            var r = Ranges(s);
            if (cpu >= r.Cpu.Min - 1 && cpu <= r.Cpu.Max + 1 && gpu >= r.Gpu.Min - 1 && gpu <= r.Gpu.Max + 1) return s;
        }
        return OemState.Transition;
    }
    public void Reset()
    {
        _stable = OemState.Unknown; _previous = _lastValid = _stateSince = null; _wasValid = false;
        ClearTimers(); _domain = Domain.Unknown;
    }
    private void ClearTimers()
    {
        _cpuHot = _gpuHot = _cold = _mild = _rampUntil = null; _gpuCandidate = OemState.Unknown;
    }
    private static bool Held(bool condition, ref DateTimeOffset? since, DateTimeOffset now, double seconds)
    {
        if (!condition) { since = null; return false; }
        since ??= now; return (now - since.Value).TotalSeconds >= seconds;
    }
    private void Commit(OemState state, Domain domain, DateTimeOffset now)
    {
        _rampFrom = _stable; _stable = state; _stateSince = now; _domain = domain; _cold = _mild = null;
        _rampUntil = now.AddSeconds(Settings.RampSeconds);
    }
    public Prediction Evaluate(Frame f)
    {
        var now = f.TimestampUtc;
        if (_previous is { } prev && (now <= prev || (now - prev).TotalMilliseconds > Settings.MaxGapMs)) Reset();
        _previous = now;
        if (!new[] { f.CpuPackage, f.CpuCoreMax, f.Gpu, f.Tz01, f.Dtt3 }.All(s => s.Fresh(now, Settings.MaxSourceAgeMs)))
        {
            ClearTimers(); _wasValid = false;
            if (_lastValid is null || (now - _lastValid.Value).TotalMilliseconds > Settings.MaxHistoryGapMs)
                _stable = OemState.Unknown;
            return new(OemState.Unknown, null, null, Domain.Unknown, 0, "SUSPENDED", "SOURCE_MISSING_STALE_OR_FUTURE", "ReducedConfidence");
        }
        if (!_wasValid) _stateSince = now; // Missing time never earns persistence or descent dwell.
        _wasValid = true; _lastValid = now;
        double cpu = Math.Max(f.CpuPackage.Value!.Value, f.CpuCoreMax.Value!.Value), tz = f.Tz01.Value!.Value,
            gpu = f.Gpu.Value!.Value, dtt = f.Dtt3.Value!.Value;
        bool cpuReady = Held(cpu >= Settings.CpuUpTemp && tz >= Settings.CpuUpTz01, ref _cpuHot, now, Settings.CpuUpSeconds);
        OemState gpuDemand = OemState.Unknown;
        for (int i = 0; i < 3; i++) if (gpu >= Settings.GpuUpTemp[i] && dtt >= Settings.GpuUpDtt3[i]) gpuDemand = (OemState)(i + 2);
        if (_gpuCandidate != gpuDemand) { _gpuHot = null; _gpuCandidate = gpuDemand; }
        bool gpuReady = Held(Plateau(gpuDemand), ref _gpuHot, now, Settings.GpuUpSeconds);
        OemState demand = cpuReady ? OemState.B : OemState.Unknown;
        if (gpuReady && gpuDemand > demand) demand = gpuDemand;
        Domain dominant = cpuReady && gpuReady && gpuDemand == OemState.B ? Domain.Shared :
            gpuReady && demand == gpuDemand ? Domain.GPU : cpuReady ? Domain.CPU : Domain.Unknown;
        string reason = "HYSTERESIS_HOLD";
        if (demand > _stable && Plateau(demand)) { Commit(demand, dominant, now); reason = "SUSTAINED_UP"; }
        else if (_stable == OemState.Unknown)
        {
            var b = Settings.BootstrapMax;
            if (Held(cpu <= b[0] && tz <= b[1] && gpu <= b[2] && dtt <= b[3], ref _mild, now, Settings.BootstrapSeconds))
            { Commit(OemState.A, Domain.Shared, now); reason = "MILD_BOOTSTRAP"; }
            else reason = "WARM_START_UNRESOLVED";
        }
        else if (_stable > OemState.A)
        {
            var b = Settings.DownMax[(int)_stable - 2];
            bool cold = Held(cpu <= b[0] && tz <= b[1] && gpu <= b[2] && dtt <= b[3], ref _cold, now, Settings.DownSeconds);
            if (cold && (now - _stateSince!.Value).TotalSeconds >= Settings.MinStateSeconds)
            { Commit(_stable - 1, Domain.Shared, now); reason = "SUSTAINED_DOWN"; }
        }
        if (!Plateau(_stable)) return new(OemState.Unknown, null, null, Domain.Unknown, 0, "BOOTSTRAP", reason, "Unavailable");
        var range = Ranges(_stable); bool ramp = _rampUntil is { } end && now < end;
        if (ramp && Plateau(_rampFrom))
        {
            var from = Ranges(_rampFrom);
            range = (new(Math.Min(from.Cpu.Min,range.Cpu.Min),Math.Max(from.Cpu.Max,range.Cpu.Max)),
                new(Math.Min(from.Gpu.Min,range.Gpu.Min),Math.Max(from.Gpu.Max,range.Gpu.Max)));
        }
        return new(ramp ? OemState.Transition : _stable, range.Cpu, range.Gpu, _domain,
            (now - _stateSince!.Value).TotalMilliseconds, ramp ? "RAMP" : _cold is not null ? "COOLING" : "HOLD",
            reason, "UnvalidatedSeed");
    }
}

public sealed record Actual(OemState State, OemState RawState, DateTimeOffset? AcceptedSinceUtc);
public sealed class ActualDebouncer(Parameters settings)
{
    private DateTimeOffset? _lastAcquisition, _candidateSince, _acceptedSince;
    private OemState _candidate, _stable;
    public void Reset() { _lastAcquisition = _candidateSince = _acceptedSince = null; _candidate = _stable = OemState.Unknown; }
    public Actual Evaluate(Frame f)
    {
        var raw = OemFanShadowModel.Classify(f.ActualCpuLevel, f.ActualGpuLevel);
        if (f.FanSampledAtUtc is not { } at || (f.TimestampUtc-at).TotalMilliseconds is var age &&
            (age < 0 || age >= settings.MaxSourceAgeMs) || raw == OemState.Unknown ||
            (_lastAcquisition is { } last && at < last))
        { Reset(); return new(OemState.Unknown, raw, null); }
        if (at == _lastAcquisition) return new(_candidate == _stable ? _stable : OemState.Transition, raw, _acceptedSince);
        _lastAcquisition = at;
        if (raw == OemState.Transition) { _candidate = raw; _candidateSince = null; return new(raw, raw, _acceptedSince); }
        if (raw != _candidate) { _candidate = raw; _candidateSince = at; }
        if (at - _candidateSince >= TimeSpan.FromSeconds(settings.ActualDebounceSeconds))
        { if (_stable != _candidate) _acceptedSince = _candidateSince; _stable = _candidate; }
        return new(_candidate == _stable ? _stable : OemState.Transition, raw, _acceptedSince);
    }
}
