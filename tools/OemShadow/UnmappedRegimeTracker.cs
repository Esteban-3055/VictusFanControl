namespace VictusFanControl.OemShadow;

// Causal descriptive evidence, independent of temperature and prediction. This does not
// assign a new OEM state or qualify its thresholds. Cached acquisitions never advance it.
public sealed class UnmappedRegimeTracker(Parameters settings)
{
    private DateTimeOffset? _since;
    private int _cpuMin, _cpuMax, _gpuMin, _gpuMax, _count;
    public void Reset() { _since=null; _count=0; }
    public ObservedRegime Add(DateTimeOffset at,int cpu,int gpu)
    {
        int cpuMin=Math.Min(_cpuMin,cpu),cpuMax=Math.Max(_cpuMax,cpu),
            gpuMin=Math.Min(_gpuMin,gpu),gpuMax=Math.Max(_gpuMax,gpu);
        if (_since is null || cpuMax-cpuMin>settings.ActualUnmappedMaximumSpan ||
            gpuMax-gpuMin>settings.ActualUnmappedMaximumSpan)
        { _since=at; _cpuMin=_cpuMax=cpu; _gpuMin=_gpuMax=gpu; _count=1; }
        else { _cpuMin=cpuMin; _cpuMax=cpuMax; _gpuMin=gpuMin; _gpuMax=gpuMax; _count++; }
        bool stable=_count>=settings.ActualUnmappedMinimumAcquisitions &&
            (at-_since.Value).TotalSeconds>=settings.ActualUnmappedStableSeconds;
        return new(stable?"UnmappedStableCandidate":"UnmappedUnresolved",new(_cpuMin,_cpuMax),
            new(_gpuMin,_gpuMax),_since.Value,_count);
    }
}
