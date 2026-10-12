namespace VictusFanControl.App;

internal static class ProductFanAnimationSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var fan=new ProductFanAnimation();fan.Advance(0.1,100);
        require(fan.Speed>0&&fan.Speed<10&&fan.Phase>0&&fan.Phase<0.4,"Fan start snapped to full speed.");
        for(int i=0;i<100;i++)fan.Advance(0.1,100);
        require(fan.Speed>70&&fan.Speed<=72,"Fan did not settle at the more visible bounded speed.");
        var fast=fan.Speed;fan.Advance(0.1,25);
        require(fan.Speed<fast&&fan.Speed>18,"Lower demand removed the fan's inertia.");
        var slowing=fan.Speed;fan.Advance(0.1,0);
        require(fan.IsMoving&&fan.Speed<slowing,"Zero demand skipped gradual deceleration.");
        for(int i=0;i<120;i++)fan.Advance(0.1,0);
        require(!fan.IsMoving,"Stopped fan kept an animation alive indefinitely.");
        var stopped=fan.Phase;fan.Advance(0.1,0);
        require(fan.Phase==stopped,"Stopped fan continued rotating.");

        var regular=new ProductFanAnimation();var uneven=new ProductFanAnimation();
        foreach(var demand in new[]{100d,25d,50d})
            for(int i=0;i<20;i++)
            {
                regular.Advance(0.05,demand);regular.Advance(0.05,demand);
                uneven.Advance(0.02,demand);uneven.Advance(0.08,demand);
            }
        require(Math.Abs(regular.Phase-uneven.Phase)<1e-9&&Math.Abs(regular.Speed-uneven.Speed)<1e-9,
            "Irregular UI frame timing changed the fan's motion.");
        var bounded=new ProductFanAnimation();var delayed=new ProductFanAnimation();
        bounded.Advance(0.1,100);delayed.Advance(30,100);
        require(delayed.Phase==bounded.Phase&&delayed.Speed==bounded.Speed,"Delayed frame jumped forward to catch up.");
        var phase=delayed.Phase;delayed.Pause();
        require(!delayed.IsMoving&&delayed.Phase==phase,"Pause rotated or retained momentum.");
        delayed.Advance(0.1,100);
        require(delayed.Speed<10,"Resume skipped the soft start.");
        foreach(var invalid in new double?[]{null,double.NaN,double.PositiveInfinity})
        {
            delayed.Advance(0.1,100);phase=delayed.Phase;delayed.Advance(0.1,invalid);
            require(!delayed.IsMoving&&delayed.Phase==phase,"Unavailable demand fabricated rotation.");
        }
        Console.WriteLine("Fan animation: PASS (slow rotation, soft start, demand changes, finite coast-down, irregular frames, bounded delays and pause/resume; presentation only).");
    }
}
