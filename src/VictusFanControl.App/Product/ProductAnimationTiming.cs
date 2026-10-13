namespace VictusFanControl.App;

// Bounded UI-side measurements: painted cadence, not display/vsync or GPU presentation FPS.
internal sealed class ProductAnimationTiming
{
    private readonly Queue<double> _intervals=new(),_paintTimes=new();
    private double? _last;
    internal long PaintedFrames {get;private set;}
    internal void Pause()=>_last=null;
    internal void Paint(double at,double duration)
    {
        if(_last is {} previous&&at>previous)Add(_intervals,at-previous);
        _last=at;Add(_paintTimes,duration);PaintedFrames++;
    }
    private static void Add(Queue<double> samples,double value){samples.Enqueue(value);if(samples.Count>600)samples.Dequeue();}
    internal object Snapshot(string scheduler,long coalesced)
    {
        double P(Queue<double> samples,double fraction){var a=samples.Order().ToArray();return a.Length==0?0:a[(int)Math.Round((a.Length-1)*fraction)];}
        return new{clock="Stopwatch",requestedFramesPerSecond=ProductAnimationFramePump.RequestedFramesPerSecond,scheduler,
            paintedFrames=PaintedFrames,paintedFramesPerSecond=_intervals.Count==0?0:1000/_intervals.Average(),
            frameIntervalMedianMs=P(_intervals,.5),frameIntervalP95Ms=P(_intervals,.95),frameIntervalP99Ms=P(_intervals,.99),
            paintMedianMs=P(_paintTimes,.5),paintP95Ms=P(_paintTimes,.95),coalescedRequests=coalesced,
            samples=_intervals.Count,measurement="UI paints; not display presentation FPS"};
    }
}
