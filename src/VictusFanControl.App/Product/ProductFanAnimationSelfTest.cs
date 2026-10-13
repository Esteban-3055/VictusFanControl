namespace VictusFanControl.App;

internal static class ProductFanAnimationSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        require(ProductFanAnimation.SimulatedRpm(30)==3000&&ProductFanAnimation.TargetDegreesPerSecond(30)==720&&
            ProductFanAnimation.TargetDegreesPerSecond(10)==240&&ProductFanAnimation.TargetDegreesPerSecond(50)==1200,
            "Request did not map to simulated RPM and the selected half-second visual revolution.");
        var fan=new ProductFanAnimation();fan.Advance(0.1,30);
        require(fan.Speed>0&&fan.Speed<60&&fan.Phase>0&&fan.Phase<3,"Fan start snapped to full speed.");
        for(int i=0;i<100;i++)fan.Advance(0.1,30);
        require(fan.Speed>719&&fan.Speed<=720,"Fan did not settle at a half-second revolution at request 30.");
        var fast=fan.Speed;fan.Advance(0.1,10);
        require(fan.Speed<fast&&fan.Speed>240,"Lower request removed the fan's inertia.");
        var slowing=fan.Speed;fan.Advance(0.1,0);
        require(fan.IsMoving&&fan.Speed<slowing,"Zero request skipped gradual deceleration.");
        for(int i=0;i<200;i++)fan.Advance(0.1,0);
        require(!fan.IsMoving,"Stopped fan kept an animation alive indefinitely.");
        var stopped=fan.Phase;fan.Advance(0.1,0);
        require(fan.Phase==stopped,"Stopped fan continued rotating.");

        var regular=new ProductFanAnimation();var uneven=new ProductFanAnimation();
        foreach(var request in new[]{50d,10d,30d})
            for(int i=0;i<20;i++)
            {
                regular.Advance(0.05,request);regular.Advance(0.05,request);
                uneven.Advance(0.02,request);uneven.Advance(0.08,request);
            }
        require(Math.Abs(regular.Phase-uneven.Phase)<1e-9&&Math.Abs(regular.Speed-uneven.Speed)<1e-9,
            "Irregular UI frame timing changed the fan's motion.");
        var bounded=new ProductFanAnimation();var delayed=new ProductFanAnimation();
        bounded.Advance(0.1,30);delayed.Advance(30,30);
        require(delayed.Phase==bounded.Phase&&delayed.Speed==bounded.Speed,"Delayed frame jumped forward to catch up.");
        var phase=delayed.Phase;delayed.Pause();
        require(!delayed.IsMoving&&delayed.Phase==phase,"Pause rotated or retained momentum.");
        delayed.Advance(0.1,30);
        require(delayed.Speed<60,"Resume skipped the soft start.");
        foreach(var invalid in new double?[]{null,double.NaN,double.PositiveInfinity,-1,51})
        {
            delayed.Advance(0.1,30);phase=delayed.Phase;delayed.Advance(0.1,invalid);
            require(!delayed.IsMoving&&delayed.Phase==phase,"Unavailable request fabricated rotation.");
        }
        Console.WriteLine("Fan animation: PASS (request x100, half-second revolution at level 30, soft start, request changes, finite coast-down, irregular frames, bounded delays and pause/resume; presentation only).");
    }
}
