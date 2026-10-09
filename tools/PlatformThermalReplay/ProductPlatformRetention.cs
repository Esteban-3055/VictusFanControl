using VictusFanControl.Control.Adaptive;
using VictusFanControl.OemShadow;
using VictusFanControl.Telemetry;

namespace VictusFanControl.PlatformThermalReplay;

public sealed record ProductRetentionState(bool Enabled, bool Ready, string Status, Source Tz01, Source Dtt3,
    double? SourceDemand, double? BaselineDemand, double? SupplementalDemand, double? RemainingSeconds);

/// <summary>Power-source-independent experimental retention. Fresh auxiliary sources cannot request a new higher fan level.</summary>
public sealed class ProductPlatformRetention : IExperimentalFanSupplement
{
    public const int MaximumExtraLevels = 2;
    public const int MaximumRetentionSeconds = 60;
    private readonly object _sync = new();
    private readonly PlatformThermalDemand _admission = new(new(), true, true);
    private readonly List<Source> _tz = new(), _dtt = new();
    private Source[] _joinedTz=[], _joinedDtt=[];
    private Frame? _frame;
    private PlatformObservation? _qualified;
    private DateTimeOffset? _evaluated, _episodeStart;
    private double? _episodeCeiling;
    private double? _baseline, _supplement;
    private bool _enabled;
    private string? _failure;
    public bool Enabled { get { lock(_sync)return _enabled; } }
    public object CaptureEvidence()
    {
        lock(_sync)return new {state=State,frame=_frame,sourceHistory=new {tz01=_joinedTz.ToArray(),dtt3=_joinedDtt.ToArray()}};
    }
    public ProductRetentionState State
    {
        get { lock(_sync) return new(_enabled, _qualified?.Available == true && _failure is null,
            !_enabled ? "Desactivada" : _failure ?? (_qualified?.Available == true ? "Cualificada · experimental" : "Cualificando TZ01/DTT3"),
            _frame?.Tz01 ?? new(), _frame?.Dtt3 ?? new(), _qualified?.DemandLevel, _baseline, _supplement,
            _episodeStart is {} start && _frame is {} frame ? Math.Max(0, MaximumRetentionSeconds-(frame.TimestampUtc-start).TotalSeconds) : null); }
    }
    // Host calls only in Firmware, before preparing performance or admitting Custom.
    public void Configure(bool enabled)
    {
        lock(_sync)
        {
            if (_enabled == enabled && _failure is null) { Reset(); return; }
            _enabled=enabled; _failure=null; _admission.Reset(); _qualified=null; _frame=null;
            _tz.Clear(); _dtt.Clear(); _joinedTz=[]; _joinedDtt=[]; Reset();
        }
    }
    public void SetSources(Source tz, Source dtt)
    {
        lock(_sync)
        {
            if(!_enabled)return;
            static void Add(List<Source> history, Source source)
            {
                // Before the first native acquisition, a missing value is an
                // unobserved slot, not an epoch. Later missing values are faults
                // and must remain in the bounded evidence history.
                if(history.Count==0 && source.SampledAtUtc is null)return;
                if(history.Count==0 || history[^1]!=source)history.Add(source);
                if(history.Count>8)history.RemoveAt(0);
            }
            Add(_tz,tz); Add(_dtt,dtt);
        }
    }
    public void ObserveTelemetry(TelemetrySnapshot snapshot, bool custom)
    {
        lock(_sync)
        {
            if(!_enabled)return;
            if(_frame is {} last && (snapshot.Timestamp<=last.TimestampUtc || snapshot.Timestamp-last.TimestampUtc>TimeSpan.FromSeconds(3)))
            {
                _admission.Reset(); _qualified=null;
                if(custom)_failure="Continuidad de telemetría perdida; reinicia en Firmware.";
            }
            Source[] Join(List<Source> history)=>history.Where(s=>s.SampledAtUtc is null || s.SampledAtUtc<=snapshot.Timestamp).ToArray();
            var tz=Join(_tz); var dtt=Join(_dtt);
            _joinedTz=tz; _joinedDtt=dtt;
            _frame=new(snapshot.Timestamp,new(snapshot.CpuTemperatureC,snapshot.Timestamp),new(snapshot.CpuCoreMaxTemperatureC,snapshot.Timestamp),
                new(snapshot.GpuTemperatureC,snapshot.Timestamp),tz.LastOrDefault()??new(),dtt.LastOrDefault()??new());
            _qualified=_admission.Evaluate(_frame,tz,dtt);
            if(custom && !_qualified.Available)
                _failure="TZ01/DTT3 no vigentes; reinicia en Firmware.";
        }
    }
    public void RequireReady(DateTimeOffset now)
    {
        lock(_sync)
        {
            if(!_enabled)return;
            if(_failure is not null || _qualified is not {Available:true} || _frame is null ||
                now<_frame.TimestampUtc || now-_frame.TimestampUtc>=TimeSpan.FromSeconds(3) ||
                !_frame.Tz01.Fresh(now,3000) || !_frame.Dtt3.Fresh(now,3000))
                throw new InvalidOperationException(_failure ?? "Retención experimental: requiere TZ01/DTT3 vigentes y cualificados; espera en Firmware.");
        }
    }
    public double? GetSupplement(AdaptiveFanPolicyInput input, double baselineRawDemand, int? lastAcknowledgedLevel)
    {
        lock(_sync)
        {
            _baseline=baselineRawDemand; _supplement=null;
            if(!_enabled)return null;
            RequireReady(input.Timestamp);
            if(_frame?.TimestampUtc!=input.Timestamp)throw new InvalidOperationException("Auxiliares no asociados a esta adquisición.");
            _evaluated=input.Timestamp;
            if(_episodeCeiling is {} ceiling && baselineRawDemand>=ceiling) { _episodeStart=null; _episodeCeiling=null; }
            if(lastAcknowledgedLevel is not {} level)return null;
            var floor=Math.Min(_qualified!.DemandLevel!.Value,Math.Min(44,level));
            if(floor<=baselineRawDemand)return null;
            _episodeStart??=input.Timestamp; _episodeCeiling??=floor;
            // Expired warm sources cannot continually renew the same retention episode.
            if(input.Timestamp-_episodeStart.Value>=TimeSpan.FromSeconds(MaximumRetentionSeconds))return null;
            _supplement=Math.Min(floor,baselineRawDemand+MaximumExtraLevels);
            return _supplement;
        }
    }
    public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input, AdaptiveFanInertiaDecision baseline)=>baseline;
    public void EnsureDispatchAllowed(DateTimeOffset snapshotTimestamp, DateTimeOffset now)
    {
        lock(_sync)
        {
            if(!_enabled)return;
            RequireReady(now);
            if(_evaluated!=snapshotTimestamp || _frame?.TimestampUtc!=snapshotTimestamp)
                throw new InvalidOperationException("Retención: adquisición de despacho no vigente.");
        }
    }
    public void ObserveDuringActuation(AdaptiveFanPolicyInput input) { }
    public void Reset()
    {
        lock(_sync) { _evaluated=null; _episodeStart=null; _episodeCeiling=null; _baseline=null; _supplement=null; }
    }
}
