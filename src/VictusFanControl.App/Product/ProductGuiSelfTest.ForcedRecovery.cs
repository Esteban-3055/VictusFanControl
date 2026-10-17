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
        }
        finally { Environment.ExitCode=exitCode;Directory.Delete(root,true); }
    }
}
