using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VictusFanControl.OemShadow;

public sealed class ShadowSession : IDisposable
{
    public static JsonSerializerOptions Json { get; } = new()
    { PropertyNameCaseInsensitive=true,Converters={new JsonStringEnumConverter()} };
    private readonly string _directory,_mode;
    private readonly OemFanShadowModel _model;
    private readonly ActualDebouncer _actual;
    private readonly Metrics _metrics;
    private StreamWriter _csv=null!,_jsonl=null!;
    private DateTimeOffset? _lastFrame,_summaryAt;
    private int _segment;
    public ShadowSession(string directory,Parameters parameters,string mode)
    {
        if(Directory.Exists(directory)||File.Exists(directory))throw new IOException("Output must be a new directory.");
        parameters.Validate();_directory=Path.GetFullPath(directory);Directory.CreateDirectory(_directory);
        _model=new(parameters);_actual=new(parameters);_metrics=new(parameters);_mode=mode;OpenSegment();
    }
    private void OpenSegment()
    {
        _csv=new(new FileStream(Path.Combine(_directory,$"shadow-{_segment:D5}.csv"),FileMode.CreateNew),new UTF8Encoding(false));
        _jsonl=new(new FileStream(Path.Combine(_directory,$"shadow-{_segment:D5}.jsonl"),FileMode.CreateNew),new UTF8Encoding(false));
        var names=new[]{"cpu_package","cpu_core_max","gpu","tz01","dtt3","dtt1","dtt2"};
        _csv.WriteLine(string.Join(",",new[]{"timestamp_utc"}.Concat(names.SelectMany(n=>new[]{n+"_temp_c",n+"_sampled_at_utc",n+"_age_ms"})).Concat(new[]{
            "actual_cpu_fan_level","actual_gpu_fan_level","fan_sampled_at_utc","fan_age_ms","actual_raw_state","actual_oem_state",
            "predicted_oem_state","predicted_cpu_min","predicted_cpu_max","predicted_gpu_min","predicted_gpu_max","dominant_domain",
            "dwell_ms","hysteresis","reason_code","confidence","match_state","mismatch_type","cpu_power_w","gpu_power_w","cpu_load_pct","gpu_load_pct"})));
    }
    public void Invalidate() { _model.Reset();_actual.Reset();_metrics.BreakContinuity(); }
    public void InvalidateClock() { Invalidate();_lastFrame=null;_metrics.ResetTimeline(); }
    public void Add(Frame f)
    {
        if(_lastFrame is { } last)
        {
            if(f.TimestampUtc<=last)throw new InvalidDataException("Frames must be strictly increasing.");
            if((f.TimestampUtc-last).TotalMilliseconds>_model.Settings.MaxGapMs)Invalidate();
        }
        _lastFrame=f.TimestampUtc;
        var prediction=_model.Evaluate(f);var actual=_actual.Evaluate(f);_metrics.Add(f,prediction,actual);
        bool comparable=OemFanShadowModel.Plateau(actual.State)&&OemFanShadowModel.Plateau(prediction.State);
        bool? match=comparable?actual.State==prediction.State:null;
        string mismatch=match==true?"MATCH":comparable?prediction.State<actual.State?"UNDER":"OVER":
            prediction.State==OemState.Unknown?"PREDICTION_UNKNOWN":actual.State==OemState.Unknown?"ACTUAL_UNKNOWN":"TRANSITION";
        var values=new List<object?>{f.TimestampUtc};
        foreach(var s in new[]{f.CpuPackage,f.CpuCoreMax,f.Gpu,f.Tz01,f.Dtt3,f.Dtt1??new(),f.Dtt2??new()})
            values.AddRange(new object?[]{s.Value,s.SampledAtUtc,s.AgeMs(f.TimestampUtc)});
        values.AddRange(new object?[]{f.ActualCpuLevel,f.ActualGpuLevel,f.FanSampledAtUtc,
            f.FanSampledAtUtc is { } at?(f.TimestampUtc-at).TotalMilliseconds:null,actual.RawState,actual.State,
            prediction.State,prediction.CpuRange?.Min,prediction.CpuRange?.Max,prediction.GpuRange?.Min,prediction.GpuRange?.Max,
            prediction.DominantDomain,prediction.DwellMs,prediction.Hysteresis,prediction.ReasonCode,prediction.Confidence,match,mismatch,
            f.CpuPowerW,f.GpuPowerW,f.CpuLoadPercent,f.GpuLoadPercent});
        _csv.WriteLine(string.Join(",",values.Select(Format)));
        _jsonl.WriteLine(JsonSerializer.Serialize(new{input=f,prediction,actual,matchState=match,mismatchType=mismatch},Json));
        if(_summaryAt is null||(f.TimestampUtc-_summaryAt.Value).TotalSeconds>=60){SaveSummary();_summaryAt=f.TimestampUtc;}
        if(_csv.BaseStream.Position>16*1024*1024||_jsonl.BaseStream.Position>16*1024*1024)
        { _csv.Dispose();_jsonl.Dispose();_segment++;OpenSegment(); }
    }
    private static string Format(object? value)
    {
        string s=value switch{null=>"",DateTimeOffset d=>d.ToString("O",CultureInfo.InvariantCulture),
            bool b=>b?"true":"false",IFormattable f=>f.ToString(null,CultureInfo.InvariantCulture),_=>value.ToString()??""};
        return s.IndexOfAny([',','"','\r','\n'])>=0?'"'+s.Replace("\"","\"\"")+'"':s;
    }
    public void SaveSummary()
    {
        _csv.Flush();_jsonl.Flush();
        var temp=Path.Combine(_directory,"summary.json.tmp");
        var options=new JsonSerializerOptions(Json){WriteIndented=true};
        File.WriteAllText(temp,JsonSerializer.Serialize(_metrics.Summary(_mode),options));
        File.Move(temp,Path.Combine(_directory,"summary.json"),true);
    }
    public void Dispose(){SaveSummary();_csv.Dispose();_jsonl.Dispose();}
}
