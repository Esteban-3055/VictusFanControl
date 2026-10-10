using VictusFanControl.Product;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestManualAutomaticRetry(Action<bool,string> require)
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-manual-retry-"+Guid.NewGuid().ToString("N"));
        var exitCode=Environment.ExitCode;
        var baseline=new ProductProfiles{ActivateAutomaticOnStart=false};
        ProductRuntimeState Blocked()=>new(){Target="HP-8C40-9D0R1LA-F18",AutomaticAuthorized=true,LifecycleBlocked=true,LifecycleBlockReason="fixture interruption"};
        try
        {
            var old=new RecordingRuntime{DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};var next=new RecordingRuntime();int created=0;
            using(var form=new ProductForm("fixture://retry",fixture:old,fixtureProfiles:baseline,restartDirectory:root,
                retryRuntimeFactory:profiles=>{created++;require(profiles.Ac.CpuPl1Watts==38,"Retry lost unsaved profile.");return Task.FromResult<IProductRuntime>(next);}))
            {
                form.Show();Application.DoEvents();form.EditValue("pl1",38);var draft=ProductProfilesStore.Serialize(form.Draft);old.Publish(Blocked());
                form.Canvas.Page=ProductPage.Fans;form.Canvas.Refresh();require(form.Canvas.Hits.Single(h=>h.Id=="fan-mode-2").Enabled,"Automatic retry button remained blocked.");
                form.PresentationTick();require(created==0,"Error automatically retried without a click.");
                var retry=form.RetryAutomaticAsync();require(ReferenceEquals(retry,form.RetryAutomaticAsync()),"Double click duplicated retry.");
                PumpUntil(()=>old.Disposals==1,"Retry did not dispose old runtime.");require(created==0&&next.Commands==0,"Retry bypassed cleanup.");
                old.DisposeGate.SetResult();PumpUntil(()=>retry.IsCompleted,"Manual retry hung.");retry.GetAwaiter().GetResult();
                require(!form.IsDisposed&&form.RestartRequest is null&&created==1&&next.Starts==1&&next.Commands==0&&form.Dirty&&ProductProfilesStore.Serialize(form.Draft)==draft,"Retry closed GUI, acquired authority or changed the draft.");
                old.Publish(Blocked() with{Failure="late old event"});require(form.Canvas.State.Failure is null,"Old runtime corrupted replacement state.");
                var at=DateTimeOffset.UtcNow.AddSeconds(-2);
                ProductRuntimeState Ready(int i,double cpu=50)=>new(){Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source="Ac",AutomaticAuthorized=true,CanApplyPerformance=true,
                    Snapshot=new TelemetrySnapshot(at.AddSeconds(i),"CPU",cpu,20,50,"GPU",45,10,30,1200,1200){CpuExpectedPhysicalCoreCount=3,CpuCoreTemperatures=new[]{new CpuCoreTemperatureSample(0,0,"Performance",cpu),new CpuCoreTemperatureSample(1,2,"Performance",cpu),new CpuCoreTemperatureSample(2,4,"Performance",cpu)}}};
                next.Publish(Ready(0,96));require(next.Commands==0,"Retry armed while CPU remained hot.");
                next.Publish(Ready(0));next.Publish(Ready(0));next.Publish(Ready(1));require(next.Commands==0,"Retry counted duplicate/insufficient observations.");
                next.Publish(Ready(2));Application.DoEvents();
                PumpUntil(()=>next.Commands>=1,"Manual retry did not activate: "+form.Canvas.Notice);
                PumpUntil(()=>!form.Canvas.Busy,"Manual activation did not finish.");require(next.Commands==1,"Manual retry did not use coupled normal Automatic exactly once: "+form.Canvas.Notice);
                form.PresentationTick();require(next.Commands==1,"UI tick repeated manual request.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Successful retry could not exit.");require(old.Disposals==1&&next.Disposals==1,"Retry shutdown duplicated disposal.");
            }
            foreach(var suspend in new[]{false,true})
            {
                var failedOld=new RecordingRuntime{DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously),DisposeFailure=suspend?null:"unresolved GPU release"};int failedCreated=0;
                using var form=new ProductForm("fixture://retry",fixture:failedOld,fixtureProfiles:baseline,restartDirectory:root,
                    retryRuntimeFactory:_=>{failedCreated++;return Task.FromResult<IProductRuntime>(new RecordingRuntime());});
                form.Show();Application.DoEvents();failedOld.Publish(Blocked());var retry=form.RetryAutomaticAsync();PumpUntil(()=>failedOld.Disposals==1,"Failure fixture cleanup did not begin.");
                if(suspend)form.HandlePowerEventAsync(4).GetAwaiter().GetResult();failedOld.DisposeGate.SetResult();PumpUntil(()=>retry.IsCompleted,"Failed retry hung.");
                require(failedCreated==0&&form.Canvas.State.LifecycleBlocked&&!form.Canvas.AutomaticRetryAvailable,"Failed release/suspension recreated control.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed retry could not exit.");require(failedOld.Disposals==1,"Failed retry disposed twice.");
            }
            Console.WriteLine("Manual Automatic retry: PASS (same GUI/session, explicit click, clean release, fresh acquisitions, hot CPU refusal, draft preservation and failure/suspend fences; zero hardware IO).");
        }
        finally{Environment.ExitCode=exitCode;if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
