namespace VictusFanControl.App;

internal static class ProductFanAnimationSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        require(ProductFanAnimation.TargetDegreesPerSecond(3000)==360&&
            ProductFanAnimation.TargetDegreesPerSecond(1000)==120&&ProductFanAnimation.TargetDegreesPerSecond(5000)==600,
            "Measured RPM did not map to a one-second visual revolution at 3000 RPM.");
        var fan=new ProductFanAnimation();fan.Advance(0.1,3000);
        require(fan.Speed>0&&fan.Speed<30&&fan.Phase>0&&fan.Phase<2,"Fan start snapped to full speed.");
        for(int i=0;i<100;i++)fan.Advance(0.1,3000);
        require(fan.Speed>359&&fan.Speed<=360,"Fan did not settle at a one-second revolution at 3000 RPM.");
        foreach(var invalid in new double?[]{null,double.NaN,double.PositiveInfinity,-1,10001})
        {
            var before=fan.Speed;var phase=fan.Phase;fan.Advance(0.1,invalid);
            require(fan.Speed>=before&&fan.IsMoving&&fan.Phase!=phase,"Missing/invalid RPM reset inertia or stopped a running fan.");
            var empty=new ProductFanAnimation();empty.Advance(0.1,invalid);
            require(!empty.IsMoving&&empty.Phase==0,"Unavailable first RPM fabricated rotation.");
        }
        var fast=fan.Speed;fan.Advance(0.1,1000);
        require(fan.Speed<fast&&fan.Speed>120,"Lower RPM removed the fan's inertia.");
        var slowing=fan.Speed;fan.Advance(0.1,0);
        require(fan.IsMoving&&fan.Speed<slowing,"Measured zero skipped gradual deceleration.");
        for(int i=0;i<200;i++)fan.Advance(0.1,null);
        require(!fan.IsMoving,"Last measured zero did not finish deceleration during a gap.");
        var stopped=fan.Phase;fan.Advance(0.1,0);
        require(fan.Phase==stopped,"Stopped fan continued rotating.");

        var regular=new ProductFanAnimation();var uneven=new ProductFanAnimation();
        foreach(var rpm in new[]{5000d,1000d,3000d})
            for(int i=0;i<20;i++)
            {
                regular.Advance(0.05,rpm);regular.Advance(0.05,rpm);
                uneven.Advance(0.02,rpm);uneven.Advance(0.08,rpm);
            }
        require(Math.Abs(regular.Phase-uneven.Phase)<1e-9&&Math.Abs(regular.Speed-uneven.Speed)<1e-9,
            "Irregular UI frame timing changed the fan's motion.");
        var bounded=new ProductFanAnimation();var delayed=new ProductFanAnimation();
        bounded.Advance(0.1,3000);delayed.Advance(30,3000);
        require(delayed.Phase==bounded.Phase&&delayed.Speed==bounded.Speed,"Delayed frame jumped forward to catch up.");
        var savedPhase=fan.Phase;var savedSpeed=fan.Speed;fan.Advance(0,3000);
        require(fan.Phase==savedPhase&&fan.Speed==savedSpeed,"A zero elapsed resume frame changed position or inertia.");
        Console.WriteLine("Fan animation: PASS (measured RPM, one-second revolution at 3000 RPM, soft start, retained target across gaps, genuine zero coast-down, irregular frames and bounded delays; presentation only).");
    }
}
