using VictusFanControl.OemShadow;

namespace VictusFanControl.PlatformThermalReplay;

public sealed record ThermalPoint(double TemperatureC, double Level);
public sealed record PlatformSettings
{
    public double MaximumSourceAgeSeconds { get; init; } = 3;
    public double MaximumFrameGapSeconds { get; init; } = 3;
    public double QualificationSeconds { get; init; } = 1;
    public int QualificationAcquisitions { get; init; } = 2;
    // Product hypotheses, NOT firmware thresholds or physical safety limits.
    public ThermalPoint[] Tz01Curve { get; init; } =
        [new(40,12),new(50,16),new(60,22),new(70,28),new(80,34),new(90,40),new(100,44)];
    public ThermalPoint[] Dtt3Curve { get; init; } =
        [new(40,12),new(45,16),new(50,22),new(55,28),new(60,34),new(67,40),new(75,44)];
    public void Validate()
    {
        if (!double.IsFinite(MaximumSourceAgeSeconds) || MaximumSourceAgeSeconds is <= 0 or > 3 ||
            !double.IsFinite(MaximumFrameGapSeconds) || MaximumFrameGapSeconds is <= 0 or > 3 ||
            !double.IsFinite(QualificationSeconds) || QualificationSeconds is < 1 or > 30 ||
            QualificationAcquisitions is < 2 or > 30)
            throw new InvalidDataException("Invalid platform source admission.");
        foreach (var curve in new[] { Tz01Curve, Dtt3Curve })
        {
            if (curve is null || curve.Length is < 2 or > 64) throw new InvalidDataException("Invalid platform curve.");
            double t = -1, level = 0;
            foreach (var p in curve)
            {
                if (p is null || !double.IsFinite(p.TemperatureC) || !double.IsFinite(p.Level) ||
                    p.TemperatureC <= t || p.TemperatureC > 120 || p.Level < level || p.Level is < 0 or > 44)
                    throw new InvalidDataException("Platform curves must rise and remain capped at level 44.");
                t = p.TemperatureC; level = p.Level;
            }
        }
    }
}

public sealed record PlatformObservation(bool Available, double? DemandLevel,
    double? Tz01Demand, double? Dtt3Demand, string Reason);

/// <summary>Pure research observer. Does not read hardware or own fan authority.</summary>
public sealed class PlatformThermalDemand
{
    private readonly PlatformSettings _settings;
    private readonly bool _useTz, _useDtt;
    private readonly Channel _tz = new(), _dtt = new();
    private DateTimeOffset? _lastFrame;
    public PlatformThermalDemand(PlatformSettings settings, bool useTz, bool useDtt)
    {
        settings.Validate();
        _settings = settings with { Tz01Curve = settings.Tz01Curve.ToArray(), Dtt3Curve = settings.Dtt3Curve.ToArray() };
        _useTz = useTz; _useDtt = useDtt;
    }
    public PlatformObservation Evaluate(Frame f, Source[]? tzHistory = null, Source[]? dttHistory = null)
    {
        if (!_useTz && !_useDtt) return new(true, null, null, null, "Disabled");
        if (_lastFrame is { } last && (f.TimestampUtc <= last ||
            (f.TimestampUtc-last).TotalSeconds > _settings.MaximumFrameGapSeconds))
        {
            Reset(); _lastFrame = f.TimestampUtc;
            return new(false, null, null, null, "FrameContinuityLost");
        }
        _lastFrame = f.TimestampUtc;
        var tzOk = !_useTz || _tz.AdmitHistory(f.Tz01, f.TimestampUtc, _settings, tzHistory);
        var dttOk = !_useDtt || _dtt.AdmitHistory(f.Dtt3, f.TimestampUtc, _settings, dttHistory);
        if (!tzOk || !dttOk)
            return new(false, null, null, null, "SourceMissingStaleInvalidOrRequalifying");
        double? tz = _useTz ? Interpolate(_settings.Tz01Curve, f.Tz01.Value!.Value) : null;
        double? dtt = _useDtt ? Interpolate(_settings.Dtt3Curve, f.Dtt3.Value!.Value) : null;
        return new(true, Math.Max(tz ?? 0, dtt ?? 0), tz, dtt, "Qualified");
    }
    public void Reset() { _lastFrame = null; _tz.Reset(); _dtt.Reset(); }
    public static double Interpolate(IReadOnlyList<ThermalPoint> curve, double value)
    {
        if (value <= curve[0].TemperatureC) return curve[0].Level;
        for (int i=1;i<curve.Count;i++)
            if (value <= curve[i].TemperatureC)
                return curve[i-1].Level + (curve[i].Level-curve[i-1].Level) *
                    (value-curve[i-1].TemperatureC)/(curve[i].TemperatureC-curve[i-1].TemperatureC);
        return curve[^1].Level;
    }
    private sealed class Channel
    {
        private DateTimeOffset? _epoch, _first;
        private double? _value;
        private int _count;
        public bool AdmitHistory(Source source, DateTimeOffset now, PlatformSettings settings, Source[]? history)
        {
            // Archived replay has no acquisition history and keeps its exact admission.
            if (history is null) return Admit(source, now, settings);
            if (history.Length is < 1 or > 8 || history[^1] != source ||
                !source.Fresh(now, settings.MaximumSourceAgeSeconds*1000))
            { Reset(); return false; }
            DateTimeOffset? previous = null;
            foreach (var sample in history)
            {
                // Reject malformed, regressed, future or mutated epochs; never sort,
                // fabricate a bridge or conceal an invalid intervening acquisition.
                if (sample is null || sample.SampledAtUtc is not {} at || at > now ||
                    !sample.Fresh(at, settings.MaximumSourceAgeSeconds*1000) ||
                    (previous is {} prior && at <= prior))
                { Reset(); return false; }
                previous = at;
            }
            if (_epoch is {} latest && source.SampledAtUtc < latest)
            { Reset(); return false; }
            var observed = _epoch;
            bool qualified = false;
            foreach (var sample in history)
            {
                var at = sample.SampledAtUtc!.Value;
                if (observed is {} last && at < last) continue;
                // With no prior admission, only presently fresh acquisitions can
                // establish qualification. Cached prefixes never renew their age.
                if (observed is null && !sample.Fresh(now, settings.MaximumSourceAgeSeconds*1000)) continue;
                if (_epoch is {} epoch && (at < epoch || (at == epoch && sample.Value != _value) ||
                    (at-epoch).TotalSeconds > settings.MaximumSourceAgeSeconds))
                { Reset(); return false; }
                qualified = Admit(sample, at, settings);
            }
            return qualified && _epoch == source.SampledAtUtc;
        }
        public bool Admit(Source? source, DateTimeOffset now, PlatformSettings settings)
        {
            if (source is null || !source.Fresh(now, settings.MaximumSourceAgeSeconds*1000))
            { Reset(); return false; }
            var at = source.SampledAtUtc!.Value;
            if (_epoch is { } last && (at < last || (at == last && source.Value != _value) ||
                (at-last).TotalSeconds > settings.MaximumSourceAgeSeconds))
            { Reset(); return false; }
            if (_epoch != at)
            {
                _first ??= at; _count++; _epoch = at; _value = source.Value;
            }
            // Cached rows never advance qualification or refresh source age.
            return _count >= settings.QualificationAcquisitions &&
                (at-_first!.Value).TotalSeconds >= settings.QualificationSeconds;
        }
        public void Reset() { _epoch=null; _first=null; _value=null; _count=0; }
    }
}
