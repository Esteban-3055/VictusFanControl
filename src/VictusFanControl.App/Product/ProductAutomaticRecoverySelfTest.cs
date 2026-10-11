namespace VictusFanControl.App;

internal static class ProductAutomaticRecoverySelfTest
{
    internal static void Run(Action<bool,string> check)
    {
        ProductRuntimeState Pending(Guid? id = null) => new() { Runtime="RecoveryRequired", LifecycleBlocked=true,
            PerformanceRecovery=new(true,"pending",id??Guid.Parse("6402ec94-9cf0-4d40-b027-53b0534a090e")) };
        var p = new ProductAutomaticRecovery(); var pending=Pending();
        check(!p.TakeRequest(pending,0) && !p.TakeRequest(pending,9999) && p.TakeRequest(pending,10000),"Recovery must wait ten seconds for a stable inventory.");
        check(!p.TakeRequest(pending,100000),"Failed/unconfirmed hardware recovery must not be retried for the same inventory.");
        check(!p.TakeRequest(pending with{LifecycleBlocked=false,FanMode="Automatic"},200000),"An active Automatic session must never be recovered.");
        var malformed=pending with{PerformanceRecovery=new(true,"invalid")};
        check(!p.TakeRequest(malformed,300000),"Illegible records cannot authorize unattended recovery.");
        var unknown=pending with{PerformanceRecovery=new(true,"unknown",FanRecords:[new("foreign","Unknown","hash","ownership unknown")])};
        check(!p.TakeRequest(unknown,300000),"Unknown ownership cannot authorize recovery.");
        p=new(); check(!p.TakeRequest(pending,0),"Initial wait bypassed.");p.Pause();
        check(!p.TakeRequest(pending,20000) && p.TakeRequest(pending,30000),"Suspension/disabled preference must restart the wait.");
        p=new();
        for(int i=0;i<4;i++)
        {
            var next=Pending(Guid.NewGuid()); var clock=i*20000;
            check(!p.TakeRequest(next,clock),"Changed inventory bypassed wait.");
            check(p.TakeRequest(next,clock+10000)==(i<3),"Recovery exceeded three episodes without healthy Automatic.");
        }
        var healthy=new ProductRuntimeState {Runtime="Healthy",FanMode="Automatic"};
        p.Observe(healthy,100000);p.Observe(healthy,159999);
        var last=Pending(Guid.NewGuid());p.TakeRequest(last,160000);
        check(!p.TakeRequest(last,170000),"Budget reset before a minute of healthy Automatic.");
        p.Observe(healthy,160000);p.Observe(last,160001);p.Observe(healthy,170000);p.Observe(healthy,230000);
        check(p.TakeRequest(last,240000),"A minute of healthy Automatic must reset the episode budget.");
        var preferences=new Product.ProductProfiles{RecoverSessionsAutomatically=false};
        check(!Product.ProductProfilesStore.Copy(preferences).RecoverSessionsAutomatically,"Automatic recovery preference did not persist.");
        Console.WriteLine("Background recovery policy: PASS (stable inventory, one attempt per inventory, bounded episodes, active-session exclusion, invalid ownership, pause and healthy reset).");
    }
}
