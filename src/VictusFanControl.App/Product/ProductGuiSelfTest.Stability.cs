using System.Diagnostics;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    internal Task StartupCompletion=>_startup;
    private (DateTimeOffset Utc,long Tick)? _startupFixtureClock;
    internal void SetStartupClockInFixture(DateTimeOffset? utc,long? tick)
    {if(!_isolatedRuntime)throw new InvalidOperationException("Clock injection requires isolated runtime.");_startupFixtureClock=utc.HasValue&&tick.HasValue?(utc.Value,tick.Value):null;}
    internal int PendingRuntimeCallbacks=>_runtimeMailbox?.PendingCallbacks??0;
    internal void DispatchPowerFixture(ref Message message)
    {if(!_isolatedRuntime||message.Msg!=0x218)throw new InvalidOperationException("Synthetic isolated power message required.");WndProc(ref message);}
}

internal static partial class ProductGuiSelfTest
{
    private static void WaitOptionalDiagnostics()=>PumpUntil(()=>!OptionalDiagnosticExport.Pending,"Optional fixture diagnostic did not complete.");
    private static void QualifyAutomaticAfterRecovery(ProductForm form,RecordingRuntime runtime,ProductRuntimeState ready)
    {
        var start=DateTimeOffset.UtcNow.AddSeconds(-30);var tick=Environment.TickCount64;
        try
        {
            for(var i=0;i<=30;i++)
            {
                var at=start.AddSeconds(i);form.SetStartupClockInFixture(at,tick+i*1000);
                runtime.Publish(ready with{Snapshot=ready.Snapshot! with{Timestamp=at,CpuThermalSampledAtUtc=at,GpuThermalSampledAtUtc=at,
                    CpuPowerSampledAtUtc=at,GpuPowerSampledAtUtc=at,FanSampledAtUtc=at,FanAgeCapturedAtUtc=at}});
            }
        }
        finally{form.SetStartupClockInFixture(null,null);}
    }
    internal static int RunStabilityChecks()
    {
        try
        {
            static void Require(bool value,string detail){if(!value)throw new InvalidOperationException(detail);}
            ProductAutomaticRecoverySelfTest.Run(Require);
            TestCoalescedAutomaticIntent(Require);
            TestStabilityHardening(Require);
            TestExitFailureRegressions(Require);TestStartupFailurePaths(Require);TestForcedSessionRecovery(Require);
            TestBackgroundRecovery(Require);TestManualAutomaticRetry(Require);TestProtections(Require);TestUnattendedAutomaticRetry(Require);
            TestStartupAutomatic(Require);TestSessionRestart(Require);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void TestCoalescedAutomaticIntent(Action<bool,string> require)
    {
        foreach(var scenario in new[]{"confirmed","already-active","cancelled","failed"})
        {
            var old=new RecordingRuntime{AutomaticModeFailure=scenario=="failed"?"activation rejected":null};var next=new RecordingRuntime();var created=0;
            using var form=new ProductForm("fixture://coalesced-intent",fixture:old,fixtureProfiles:new(),
                retryRuntimeFactory:p=>{created++;require(p.Ac.CpuPl1Watts==35,"Coalesced intent used a later draft.");return Task.FromResult<IProductRuntime>(next);});
            form.Show();Application.DoEvents();
            var now=DateTimeOffset.UtcNow;
            var ready=new ProductRuntimeState{Target="HP-8C40-9D0R1LA-F18",AutomaticAuthorized=true,CanApplyPerformance=true,Runtime="Healthy",Source="Ac",FanMode="Firmware",FanAuthority="Firmware",
                Snapshot=new(now,"CPU",55,20,30,"GPU",45,15,20,2000,2000){CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",55)]}};
            if(scenario=="already-active")
            {old.StateOverride=ready with{FanMode="Automatic",AutomaticResumeProfiles=ProductProfilesStore.Freeze(new())};form.EditValue("pl1",38);}
            old.Publish(ready);form.HandleCommand("fan-mode-2");require(old.Commands==1,"Automatic intent fixture did not issue its explicit request.");
            form.EditValue("pl1",38);
            if(scenario=="cancelled")form.HandleCommand("firmware");
            Task.Run(()=>
            {
                if(scenario!="failed")old.Publish(ready with{PublicationSequence=10,FanMode="Automatic",FanAuthority="Custom"});
                old.Publish(ready with{PublicationSequence=11,LifecycleBlocked=true,LifecycleBlockReason="interrupted before paint"});
            }).GetAwaiter().GetResult();
            require(form.PendingRuntimeCallbacks==1,"Coalesced intent fixture did not hold the UI.");Application.DoEvents();
            require(form.Canvas.State.PublicationSequence==11,"Interrupted latest state was not presented.");
            form.TryAutomaticResumption(Environment.TickCount64+10001);
            if(scenario is "confirmed" or "already-active")
            {
                PumpUntil(()=>created==1&&!form.Canvas.Busy,"Confirmed activation intent was lost when its Automatic frame was coalesced.");
                require(old.Disposals==1&&next.Starts==1&&next.Commands==0,"Coalesced intent skipped drain or fresh reentry qualification.");
            }
            else require(created==0&&old.Disposals==0,"Unconfirmed or cancelled intent requested recovery.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Coalesced intent fixture did not exit.");
        }
        Console.WriteLine("PASS confirmed Automatic intent survives coalesced UI frames with frozen preferences; failed activation and user cancellation cannot arm recovery; no control before fresh qualification.");
    }
    private static void TestStabilityHardening(Action<bool,string> require)
    {
        var priorExitCode=Environment.ExitCode;
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        {
            var runtime=new RecordingRuntime();
            using var form=new ProductForm("fixture://held-preflight",fixture:runtime,fixtureProfiles:new(),recoveryPreflight:()=>
            {entered.Set();if(!release.Wait(10000))throw new TimeoutException("Held preflight test guard.");return new(false,"empty");});
            try
            {
                var shown=Stopwatch.StartNew();form.Show();PumpUntil(()=>entered.IsSet,"Background preflight did not start.");
                require(shown.ElapsedMilliseconds<2000&&form.Visible&&runtime.Starts==0,"Held preflight blocked showing the window or started hardware.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"A read-only preflight held native shutdown.");
                require(runtime.Disposals==1&&runtime.Starts==0&&!form.StartupCompletion.IsCompleted,"Exit abandoned a native owner or waited on optional discovery.");
            }
            finally{release.Set();PumpUntil(()=>form.StartupCompletion.IsCompleted,"Late preflight did not finish.");}
            require(runtime.Starts==0,"Late preflight opened a controller after exit.");
        }
        using(var form=new ProductForm("fixture://power",fixture:new RecordingRuntime(),fixtureProfiles:new()))
        {
            // Covered below with a retained native release.
            form.Show();Application.DoEvents();var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Control fixture exit failed.");
        }
        foreach(var failed in new[]{false,true})
        {
            var runtime=new RecordingRuntime{ReleaseGate=failed?null:new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseFailure=failed?"native release unconfirmed":null};
            using var form=new ProductForm("fixture://power-pending",fixture:runtime,fixtureProfiles:new());form.Show();Application.DoEvents();
            var message=Message.Create(form.Handle,0x218,(IntPtr)4,IntPtr.Zero);
            var timer=Stopwatch.StartNew();form.DispatchPowerFixture(ref message);timer.Stop();
            require(timer.ElapsedMilliseconds<200&&runtime.Fences>=1,"PBT_APMSUSPEND waited for native release.");
            form.DispatchPowerFixture(ref message);
            PumpUntil(()=>runtime.Releases==1,"Synthetic native release did not start.");
            runtime.Publish(new(){Runtime="Healthy",FanMode="Manual",ManualAuthorized=true,AutomaticAuthorized=true,LifecycleBlocked=false});
            form.HandleCommand("manual-apply");form.HandleCommand("fan-mode-2");
            require(runtime.Commands==0,"A stale presentation reopened control during suspension.");
            var resume=form.HandlePowerEventAsync(18);
            if(!failed)
            {
                require(!resume.IsCompleted&&runtime.Resumes==0,"Acknowledging Windows falsely confirmed native release.");
                runtime.ReleaseGate!.SetResult();PumpUntil(()=>resume.IsCompleted,"Confirmed release did not permit telemetry resume.");
                require(runtime.Resumes==1,"Telemetry did not resume after confirmed release.");
                CompleteGuiTask(form.HandlePowerEventAsync(18),"Duplicate resume blocked.");require(runtime.Resumes==1,"Duplicate power message restarted telemetry.");
            }
            else
            {PumpUntil(()=>resume.IsCompleted,"Failed release held the window.");require(runtime.Resumes==0,"Unconfirmed release permitted resume.");}
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Power fixture shutdown failed.");require(runtime.Disposals==1,"Power notification duplicated disposal.");
        }
        var publishing=new RecordingRuntime();
        using(var form=new ProductForm("fixture://held-ui",fixture:publishing,fixtureProfiles:new()))
        {
            form.Show();Application.DoEvents();
            Task.Run(()=>
            {
                for(var i=1;i<=10000;i++)publishing.Publish(new(){PublicationSequence=i,Message=i.ToString()});
                publishing.Publish(new(){PublicationSequence=9999,Message="late"});
            }).GetAwaiter().GetResult();
            require(form.PendingRuntimeCallbacks==1,"A held UI accumulated publication callbacks.");
            Application.DoEvents();require(form.Canvas.State.PublicationSequence==10000,"Coalescing lost the newest state to a late older frame.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Held UI fixture could not exit.");
        }
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        {
            var export=OptionalDiagnosticExport.TryStart(()=>{entered.Set();release.Wait();});
            var previous=new RecordingRuntime();var next=new RecordingRuntime();
            using var form=new ProductForm("fixture://held-zip",fixture:previous,fixtureProfiles:new());
            form.RecoveryRuntimeFactory=_=>Task.FromResult<IProductRuntime>(next);
            try
            {
                require(entered.Wait(5000),"Held export did not start.");form.Show();Application.DoEvents();
                var recover=form.ForceSessionRecoveryAsync(true);PumpUntil(()=>recover.IsCompleted,"Optional export retained native recovery.");
                require(!export.IsCompleted&&previous.Disposals==1&&next.Starts==1&&next.Commands==0,"Recovery skipped owner drain or acquired Automatic while export was held.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Optional export retained exit.");
            }
            finally{release.Set();export.GetAwaiter().GetResult();}
        }
        Environment.ExitCode=priorExitCode;
        Console.WriteLine("PASS stability GUI: held startup preflight stays visible and cannot start late; real WndProc <200ms with retained/failed release; resume waits and coalesces; 10000 publications leave one UI callback; held ZIP does not retain owner recovery/exit; zero hardware IO.");
    }
}
