using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.OemShadow;
using VictusFanControl.Telemetry;

namespace VictusFanControl.PlatformThermalReplay;

public sealed record ExperimentStage(int Index, string Controller, string Activity, int RemainingSeconds)
{
    public bool Custom => Controller is "baseline" or "both-retention";
}

public static class ExperimentProtocol
{
    public const int PreflightSeconds=120, BlockSeconds=540, FinalSeconds=300;
    public const int TotalSeconds=PreflightSeconds+4*BlockSeconds+FinalSeconds;
    public static ExperimentStage At(int seconds)
    {
        if(seconds<0)throw new ArgumentOutOfRangeException(nameof(seconds));
        if(seconds<PreflightSeconds)return new(-1,"firmware","Reposo y validación inicial",PreflightSeconds-seconds);
        int elapsed=seconds-PreflightSeconds;
        if(elapsed>=4*BlockSeconds)return new(4,"firmware",seconds<TotalSeconds?"Enfriamiento final":"Finalizado",Math.Max(0,TotalSeconds-seconds));
        int block=elapsed/BlockSeconds, within=elapsed%BlockSeconds;
        string activity=within<120?"Reposo":within<360?"Carga repetible (misma escena/benchmark)":"Enfriamiento: cerrar la carga";
        return new(block,block is 0 or 3?"baseline":"both-retention",activity,BlockSeconds-within);
    }
}

/// <summary>Explicit physical experiment; contains no hardware backend or write API.</summary>
public sealed class PhysicalPlatformExperiment : IExperimentalFanPolicy, IDisposable
{
    private readonly object _sync=new();
    private readonly FanConfiguration _fan;
    private readonly PlatformSettings _settings=new();
    private readonly PlatformThermalDemand _admission=new(new(),true,true);
    private readonly ResearchFanInertiaPolicy _active;
    private readonly (string Name,PlatformThermalDemand Sources,ResearchFanInertiaPolicy Policy)[] _variants;
    private readonly StreamWriter _trace;
    private readonly string _directory;
    private readonly List<Source> _tz=[],_dtt1=[],_dtt2=[],_dtt3=[];
    private Frame? _frame;
    private PlatformObservation? _qualified;
    private ExperimentStage _stage=ExperimentProtocol.At(0);
    private int _evaluatedStage=int.MinValue;
    private DateTimeOffset? _evaluatedTimestamp,_lastTelemetry;
    private int? _lastApplied;
    private bool _closed,_disposed;
    private string? _reason;
    private int _telemetryRows,_decisions,_appliedChanges,_qualifiedRows;
    private double _cpuMaximum,_gpuMaximum;
    private DateTimeOffset? _first,_last;
    private readonly Dictionary<string,int> _counts=new();
    public static JsonSerializerOptions Json { get; }=new(ShadowSession.Json){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public string DirectoryPath=>_directory;
    public bool Ready { get {lock(_sync)return !_closed&&_qualified is {Available:true};} }
    public string? Failure {get {lock(_sync)return _reason;} }
    public PhysicalPlatformExperiment(string directory,FanConfiguration fan,bool physicalExecution=false)
    {
        _fan=FanConfigurationStore.Copy(fan);_ = _fan.BuildPolicy();
        if(System.IO.Directory.Exists(directory)||File.Exists(directory))throw new IOException("Use a new experimental output directory.");
        _directory=Path.GetFullPath(directory);System.IO.Directory.CreateDirectory(_directory);
        _trace=new(new FileStream(Path.Combine(_directory,"experiment.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
        var config=Hp8C40AutomaticPolicy.Create(_fan.BuildPolicy(),10);
        _active=new(config,_fan.Tuning);
        _variants=new[]{"tz01","dtt3","both","both-retention","both-warmer-thresholds","both-colder-thresholds"}.Select(name=>
        {
            int tz=name=="both-warmer-thresholds"?5:name=="both-colder-thresholds"?-5:0;
            int dtt=name=="both-warmer-thresholds"?3:name=="both-colder-thresholds"?-3:0;
            var settings=_settings with{Tz01Curve=_settings.Tz01Curve.Select(p=>p with{TemperatureC=p.TemperatureC+tz}).ToArray(),
                Dtt3Curve=_settings.Dtt3Curve.Select(p=>p with{TemperatureC=p.TemperatureC+dtt}).ToArray()};
            return(name,new PlatformThermalDemand(settings,name!="dtt3",name!="tz01"),new ResearchFanInertiaPolicy(config,_fan.Tuning));
        }).ToArray();
        Write("session",new{target="HP-8C40-9D0R1LA-F18",protocol="A-B-B-A;120s firmware + 4x540s + 300s firmware",
            fan=_fan,settings=_settings,physicalExecution,normalAutomaticPromoted=false,performancePresets="Frozen by host in metadata.json; existing Guardian applies/releases them",
            disclosure="Shadow variants share the observed temperatures, not counterfactual thermal outcomes. HP-WMI levels*100 are nominal RPM; workload labels are scheduled instructions, not proof of load. Query epochs do not prove silicon sensor update times."});
    }
    private void Write(string kind,object data)=>_trace.WriteLine(JsonSerializer.Serialize(new{kind,data},Json));
    public void SetSources(Source tz,Source dtt1,Source dtt2,Source dtt3)
    {
        lock(_sync)
        {
            void Add(List<Source> history,Source source)
            {
                if(history.Count==0||history[^1]!=source)history.Add(source);
                if(history.Count>8)history.RemoveAt(0);
            }
            Add(_tz,tz);Add(_dtt1,dtt1);Add(_dtt2,dtt2);Add(_dtt3,dtt3);
        }
    }
    public void SetStage(ExperimentStage stage)
    {
        lock(_sync)
        {
            if(_closed)return;
            if(stage.Index!=_stage.Index)
            {
                if(_lastApplied is {} level)_active.AlignExperimentalTarget(level);
                _evaluatedTimestamp=null;
                Write("stage",new{atUtc=DateTimeOffset.UtcNow,stage,transitionTarget=_lastApplied,confirmationsReset=true,filterHistoryPreserved=true});
            }
            _stage=stage;
        }
    }
    public void ObserveTelemetry(TelemetrySnapshot snapshot,string source)
    {
        lock(_sync)
        {
            if(_disposed)return;
            if(_lastTelemetry is {} last&&(snapshot.Timestamp<=last||snapshot.Timestamp-last>TimeSpan.FromSeconds(3)))
            {
                _admission.Reset();_qualified=null;
                if(_stage.Custom)Close("Telemetry epoch gap or regression");
            }
            _lastTelemetry=snapshot.Timestamp;
            Source Pick(List<Source> history)=>history.LastOrDefault(s=>s.SampledAtUtc<=snapshot.Timestamp)??new();
            _frame=new(snapshot.Timestamp,new(snapshot.CpuTemperatureC,snapshot.Timestamp),new(snapshot.CpuCoreMaxTemperatureC,snapshot.Timestamp),
                new(snapshot.GpuTemperatureC,snapshot.Timestamp),Pick(_tz),Pick(_dtt3),Pick(_dtt1),Pick(_dtt2),
                snapshot.CpuFanSpeedLevel,snapshot.GpuFanSpeedLevel,snapshot.FanSampledAtUtc,
                snapshot.CpuPackagePowerW,snapshot.GpuPowerW,snapshot.CpuLoadPercent,snapshot.GpuLoadPercent);
            _qualified=_admission.Evaluate(_frame);
            _telemetryRows++;if(_qualified.Available)_qualifiedRows++;
            _first??=snapshot.Timestamp;_last=snapshot.Timestamp;
            _cpuMaximum=Math.Max(_cpuMaximum,snapshot.CpuControlTemperatureC??0);_gpuMaximum=Math.Max(_gpuMaximum,snapshot.GpuTemperatureC??0);
            Write("telemetry",new{stage=_stage,source,snapshot,frame=_frame,admission=_qualified});
            if(_stage.Custom&&(source!="Ac"||!_qualified.Available))Close(source!="Ac"?"Power source changed; experiment is AC-only":"Required TZ01/DTT3 source unavailable or requalifying");
        }
    }
    public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input,AdaptiveFanInertiaDecision baseline)
    {
        lock(_sync)
        {
            if(_closed||!_stage.Custom||_frame?.TimestampUtc!=input.Timestamp||_qualified is not{Available:true}||!baseline.Accepted)
                throw new InvalidOperationException(_reason??"Experimental acquisition is not qualified");
            var comparisons=new List<object>();
            foreach(var v in _variants)
            {
                var observation=v.Sources.Evaluate(_frame);
                // Readiness was qualified by the common observer before custom admission.
                // Variant observers still report their own first-acquisition unavailability.
                double? extra=observation.Available?observation.DemandLevel:null;
                if(v.Name=="both-retention"&&extra.HasValue)extra=Math.Min(extra.Value,_variantLevels.GetValueOrDefault(v.Name,baseline.EqualFanLevel!.Value));
                var candidate=v.Policy.Evaluate(input,extra);
                if(candidate.EqualFanLevel.HasValue)_variantLevels[v.Name]=candidate.EqualFanLevel.Value;
                comparisons.Add(new{name=v.Name,observation,extra,candidate=observation.Available?candidate:null});
            }
            double? supplemental=_stage.Controller=="both-retention"?Math.Min(_qualified.DemandLevel!.Value,_lastApplied??baseline.EqualFanLevel!.Value):null;
            var active=_active.Evaluate(input,supplemental);
            if(!active.Accepted||active.EqualFanLevel is not (>=10 and <=50)||active.RawDemandLevel<baseline.RawDemandLevel||active.ThermalOverride!=baseline.ThermalOverride)
                throw new InvalidOperationException("Experimental policy failed range/raw-demand/thermal parity invariant");
            _evaluatedTimestamp=input.Timestamp;_evaluatedStage=_stage.Index;_decisions++;
            _counts[_stage.Controller]=_counts.GetValueOrDefault(_stage.Controller)+1;
            Write("decision",new{timestamp=input.Timestamp,stage=_stage,input,baseline,comparisons,supplemental,active,
                baselineEnvelope="Same prepared 10..50 curve and tuning",physicalLevelBeforeDecision=_lastApplied});
            return active;
        }
    }
    private readonly Dictionary<string,int> _variantLevels=new();
    public void EnsureDispatchAllowed(DateTimeOffset timestamp,DateTimeOffset now)
    {
        lock(_sync)
        {
            if(_closed||!_stage.Custom||_evaluatedStage!=_stage.Index||_evaluatedTimestamp!=timestamp||_frame?.TimestampUtc!=timestamp||
                now<timestamp||now-timestamp>=TimeSpan.FromSeconds(3)||!_frame.Tz01.Fresh(now,3000)||!_frame.Dtt3.Fresh(now,3000))
                throw new InvalidOperationException(_reason??"Experimental stage/source/dispatch epoch lost");
        }
    }
    public void ObserveDuringActuation(AdaptiveFanPolicyInput input)
    {
        lock(_sync)
        {
            if(!_active.ObserveDuringActuation(input,out var failure))throw new InvalidOperationException(failure);
            foreach(var v in _variants)if(!v.Policy.ObserveDuringActuation(input,out failure))throw new InvalidOperationException(failure);
            Write("actuation-observation",input);
        }
    }
    public void NoteApplied(AdaptiveFanProductionResult result,DateTimeOffset timestamp)
    {
        lock(_sync)
        {
            if(result.Action is AdaptiveFanProductionActionKind.EnterCustomAndApply or AdaptiveFanProductionActionKind.ApplyChangedLevel or AdaptiveFanProductionActionKind.HoldCustom)
            {
                if(result.EqualFanLevel!=_lastApplied&&result.Action!=AdaptiveFanProductionActionKind.HoldCustom)_appliedChanges++;
                _lastApplied=result.EqualFanLevel;
            }
            Write("dispatch-result",new{timestamp,stage=_stage,result});
        }
    }
    public void Close(string reason)
    {
        lock(_sync){if(_closed)return;_closed=true;_reason=reason;Write("closed",new{atUtc=DateTimeOffset.UtcNow,reason});}
    }
    public void Reset()
    {
        lock(_sync){_active.Reset();foreach(var v in _variants){v.Policy.Reset();v.Sources.Reset();}_variantLevels.Clear();_lastApplied=null;_evaluatedTimestamp=null;}
    }
    public void RecordHost(string kind,object data){lock(_sync){if(!_disposed)Write(kind,data);}}
    public void Complete(object cleanup,bool protocolComplete=false)
    {
        lock(_sync)
        {
            var summary=new{schemaVersion=1,kind="VictusFanControl.PhysicalPlatformExperiment",firstUtc=_first,lastUtc=_last,
                telemetryRows=_telemetryRows,qualifiedRows=_qualifiedRows,decisions=_decisions,appliedChanges=_appliedChanges,
                cpuMaximumC=_cpuMaximum,gpuMaximumC=_gpuMaximum,decisionsByController=_counts,reason=_reason,cleanup,
                protocolComplete,physicalPassClaimed=false,
                disclosure="Completion/cleanup and log integrity are not proof of a cooling, noise, performance or safety improvement."};
            Write("completed",summary);
            var temp=Path.Combine(_directory,"summary.json.tmp");File.WriteAllText(temp,JsonSerializer.Serialize(summary,new JsonSerializerOptions(Json){WriteIndented=true}));
            File.Move(temp,Path.Combine(_directory,"summary.json"),true);
        }
    }
    public void Dispose(){lock(_sync){if(_disposed)return;_disposed=true;_trace.Dispose();}}
}
