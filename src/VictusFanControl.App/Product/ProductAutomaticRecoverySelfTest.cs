using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static class ProductAutomaticRecoverySelfTest
{
    internal static void Run(Action<bool,string> check)
    {
        ProductRuntimeState Pending(Guid? id=null)=>new(){Runtime="RecoveryRequired",LifecycleBlocked=true,PerformanceRecovery=new(true,"pending",id??Guid.NewGuid())};
        var episode=new ProductRecoveryEpisode();var policy=new ProductAutomaticRecovery(episode);var pending=Pending();
        check(!policy.TakeRequest(pending,0)&&!policy.TakeRequest(pending,9999)&&policy.TakeRequest(pending,10000),"Initial stable-inventory wait changed.");
        check(!policy.TakeRequest(pending,100000),"Same unconfirmed inventory was repeated.");
        check(policy.DeferInventoryRead(pending.PerformanceRecovery!,10000)&&episode.Attempts==0,"Read-only failure consumed a native attempt.");
        check(!policy.TakeRequest(pending,39999)&&policy.TakeRequest(pending,40000),"Read backoff was bypassed.");
        check(policy.DeferInventoryRead(pending.PerformanceRecovery!,40000)&&policy.TakeRequest(pending,100000),"Second read-only retry failed.");
        check(policy.DeferInventoryRead(pending.PerformanceRecovery!,100000)&&episode.Attempts==0,"Exhausted read-only retries retained native authority or consumed its budget.");
        check(!policy.TakeRequest(pending,399999)&&policy.TakeRequest(pending,400000),"Exhausted read-only burst did not respect the five-minute pause.");
        policy.RecordAttempt(pending.PerformanceRecovery!);
        policy.SessionOpened();
        check(!policy.TakeRequest(pending,1000000),"Opening a session replayed a prior exact native recovery.");
        var rejected=new ProductAutomaticRecovery();
        check(!rejected.TakeRequest(pending,0)&&rejected.TakeRequest(pending,10000),"Read rejection fixture did not reserve.");
        rejected.RejectInventoryRead(pending.PerformanceRecovery!);
        check(!rejected.TakeRequest(pending,1000000),"A malformed exact inventory was retried as transient IO.");
        var changed=Pending();
        check(!rejected.TakeRequest(changed,1000001)&&rejected.TakeRequest(changed,1010001),"A different valid inventory could not qualify after read rejection.");
        foreach(var invalid in new[]{pending with{PerformanceRecovery=new(true,"invalid")},pending with{AutomaticPreparing=true},pending with{PerformanceUpdating=true},pending with{FanMode="Automatic",LifecycleBlocked=false},pending with{AutomaticReview=true}})
        {var guarded=new ProductAutomaticRecovery();check(!guarded.TakeRequest(invalid,0)&&!guarded.TakeRequest(invalid,100000),"Uncertain/active inventory was admitted.");}
        episode=new();policy=new(episode);
        var fault=new ProductRuntimeState{Runtime="Degraded",LifecycleBlocked=true};
        for(var i=0;i<3;i++)
        {
            var start=i*100000L;var delay=new[]{10000,30000,60000}[i];
            check(!policy.TakeSessionRequest(fault,start)&&!policy.TakeSessionRequest(fault,start+delay-1)&&policy.TakeSessionRequest(fault,start+delay),"Shared runtime recovery backoff changed.");
            check(episode.Attempts==i+1&&!policy.TakeSessionRequest(fault,start+delay+1),"Native attempt duplicated or was not counted.");
            policy.SessionOpened();
        }
        check(!policy.TakeSessionRequest(fault,1000000),"Budget renewed on a timer without valid sensors.");
        var at=DateTimeOffset.UtcNow.AddMinutes(-11);
        ProductRuntimeState Ready(int second,string mode="Firmware")=>new(){Runtime="Healthy",Source="Ac",FanMode=mode,FanAuthority=mode=="Automatic"?"Custom":"Firmware",AutomaticAuthorized=true,Snapshot=new TelemetrySnapshot(at.AddSeconds(second),"CPU",55,20,30,"GPU",45,15,20,2000,2000){CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",55)]}};
        for(var i=0;i<=600;i++)episode.Observe(Ready(i),at.AddSeconds(i));
        check(episode.Attempts==3,"Healthy Firmware reset the recovery budget.");
        check(episode.TryReserve(1000001)&&episode.Attempts==3,"Cooldown and verified stability did not allow one bounded new attempt.");episode.Finish();
        for(var i=601;i<=1201;i++)episode.Observe(Ready(i,"Automatic"),at.AddSeconds(i));
        check(episode.Attempts==0,"Ten minutes of real fresh Automatic failed to renew the budget.");
        check(episode.TryReserve(2000000),"Renewed episode could not reserve.");episode.Finish(ownerConfirmed:false);
        for(var i=1202;i<=1803;i++)episode.Observe(Ready(i,"Automatic"),at.AddSeconds(i));
        check(!episode.TryReserve(9000000)&&episode.Uncertain,"A timer or healthy label erased native uncertainty.");
        episode.ConfirmOwnerReleased();check(episode.TryReserve(9000001),"Exact confirmed release did not unblock a legitimate next attempt.");episode.Finish();
        check(episode.TryReserve(9100000),"New boundary unavailable.");var attempts=episode.Attempts;episode.CarryPreparation();
        check(episode.TryPrepare(9100001)&&episode.Attempts==attempts,"Automatic preparation double-counted its recovery boundary.");episode.Finish();
        Console.WriteLine("PASS shared recovery episode: exact-inventory latch, read-only refund/backoff, single reservation, shared 10/30/60s budget, stable-only cooldown, no Firmware reset, ten-minute Automatic reset, native uncertainty retained and preparation credit; zero hardware IO.");
    }
}
