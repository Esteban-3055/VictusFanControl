using System.Drawing.Imaging;
using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestForcedSessionRecovery(Action<bool,string> require)
    {
        var root = Path.Combine(Path.GetTempPath(), "vfc-forced-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var exitCode = Environment.ExitCode;
        try
        {
            var old = new RecordingRuntime { DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var next = new RecordingRuntime(); var factories = 0; var recoveryRuns = 0;
            using (var form = new ProductForm("fixture://forced-session", fixture:old, fixtureProfiles:new ProductProfiles{ActivateAutomaticOnStart=true}, restartDirectory:root))
            {
                form.RecoveryRuntimeFactory = p => { factories++; require(p.Ac.CpuPl1Watts==38,"Forced recovery lost edited preferences."); return Task.FromResult<IProductRuntime>(next); };
                form.RecoveryRunner = _ => { recoveryRuns++; throw new IOException("Forced session reset selected another owner's recovery."); };
                form.Show(); Application.DoEvents(); form.EditValue("pl1",38);
                var draft = ProductProfilesStore.Serialize(form.Draft);
                old.Publish(new(){Runtime="Degraded",FanMode="Firmware",FanAuthority="Firmware"});
                form.Canvas.Page = ProductPage.Settings; form.Canvas.Refresh();
                require(form.Canvas.Hits.Any(h=>h.Id=="session-force-recovery"&&h.Enabled),"Settings has no forced session recovery without pending inventory.");
                var output=Path.GetFullPath("logs/product-gui-self-test");Directory.CreateDirectory(output);
                using(var image=new Bitmap(form.Canvas.Width,form.Canvas.Height))
                { form.Canvas.DrawToBitmap(image,new(Point.Empty,image.Size)); image.Save(Path.Combine(output,"forced-session-recovery-settings.png"),ImageFormat.Png); }
                var reset = form.ForceSessionRecoveryAsync(confirmed:true);
                require(ReferenceEquals(reset,form.ForceSessionRecoveryAsync(true)),"Double click duplicated forced recovery.");
                PumpUntil(()=>old.Disposals==1,"Forced recovery did not drain the old session.");
                require(Directory.GetFiles(root,"diagnostic-before-forced-recovery-*.zip").Length==1&&factories==0&&old.Fences==1,
                    "Forced recovery did not save evidence before draining or opened a new runtime too early.");
                old.DisposeGate.SetResult(); PumpUntil(()=>reset.IsCompleted,"Forced session reset hung."); reset.GetAwaiter().GetResult();
                require(factories==1&&next.Starts==1&&next.Commands==0&&recoveryRuns==0&&form.Dirty&&ProductProfilesStore.Serialize(form.Draft)==draft,
                    "Forced recovery lost draft, issued commands, or recovered unrelated records.");
                require(form.Canvas.State.FanMode=="Firmware"&&!form.Canvas.State.LifecycleBlocked,"Forced recovery did not finish in Firmware.");
                old.Publish(new(){Failure="late old state"});require(form.Canvas.State.Failure is null,"Old runtime republished into recovered session.");
                form.PresentationTick();require(next.Commands==0,"Saved startup preference reactivated Automatic after forced recovery.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Forced recovery fixture exit hung.");
                require(old.Disposals==1&&next.Disposals==1,"Forced recovery repeated disposal.");
            }
            foreach(var failure in new[]{"uncertain-close","remaining-record","suspend","diagnostic"})
            {
                var directory=Path.Combine(root,failure);Directory.CreateDirectory(directory);
                if(failure=="diagnostic") { directory=Path.Combine(directory,"not-a-directory");File.WriteAllText(directory,"fixture"); }
                var runtime=new RecordingRuntime{DisposeFailure=failure=="uncertain-close"?"native completion unknown":null,
                    DisposeGate=failure=="suspend"?new(TaskCreationOptions.RunContinuationsAsynchronously):null};
                var created=0;var runners=0;
                using var form=new ProductForm("fixture://forced-refusal",fixture:runtime,fixtureProfiles:new(),restartDirectory:directory);
                form.RecoveryRuntimeFactory=_=>{created++;return Task.FromResult<IProductRuntime>(new RecordingRuntime());};
                form.RecoveryRunner=_=>{runners++;throw new IOException("Forced recovery bypassed an inventory check.");};
                if(failure=="remaining-record")form.RecoverySelectionReader=()=>new(Guid.NewGuid(),Guid.Empty);
                form.Show();Application.DoEvents();
                var reset=form.ForceSessionRecoveryAsync(true);
                if(failure=="suspend")
                {
                    PumpUntil(()=>runtime.Disposals==1,"Suspend fixture did not start closing.");
                    form.HandlePowerEventAsync(4).GetAwaiter().GetResult();runtime.DisposeGate!.SetResult();
                }
                PumpUntil(()=>reset.IsCompleted,"Forced failure recovery hung.");reset.GetAwaiter().GetResult();
                require(created==0&&runners==0&&!form.Canvas.Busy&&form.RecoveryMonitorActive,"Failed forced recovery reopened control, recovered foreign records or disabled monitoring.");
                if(failure=="diagnostic")require(runtime.Fences==0&&runtime.Disposals==0,"Diagnostic failure modified the live session.");
                else require(form.Canvas.State.LifecycleBlocked&&form.Canvas.State.Failure is not null,"Forced recovery hid a failed release.");
                if(failure=="uncertain-close")require(form.Canvas.State.FanAuthority=="Unknown","Failed forced closure claimed Firmware ownership.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Forced failure fixture exit hung.");
            }
            Console.WriteLine("PASS forced session recovery: visible without pending records, diagnostic before drain, one task, fresh Firmware runtime, preserved draft, no Automatic, no foreign recovery, uncertainty/suspend/evidence-failure fences (zero hardware IO).");
            TestDefinitiveAutomaticSessionRecovery(Path.Combine(root,"automatic"), require);
        }
        finally { Environment.ExitCode=exitCode;Directory.Delete(root,true); }
    }

    private static void TestDefinitiveAutomaticSessionRecovery(string root, Action<bool,string> require)
    {
        foreach(var scenario in new[]{"resume","firmware","stop","disabled","uncertain","suspend","pending"})
        {
            Console.WriteLine("Definitive automatic recovery fixture: " + scenario);
            var old=new RecordingRuntime{DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously),DisposeFailure=scenario=="uncertain"?"unknown native completion":null};
            var next=new RecordingRuntime();var creates=0;var runs=0;
            var pending=scenario=="pending"?new ProductRecoverySelection(Guid.NewGuid(),Guid.Empty):new(Guid.Empty,Guid.Empty);
            var profiles=new ProductProfiles{RecoverSessionsAutomatically=scenario!="disabled",ActivateAutomaticOnStart=true};
            using var form=new ProductForm("fixture://definitive-auto",fixture:old,fixtureProfiles:profiles,restartDirectory:Path.Combine(root,scenario));
            form.RecoverySelectionReader=()=>pending;
            form.RecoveryRunner=s=>{runs++;require(s==pending&&old.Disposals==1,"Definitive recovery lost the captured selection or skipped drain.");pending=new(Guid.Empty,Guid.Empty);return Task.FromResult(new ProductRecoveryResult(true,"fixture","released"));};
            form.RecoveryRuntimeFactory=_=>{creates++;return Task.FromResult<IProductRuntime>(next);};
            form.Show();Application.DoEvents();
            var at=DateTimeOffset.UtcNow.AddSeconds(-2);
            ProductRuntimeState Ready(int i)=>new(){Target=ProductRecoverySelection.Target,AutomaticAuthorized=true,Runtime="Healthy",Source="Ac",FanAuthority="Firmware",CanApplyPerformance=true,
                Snapshot=new VictusFanControl.Telemetry.TelemetrySnapshot(at.AddSeconds(i),"CPU",55,20,30,"GPU",45,15,20,2000,2000)
                {CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",55)]}};
            if(scenario!="firmware")old.Publish(Ready(2) with{FanMode="Automatic"});
            old.Publish(Ready(2) with{Runtime="Degraded",LifecycleBlocked=true});
            form.EditValue("pl1",38);var draft=ProductProfilesStore.Serialize(form.Draft);
            var now=Environment.TickCount64;form.TryAutomaticRecovery(now);form.TryAutomaticRecovery(now+14999);
            require(old.Disposals==0,"Definitive recovery acted before the stable-fault wait.");
            form.TryAutomaticRecovery(now+15000);
            if(scenario=="disabled")
            {
                require(!form.Canvas.Busy&&old.Disposals==0,"Disabled recovery changed the live runtime.");
                old.DisposeGate.SetResult();var disabledExit=form.RequestExitAsync();PumpUntil(()=>disabledExit.IsCompleted,"Disabled definitive fixture exit hung.");continue;
            }
            PumpUntil(()=>old.Disposals==1,"Persistent degraded session without inventory was not recovered automatically.");
            require(creates==0&&Directory.GetFiles(Path.Combine(root,scenario),"diagnostic-before-forced-recovery-*.zip").Length==1,"Automatic recovery skipped diagnostic or opened before drain.");
            if(scenario=="stop")form.HandleCommand("firmware");
            if(scenario=="suspend")CompleteGuiTask(form.HandlePowerEventAsync(4),"Definitive suspend hung.");
            old.DisposeGate.SetResult();PumpUntil(()=>!form.Canvas.Busy,"Definitive automatic recovery did not finish.");
            if(scenario is "uncertain" or "suspend")
                require(creates==0&&runs==0&&form.Canvas.State.LifecycleBlocked&&form.RecoveryMonitorActive,"Unconfirmed/interrupted recovery reopened control or stopped monitoring.");
            else
            {
                require(creates==1&&next.Starts==1&&next.Commands==0&&ProductProfilesStore.Serialize(form.Draft)==draft,"Automatic reset duplicated the session, activated early or lost edits.");
                require(runs==(scenario=="pending"?1:0),"Unified recovery skipped selected pending records or created spurious recovery writes.");
                old.Publish(new(){Failure="late epoch"});require(form.Canvas.State.Failure is null,"Old session published after automatic reset.");
                for(var i=0;i<3;i++)next.Publish(Ready(i));
                PumpUntil(()=>!form.Canvas.Busy,"Definitive automatic reentry hung.");
                require(next.Commands==(scenario is "resume" or "pending"?1:0),"Definitive recovery ignored previous intent, user stop or fresh-observation admission.");
            }
            form.TryAutomaticRecovery(now+500000);require(old.Disposals==1,"Unconfirmed closure was replayed.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Definitive fixture exit hung.");
        }
        Console.WriteLine("PASS definitive automatic session recovery: persistent fault without inventory, diagnostic and release drain, exact pending subset, preserved draft, fresh-only previous Automatic intent, explicit Firmware stop, disabled preference, uncertain release and suspension (zero hardware IO).");
    }
}
