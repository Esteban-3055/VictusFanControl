namespace VictusFanControl.App;

internal static class ProductFanAnimationSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var fan=new ProductFanAnimation();fan.Advance(0.1,6000);
        require(fan.Speed>0&&fan.Speed<3&&fan.Phase>0&&fan.Phase<0.2,"Fan start snapped to full speed.");
        for(int i=0;i<100;i++)fan.Advance(0.1,6000);
        require(fan.Speed>23&&fan.Speed<=24,"Fan did not settle at a slow bounded speed.");
        var fast=fan.Speed;fan.Advance(0.1,1500);
        require(fan.Speed<fast&&fan.Speed>6,"Lower RPM removed the fan's inertia.");
        var slowing=fan.Speed;fan.Advance(0.1,0);
        require(fan.IsMoving&&fan.Speed<slowing,"Zero RPM skipped gradual deceleration.");
        for(int i=0;i<120;i++)fan.Advance(0.1,0);
        require(!fan.IsMoving,"Stopped fan kept an animation alive indefinitely.");
        var stopped=fan.Phase;fan.Advance(0.1,0);
        require(fan.Phase==stopped,"Stopped fan continued rotating.");

        var regular=new ProductFanAnimation();var uneven=new ProductFanAnimation();
        foreach(var rpm in new[]{6000d,1500d,3000d})
            for(int i=0;i<20;i++)
            {
                regular.Advance(0.05,rpm);regular.Advance(0.05,rpm);
                uneven.Advance(0.02,rpm);uneven.Advance(0.08,rpm);
            }
        require(Math.Abs(regular.Phase-uneven.Phase)<1e-9&&Math.Abs(regular.Speed-uneven.Speed)<1e-9,
            "Irregular UI frame timing changed the fan's motion.");
        var bounded=new ProductFanAnimation();var delayed=new ProductFanAnimation();
        bounded.Advance(0.1,6000);delayed.Advance(30,6000);
        require(delayed.Phase==bounded.Phase&&delayed.Speed==bounded.Speed,"Delayed frame jumped forward to catch up.");
        var phase=delayed.Phase;delayed.Pause();
        require(!delayed.IsMoving&&delayed.Phase==phase,"Pause rotated or retained momentum.");
        delayed.Advance(0.1,6000);
        require(delayed.Speed<3,"Resume skipped the soft start.");
        foreach(var invalid in new double?[]{null,double.NaN,double.PositiveInfinity})
        {
            delayed.Advance(0.1,6000);phase=delayed.Phase;delayed.Advance(0.1,invalid);
            require(!delayed.IsMoving&&delayed.Phase==phase,"Unavailable RPM fabricated rotation.");
        }
        Console.WriteLine("Fan animation: PASS (slow rotation, soft start, RPM changes, finite coast-down, irregular frames, bounded delays and pause/resume; presentation only).");
    }
}
