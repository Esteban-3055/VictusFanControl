namespace VictusFanControl.OemShadow;

public sealed record StateEvent(OemState From, OemState To, DateTimeOffset AtUtc);
public sealed class Metrics(Parameters settings)
{
    private static readonly int StateCount=Enum.GetNames<OemState>().Length;
    private readonly long[][] _matrix = Enumerable.Range(0,StateCount).Select(_=>new long[StateCount]).ToArray();
    private readonly long[][] _uniqueMatrix = Enumerable.Range(0,StateCount).Select(_=>new long[StateCount]).ToArray();
    private readonly long[] _misses = new long[5];
    private readonly List<StateEvent> _actualEvents = [], _predictedEvents = [];
    private readonly List<StateEvent> _unmappedEndpointChanges=[];
    private OemState _lastMappedEndpoint;
    private bool _unmappedSinceEndpoint;
    private Frame? _previous;
    private OemState _previousPrediction, _lastActual, _lastPredicted;
    private DateTimeOffset? _lastFan;
    private long _samples, _uniqueFans;
    private double _unknownSeconds, _observedSeconds, _unobservedSeconds;
    private bool _eventsTruncated;
    private readonly Dictionary<string,long> _regimeFrames=[],_regimeAcquisitions=[];
    private readonly List<ObservedRegime> _stableCandidates=[];
    private bool _regimesTruncated;
    private long _rangeComparable,_aboveRange,_belowRange,_insideRange;
    private double _unmappedSeconds,_stableUnmappedSeconds;
    private Actual? _previousActual;
    public void BreakContinuity() { _lastActual=_lastPredicted=_lastMappedEndpoint=OemState.Unknown; _unmappedSinceEndpoint=false; }
    public void ResetTimeline() { BreakContinuity();_previous=null;_previousActual=null;_lastFan=null; }
    public void Add(Frame frame, Prediction prediction, Actual actual)
    {
        if (_previous is { } prev)
        {
            var seconds = (frame.TimestampUtc-prev.TimestampUtc).TotalSeconds;
            if (seconds > 0 && seconds*1000 <= settings.MaxGapMs)
            { _observedSeconds += seconds; if (_previousPrediction==OemState.Unknown) _unknownSeconds+=seconds;
                if(_previousActual?.State==OemState.Unmapped)_unmappedSeconds+=seconds;
                if(_previousActual?.Regime?.Kind=="UnmappedStableCandidate")_stableUnmappedSeconds+=seconds; }
            else
            { _unobservedSeconds+=Math.Max(0,seconds); BreakContinuity(); }
        }
        _matrix[(int)actual.State][(int)prediction.State]++; _samples++;
        var sources=new[]{frame.CpuPackage,frame.CpuCoreMax,frame.Gpu,frame.Tz01,frame.Dtt3};
        for(int i=0;i<sources.Length;i++) if(!sources[i].Fresh(frame.TimestampUtc,settings.MaxSourceAgeMs))_misses[i]++;
        bool fanFresh=frame.FanSampledAtUtc is { } fan && (frame.TimestampUtc-fan).TotalMilliseconds>=0 &&
            (frame.TimestampUtc-fan).TotalMilliseconds<settings.MaxSourceAgeMs && actual.RawState!=OemState.Unknown;
        bool distinct=fanFresh && (_lastFan is null || frame.FanSampledAtUtc>_lastFan);
        if(distinct)
        { _uniqueMatrix[(int)actual.State][(int)prediction.State]++;_uniqueFans++;_lastFan=frame.FanSampledAtUtc; }
        if(actual.Regime is { } regime)
        {
            _regimeFrames[regime.Kind]=_regimeFrames.GetValueOrDefault(regime.Kind)+1;
            if(distinct)_regimeAcquisitions[regime.Kind]=_regimeAcquisitions.GetValueOrDefault(regime.Kind)+1;
            if(regime.Kind=="UnmappedStableCandidate")
            {
                if(_stableCandidates.Count>0 && _stableCandidates[^1].SinceUtc==regime.SinceUtc)_stableCandidates[^1]=regime;
                else if(_stableCandidates.Count<5000)_stableCandidates.Add(regime);
                else _regimesTruncated=true;
            }
        }
        if(fanFresh && prediction.CpuRange is { } cpu && prediction.GpuRange is { } gpu)
        {
            _rangeComparable++;
            bool above=frame.ActualCpuLevel>cpu.Max || frame.ActualGpuLevel>gpu.Max,
                below=frame.ActualCpuLevel<cpu.Min || frame.ActualGpuLevel<gpu.Min;
            if(above)_aboveRange++; if(below)_belowRange++; if(!above&&!below)_insideRange++;
        }
        if(actual.State==OemState.Unknown) { _lastMappedEndpoint=OemState.Unknown;_unmappedSinceEndpoint=false; }
        else if(actual.State==OemState.Unmapped) _unmappedSinceEndpoint=true;
        else if(OemFanShadowModel.Plateau(actual.State))
        {
            if(_unmappedSinceEndpoint && OemFanShadowModel.Plateau(_lastMappedEndpoint) && actual.State!=_lastMappedEndpoint)
            { if(_unmappedEndpointChanges.Count<5000)_unmappedEndpointChanges.Add(new(_lastMappedEndpoint,actual.State,actual.AcceptedSinceUtc??frame.TimestampUtc));else _eventsTruncated=true; }
            _lastMappedEndpoint=actual.State;_unmappedSinceEndpoint=false;
        }
        AddEvent(actual.State,actual.AcceptedSinceUtc??frame.TimestampUtc,ref _lastActual,_actualEvents);
        AddEvent(prediction.State,frame.TimestampUtc,ref _lastPredicted,_predictedEvents);
        _previous=frame;_previousPrediction=prediction.State;_previousActual=actual;
    }
    private void AddEvent(OemState state,DateTimeOffset epoch,ref OemState previous,List<StateEvent> events)
    {
        if(state is OemState.Unknown or OemState.Unmapped){previous=OemState.Unknown;return;}
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
            schemaVersion=2,mode,liveValidation,goForFanControl=false,fanWriteAuthority=false,parameters=settings,
            samples=_samples,uniqueFreshFanAcquisitions=_uniqueFans,knownStateAccuracy=Ratio(Enumerable.Range(1,4).Sum(i=>_matrix[i][i]),Enumerable.Range(1,4).Sum(i=>_matrix[i].Sum())),
            perState=byState,confusionMatrix=new{rowsActual=names,columnsPredicted=names,counts=_matrix},
            uniqueAcquisitionConfusionMatrix=new{rowsActual=names,columnsPredicted=names,counts=_uniqueMatrix},
            classificationCoverage=new{knownPlateauFrames=Enumerable.Range(1,4).Sum(i=>_matrix[i].Sum()),
                knownPlateauFraction=Ratio(Enumerable.Range(1,4).Sum(i=>_matrix[i].Sum()),_samples),
                unmappedFrames=_matrix[(int)OemState.Unmapped].Sum(),transitionFrames=_matrix[(int)OemState.Transition].Sum(),
                unknownActualFrames=_matrix[0].Sum(),knownStateAccuracyDenominator="Only actual A-D frames; excludes Unmapped, Transition and Unknown."},
            observedRegimes=new{frameCounts=_regimeFrames,distinctAcquisitionCounts=_regimeAcquisitions,
                unmappedSeconds=_unmappedSeconds,stableCandidateSeconds=_stableUnmappedSeconds,
                stableCandidates=_stableCandidates,candidatesTruncated=_regimesTruncated,
                interpretation="Causal envelopes of unmapped pairs; stable candidates are observations, not qualified OEM states."},
            rawFanRangeComparison=new{comparableFrames=_rangeComparable,insideBothRanges=_insideRange,
                actualAbovePredictedUpperBound=_aboveRange,actualBelowPredictedLowerBound=_belowRange,
                interpretation="All fresh raw fan pairs, including Unmapped. Either fan suffices; above/below may overlap for asymmetric pairs. Exact level comparison, no classification tolerance."},
            observedSeconds=_observedSeconds,unknownPredictionSeconds=_unknownSeconds,unobservedGapSeconds=_unobservedSeconds,
            missingOrStaleFrames=new{cpuPackage=_misses[0],cpuCoreMax=_misses[1],gpu=_misses[2],tz01=_misses[3],dtt3=_misses[4]},
            transitionMetrics=new{up=Direction(true),down=Direction(false),windowSeconds=settings.TransitionMatchWindowSeconds,
                eventsTruncated=_eventsTruncated,epochRule="Actual: first distinct acquisition later accepted by debounce. Predicted: first plateau after ramp. Unknown, Unmapped or frame gap breaks continuity.",
                endpointChangesAcrossUnmapped=_unmappedEndpointChanges,
                endpointChangeInterpretation="Known endpoints separated by Unmapped observations. Retained for investigation, excluded from direct A-D transition scores; the intermediate path is not modeled.",
                observed=_actualEvents.Select((e,i)=>new{e.From,e.To,actualAtUtc=e.AtUtc,
                    predictedAtUtc=match[i] is { } j?(DateTimeOffset?)_predictedEvents[j].AtUtc:null,
                    errorSecondsPositiveIsLate=match[i] is { } k?(double?)(_predictedEvents[k].AtUtc-e.AtUtc).TotalSeconds:null}).ToArray(),
                predicted=_predictedEvents.Select((e,i)=>new{e.From,e.To,e.AtUtc,matched=used.Contains(i)}).ToArray()},
            interpretation="Row counts include cached frames; unique acquisition matrix is also reported. Confidence is a label, not a probability. Missing support is null, not success. Seeds are not qualified for control."
        };
    }
}
