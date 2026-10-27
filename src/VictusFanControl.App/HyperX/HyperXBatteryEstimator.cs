namespace VictusFanControl.App;

/// <summary>Display-only voltage estimate, not a measurement of remaining charge.
/// Reference knots are the lower voltage edges of the existing srn MIT table.
/// Interpolation fills its gaps; no extrapolation invents an uncharacterized 0..9%.</summary>
internal sealed class HyperXBatteryEstimator
{
    internal const int MinimumMillivolts=3584,MaximumMillivolts=4107;
    internal const double TimeConstantMilliseconds=60_000;
    private const double DisplayHysteresisPoints=0.75;
    private static readonly (int Millivolts,int Percent)[] Knots=
    [
        (3584,10),(3674,15),(3703,20),(3732,25),(3743,30),(3753,35),(3764,40),
        (3774,45),(3784,50),(3792,55),(3804,60),(3825,65),(3840,70),(3860,75),
        (3890,80),(3910,85),(3940,90),(3960,95),(3970,100),(4107,100)
    ];
    private double? filtered;
    private int? displayed;
    private long lastSampleMilliseconds;
    internal static double? RawPercent(int millivolts)
    {
        if(millivolts is <MinimumMillivolts or >MaximumMillivolts)return null;
        for(int i=1;i<Knots.Length;i++)
        {
            var left=Knots[i-1];var right=Knots[i];
            if(millivolts<=right.Millivolts)
                return left.Percent+(right.Percent-left.Percent)*(double)(millivolts-left.Millivolts)/(right.Millivolts-left.Millivolts);
        }
        return null;
    }
    internal void Reset(){filtered=null;displayed=null;lastSampleMilliseconds=0;}
    internal int? Observe(int millivolts,long sampleMilliseconds)
    {
        var raw=RawPercent(millivolts);
        if(raw is null){Reset();return null;}
        var elapsed=sampleMilliseconds-lastSampleMilliseconds;
        // Use acquisition time on a monotonic clock, not UI refreshes or UTC.
        // A missing/old source starts afresh rather than blending different sessions.
        if(filtered is null||elapsed<0||elapsed>=HyperXSnapshot.FreshSeconds*1000||Round(raw.Value)<=10)
        {filtered=raw;displayed=Round(raw.Value);}
        else if(elapsed>0)
            filtered+=(1-Math.Exp(-elapsed/TimeConstantMilliseconds))*(raw.Value-filtered.Value);
        // The lowest supported band bypasses smoothing so it cannot postpone
        // the default 10% critical warning. Unknown data never becomes zero.
        lastSampleMilliseconds=sampleMilliseconds;
        if(displayed is null||Math.Abs(filtered.Value-displayed.Value)>=DisplayHysteresisPoints)
            displayed=Round(filtered.Value);
        return displayed;
    }
    private static int Round(double percent)=>(int)Math.Round(Math.Clamp(percent,10,100),MidpointRounding.AwayFromZero);
}
