namespace VictusFanControl.OemShadow;

public sealed record StateEvent(OemState From, OemState To, DateTimeOffset AtUtc);
public sealed class Metrics(Parameters settings)
{
    private readonly long[][] _matrix = Enumerable.Range(0,6).Select(_=>new long[6]).ToArray();
    private readonly long[][] _uniqueMatrix = Enumerable.Range(0,6).Select(_=>new long[6]).ToArray();
    private readonly long[] _misses = new long[5];
    private readonly List<StateEvent> _actualEvents = [], _predictedEvents = [];
    private Frame? _previous;
    private OemState _previousPrediction, _lastActual, _lastPredicted;
    private DateTimeOffset? _lastFan;
    private long _samples, _uniqueFans;
    private double _unknownSeconds, _observedSeconds, _unobservedSeconds;
    private bool _eventsTruncated;
    public void BreakContinuity() { _lastActual=_lastPredicted=OemState.Unknown; }
    public void ResetTimeline() { BreakContinuity();_previous=null;_lastFan=null; }
    public void Add(Frame frame, Prediction prediction, Actual actual)
    {
        if (_previous is { } prev)
        {
            var seconds = (frame.TimestampUtc-prev.TimestampUtc).TotalSeconds;
            if (seconds > 0 && seconds*1000 <= settings.MaxGapMs)
            { _observedSeconds += seconds; if (_previousPrediction==OemState.Unknown) _unknownSeconds+=seconds; }
            else
            { _unobservedSeconds+=Math.Max(0,seconds); _lastActual=_lastPredicted=OemState.Unknown; }
        }
        _matrix[(int)actual.State][(int)prediction.State]++; _samples++;
        var sources=new[]{frame.CpuPackage,frame.CpuCoreMax,frame.Gpu,frame.Tz01,frame.Dtt3};
        for(int i=0;i<sources.Length;i++) if(!sources[i].Fresh(frame.TimestampUtc,settings.MaxSourceAgeMs))_misses[i]++;
        bool fanFresh=frame.FanSampledAtUtc is { } fan && (frame.TimestampUtc-fan).TotalMilliseconds>=0 &&
            (frame.TimestampUtc-fan).TotalMilliseconds<settings.MaxSourceAgeMs && actual.RawState!=OemState.Unknown;
        if(fanFresh && (_lastFan is null || frame.FanSampledAtUtc>_lastFan))
        { _uniqueMatrix[(int)actual.State][(int)prediction.State]++;_uniqueFans++;_lastFan=frame.FanSampledAtUtc; }
        AddEvent(actual.State,actual.AcceptedSinceUtc??frame.TimestampUtc,ref _lastActual,_actualEvents);
        AddEvent(prediction.State,frame.TimestampUtc,ref _lastPredicted,_predictedEvents);
        _previous=frame;_previousPrediction=prediction.State;
    }
    private void AddEvent(OemState state,DateTimeOffset epoch,ref OemState previous,List<StateEvent> events)
    {
        if(state==OemState.Unknown){previous=state;return;}
        if(!OemFanShadowModel.Plateau(state))return;
        if(OemFanShadowModel.Plateau(previous) && state!=previous)
        { if(events.Count<5000) events.Add(new(previous,state,epoch)); else _eventsTruncated=true; }
        previous=state;
    }
    public object Summary(string mode,string liveValidation="PENDING")
    {
        var names=Enum.GetNames<OemState>(); var used=new HashSet<int>();
        var match=new int?[_actualEvents.Count];
        // Deterministic one-to-one nearest matching, identical state pair, bounded window.
        for(int i=0;i<_actualEvents.Count;i++)
        {
            var observed=_actualEvents[i];
            var candidates=_predictedEvents.Select((e,index)=>new{e,index,delta=Math.Abs((e.AtUtc-observed.AtUtc).TotalSeconds)})
                .Where(x=>!used.Contains(x.index) && x.e.From==observed.From && x.e.To==observed.To &&
                    x.delta<=settings.TransitionMatchWindowSeconds).OrderBy(x=>x.delta).ThenBy(x=>x.index).ToArray();
            if(candidates.Length>0){match[i]=candidates[0].index;used.Add(candidates[0].index);}
        }
        double? Ratio(long num,long den)=>den==0?null:(double)num/den;
        var byState=Enumerable.Range(1,4).ToDictionary(i=>names[i],i=>new{
            actualSupport=_matrix[i].Sum(),predictedSupport=_matrix.Sum(r=>r[i]),correct=_matrix[i][i],
            recall=Ratio(_matrix[i][i],_matrix[i].Sum()),precision=Ratio(_matrix[i][i],_matrix.Sum(r=>r[i]))});
        object Direction(bool up)
        {
            bool Test(StateEvent e)=>up?e.To>e.From:e.To<e.From;
            int actualCount=_actualEvents.Count(Test),predictedCount=_predictedEvents.Count(Test);
            int matched=_actualEvents.Select((e,i)=>new{e,i}).Count(x=>Test(x.e)&&match[x.i] is not null);
            return new{actualCount,predictedCount,matched,precision=Ratio(matched,predictedCount),recall=Ratio(matched,actualCount)};
        }
        return new{
            schemaVersion=1,mode,liveValidation,goForFanControl=false,fanWriteAuthority=false,parameters=settings,
            samples=_samples,uniqueFreshFanAcquisitions=_uniqueFans,knownStateAccuracy=Ratio(Enumerable.Range(1,4).Sum(i=>_matrix[i][i]),Enumerable.Range(1,4).Sum(i=>_matrix[i].Sum())),
            perState=byState,confusionMatrix=new{rowsActual=names,columnsPredicted=names,counts=_matrix},
            uniqueAcquisitionConfusionMatrix=new{rowsActual=names,columnsPredicted=names,counts=_uniqueMatrix},
            observedSeconds=_observedSeconds,unknownPredictionSeconds=_unknownSeconds,unobservedGapSeconds=_unobservedSeconds,
            missingOrStaleFrames=new{cpuPackage=_misses[0],cpuCoreMax=_misses[1],gpu=_misses[2],tz01=_misses[3],dtt3=_misses[4]},
            transitionMetrics=new{up=Direction(true),down=Direction(false),windowSeconds=settings.TransitionMatchWindowSeconds,
                eventsTruncated=_eventsTruncated,epochRule="Actual: first distinct acquisition later accepted by debounce. Predicted: first plateau after ramp. Unknown or frame gap breaks continuity.",
                observed=_actualEvents.Select((e,i)=>new{e.From,e.To,actualAtUtc=e.AtUtc,
                    predictedAtUtc=match[i] is { } j?(DateTimeOffset?)_predictedEvents[j].AtUtc:null,
                    errorSecondsPositiveIsLate=match[i] is { } k?(double?)(_predictedEvents[k].AtUtc-e.AtUtc).TotalSeconds:null}).ToArray(),
                predicted=_predictedEvents.Select((e,i)=>new{e.From,e.To,e.AtUtc,matched=used.Contains(i)}).ToArray()},
            interpretation="Row counts include cached frames; unique acquisition matrix is also reported. Confidence is a label, not a probability. Missing support is null, not success. Seeds are not qualified for control."
        };
    }
}
