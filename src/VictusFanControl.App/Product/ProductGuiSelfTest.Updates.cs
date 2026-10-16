using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void CompleteGuiTask(Task task, string failure)
    { PumpUntil(() => task.IsCompleted, failure); task.GetAwaiter().GetResult(); }

    internal static int RunRecoveryChecks()
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            TestGuidedRecovery(Require); ProductAutomaticRecoverySelfTest.Run(Require);
            TestBackgroundRecovery(Require); TestUpdatePage(Require); TestUpdateHandoff(Require);
            return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL recovery checks: " + ex);return 1;}
    }
    private static ProductUpdate FutureUpdate(long size)
    {
        var installed=ProductRelease.NumericVersion;
        var version=new Version(installed.Major,installed.Minor+1,0);
        return new(version,new Uri($"https://github.com/Esteban-3055/VictusFanControl/releases/download/v{version}/VictusFanControl-{version}-Setup-win-x64.exe"),size,new string('a',64));
    }
    private static void TestUpdatePage(Action<bool,string> require)
    {
        var exitCode=Environment.ExitCode;
        var preferencePath=Path.Combine(Path.GetTempPath(),"Victus-update-fixture-"+Guid.NewGuid().ToString("N"),"profiles.json");
        var update=FutureUpdate(1024);
        try
        {
            var runtime=new RecordingRuntime();
            using(var form=new ProductForm("fixture://updates",fixture:runtime,fixtureProfiles:new ProductProfiles(),profilesPath:preferencePath))
            {
                form.Show();Application.DoEvents();form.HandleCommand("page-"+(int)ProductPage.Updates);form.Canvas.Refresh();
                require(form.Canvas.Hits.Single(h=>h.Id=="page-"+(int)ProductPage.Updates).Label=="Actualizaciones"&&!form.Canvas.Hits.Single(h=>h.Id=="updates-install").Enabled,"Updates navigation/install admission missing.");
                var gate=new TaskCompletionSource<ProductUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);int queries=0;
                var check=form.CheckUpdateAsync(_=>{queries++;return gate.Task;});form.CheckUpdateAsync(_=>{queries++;return Task.FromResult<ProductUpdate?>(update);}).GetAwaiter().GetResult();
                require(queries==1&&form.Canvas.UpdateBusy,"Parallel update queries were admitted.");
                form.HandleCommand("page-0");gate.SetResult(update);PumpUntil(()=>check.IsCompleted,"Update query hung.");check.GetAwaiter().GetResult();
                require(form.Canvas.AvailableUpdate==update&&form.Canvas.UpdateCheckedAt is not null&&runtime.Commands==0&&!form.Canvas.UpdateBusy,"Update check lost result or wrote hardware.");
                form.HandleCommand("updates-page");form.Canvas.Refresh();require(form.Canvas.Hits.Single(h=>h.Id=="updates-download").Enabled&&!form.Canvas.Hits.Single(h=>h.Id=="updates-install").Enabled,"Download/install separation missing.");
                form.HandleCommand("updates-prerelease-toggle");form.Canvas.Refresh();
                require(form.Draft.IncludePrereleaseUpdates&&form.Canvas.Dirty&&form.Canvas.AvailableUpdate is null&&runtime.Commands==0,"Channel toggle did not invalidate result or touched hardware.");
                var channelGate=new TaskCompletionSource<ProductUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var channelCheck=form.CheckUpdateAsync(_=>channelGate.Task);
                form.HandleCommand("updates-prerelease-toggle");require(form.Draft.IncludePrereleaseUpdates,"Busy channel toggle was admitted.");
                form.HandleCommand("discard");channelGate.SetResult(update with{IsPrerelease=true});PumpUntil(()=>channelCheck.IsCompleted,"Channel query hung.");
                require(!form.Draft.IncludePrereleaseUpdates&&form.Canvas.AvailableUpdate is null&&!form.Canvas.UpdateBusy,"Query from discarded preview channel was published.");
                form.Canvas.AvailableUpdate=update with{IsPrerelease=true};int blockedPreviewDownloads=0;
                form.DownloadUpdateAsync((_,_)=>{blockedPreviewDownloads++;return Task.FromResult("fixture");}).GetAwaiter().GetResult();
                require(blockedPreviewDownloads==0&&form.Canvas.AvailableUpdate is null,"Stable channel downloaded stale preview result.");
                form.HandleCommand("updates-prerelease-toggle");form.HandleCommand("save");
                PumpUntil(()=>!form.Canvas.Busy&&!form.Canvas.Dirty,"Preview preference save hung.");
                require(ProductProfilesStore.Parse(File.ReadAllText(preferencePath)).IncludePrereleaseUpdates&&runtime.Commands==0,"Saved preview channel lost preference or touched hardware.");
                form.ClientSize=new Size(1040,660);form.PerformLayout();form.Canvas.Refresh();
                require(form.Canvas.Hits.Single(h=>h.Id=="updates-prerelease-toggle").Enabled,"Minimum layout hid preview control.");
                Directory.CreateDirectory(Path.Combine("logs","product-gui-self-test"));
                using(var bitmap=new Bitmap(form.Canvas.Width,form.Canvas.Height))
                {form.Canvas.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine("logs","product-gui-self-test","updates-preview-minimum-layout.png"));}
                form.Canvas.AvailableUpdate=update;
                form.EditValue("pl1",38);form.Canvas.Refresh();require(!form.Canvas.Hits.Single(h=>h.Id=="updates-install").Enabled,"Dirty draft can install.");
                int downloads=0;var downloaded=form.DownloadUpdateAsync((_,_)=>{downloads++;return Task.FromResult("fixture");});PumpUntil(()=>downloaded.IsCompleted,"Dirty download hung.");
                require(downloads==1&&form.Canvas.DownloadedUpdate==update&&runtime.Disposals==0,"Download changed runtime or could not preserve a draft.");
                bool dirtyLaunch=false;form.InstallDownloadedUpdateAsync(()=>{},()=>dirtyLaunch=true,_=>Task.CompletedTask).GetAwaiter().GetResult();require(!dirtyLaunch,"Dirty draft installed.");
                form.HandleCommand("discard");
                var failed=form.CheckUpdateAsync(_=>Task.FromException<ProductUpdate?>(new IOException("offline")));PumpUntil(()=>failed.IsCompleted,"Failed check hung.");
                require(form.Canvas.AvailableUpdate is null&&form.Canvas.DownloadedUpdate==update&&form.Canvas.UpdateStatus.Contains("offline")&&!form.Canvas.UpdateBusy,"Failed check retained stale update/admission.");
                var latest=form.CheckUpdateAsync(_=>Task.FromResult<ProductUpdate?>(null));PumpUntil(()=>latest.IsCompleted,"No-update check hung.");require(form.Canvas.AvailableUpdate is null&&form.Canvas.UpdateStatus.Contains("No hay"),"Latest-version status missing.");
                form.Canvas.AvailableUpdate=update;
                var bad=form.DownloadUpdateAsync((_,_)=>Task.FromException<string>(new InvalidDataException("SHA-256 mismatch")));
                PumpUntil(()=>bad.IsCompleted,"Bad download hung.");require(!form.IsDisposed&&runtime.Disposals==0&&form.Canvas.UpdateStatus.Contains("SHA-256")&&!form.Canvas.UpdateBusy,"Bad download released runtime or lost error.");
                var downloadGate=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);bool launched=false;
                var interrupted=form.DownloadUpdateAsync((_,_)=>downloadGate.Task);
                CompleteGuiTask(form.HandlePowerEventAsync(4),"Power event fixture hung.");CompleteGuiTask(form.HandlePowerEventAsync(18),"Power event fixture hung.");
                downloadGate.SetResult("fixture");PumpUntil(()=>interrupted.IsCompleted,"Interrupted download hung.");
                require(!launched&&runtime.Disposals==0&&!form.IsDisposed&&form.Canvas.DownloadedUpdate==update,"Suspend during download lost the saved installer or launched it.");
                form.HandleCommand("updates-page");form.Canvas.Refresh();require(form.Canvas.Hits.Single(h=>h.Id=="updates-install").Enabled,"Resume did not retain explicit install action.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Update page could not exit.");
            }
            Console.WriteLine("Updates fixture: verified download and clean handoff");
            runtime=new RecordingRuntime();
            using(var form=new ProductForm("fixture://updates",fixture:runtime,fixtureProfiles:new ProductProfiles{IncludePrereleaseUpdates=true}))
            {
                form.Show();Application.DoEvents();form.Canvas.AvailableUpdate=update with{IsPrerelease=true};bool released=false,launched=false;
                var download=form.DownloadUpdateAsync((_,_)=>Task.FromResult("fixture"));PumpUntil(()=>download.IsCompleted,"Download hung.");
                require(form.Canvas.DownloadedUpdate is {IsPrerelease:true}&&runtime.Disposals==0&&!form.IsDisposed,"Download automatically shut down the runtime.");
                var install=form.InstallDownloadedUpdateAsync(()=>{released=true;require(runtime.Disposals==1,"Install skipped cleanup.");},()=>{require(released,"Install launch preceded release.");launched=true;},_=>Task.CompletedTask);
                PumpUntil(()=>install.IsCompleted,"Download/install hung.");require(launched&&form.IsDisposed,"Page download did not hand off to installer.");
            }
            Console.WriteLine("Updates fixture: cancelled installer retry");
            runtime=new RecordingRuntime();
            using(var form=new ProductForm("fixture://retry-install",fixture:runtime,fixtureProfiles:new()))
            {
                form.Show();Application.DoEvents();form.HandleCommand("updates-page");form.Canvas.AvailableUpdate=update;int downloads=0,attempts=0;
                var download=form.DownloadUpdateAsync((_,_)=>{downloads++;return Task.FromResult("fixture");});PumpUntil(()=>download.IsCompleted,"Retry download hung.");
                var first=form.InstallDownloadedUpdateAsync(()=>{},()=>{attempts++;throw new IOException("UAC cancelled");},_=>Task.CompletedTask);
                PumpUntil(()=>first.IsCompleted,"First install failed to finish.");
                require(!form.IsDisposed&&form.Canvas.DownloadedUpdate==update&&form.Canvas.UpdateStatus.Contains("se conserva")&&runtime.Disposals==1,"Launch failure discarded cache or repeated cleanup.");
                form.Canvas.Refresh();require(form.Canvas.Hits.Single(h=>h.Id=="updates-install").Enabled,"Failed launch lost install retry.");
                CompleteGuiTask(form.HandlePowerEventAsync(0x8013,0),"Power event fixture hung.");bool verifiedWhileOff=false;
                form.InstallDownloadedUpdateAsync(()=>{},()=>attempts++,_=>{verifiedWhileOff=true;return Task.CompletedTask;}).GetAwaiter().GetResult();
                require(!verifiedWhileOff&&attempts==1,"A released runtime forgot display-off state and installed.");
                CompleteGuiTask(form.HandlePowerEventAsync(0x8013,1),"Power event fixture hung.");
                var retry=form.InstallDownloadedUpdateAsync(()=>{},()=>attempts++,_=>Task.CompletedTask);PumpUntil(()=>retry.IsCompleted,"Install retry hung.");
                require(form.IsDisposed&&downloads==1&&attempts==2&&runtime.Disposals==1,$"Install retry failed: disposed={form.IsDisposed}; downloads={downloads}; attempts={attempts}; disposals={runtime.Disposals}; status={form.Canvas.UpdateStatus}.");
            }
            Console.WriteLine("Updates fixture: disabled automatic recovery");
            var pending=new ProductRecoverySelection(Guid.NewGuid(),Guid.Empty);runtime=new RecordingRuntime();var recoveredRuntime=new RecordingRuntime();
            using(var form=new ProductForm("fixture://pending-update",fixture:runtime,fixtureProfiles:new(){RecoverSessionsAutomatically=false}))
            {
                form.RecoverySelectionReader=()=>pending;
                form.RecoveryRunner=_=>{pending=new(Guid.Empty,Guid.Empty);return Task.FromResult(new ProductRecoveryResult(true,"fixture-evidence","recovered"));};
                form.RecoveryRuntimeFactory=_=>Task.FromResult<IProductRuntime>(recoveredRuntime);
                form.Show();Application.DoEvents();form.HandleCommand("updates-page");form.Canvas.AvailableUpdate=update;int downloads=0;bool launched=false;
                var download=form.DownloadUpdateAsync((_,_)=>{downloads++;return Task.FromResult("fixture");});PumpUntil(()=>download.IsCompleted,"Pending-session download hung.");
                runtime.Publish(runtime.State with{LifecycleBlocked=true,PerformanceRecovery=new(true,"previous session",pending.CpuSession),Runtime="RecoveryRequired"});
                form.HandleCommand("updates-page");form.Canvas.Refresh();require(form.Canvas.Hits.Single(h=>h.Id=="recovery-run").Enabled,"Update page omitted pending-session recovery.");
                using(var bitmap=new Bitmap(form.Canvas.Width,form.Canvas.Height))
                {form.Canvas.DrawToBitmap(bitmap,new(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine("logs","product-gui-self-test","updates-downloaded-recovery.png"));}
                var install=form.InstallDownloadedUpdateAsync(()=>{},()=>launched=true,_=>Task.CompletedTask);PumpUntil(()=>install.IsCompleted,"Pending install hung.");
                require(!launched&&runtime.Disposals==0&&form.Canvas.DownloadedUpdate==update,"Prior-session block discarded download or destroyed the runtime.");
                var recovery=form.RecoverPerformanceAsync(true);PumpUntil(()=>recovery.IsCompleted,"Update recovery hung.");
                require(form.Canvas.DownloadedUpdate==update&&recoveredRuntime.Starts==1&&recoveredRuntime.Commands==0,"Recovery lost the installer or activated hardware.");
                install=form.InstallDownloadedUpdateAsync(()=>{},()=>launched=true,_=>Task.CompletedTask);PumpUntil(()=>install.IsCompleted,"Recovered install hung.");
                require(launched&&form.IsDisposed&&downloads==1&&runtime.Disposals==1&&recoveredRuntime.Disposals==1,"Recovered update re-downloaded or repeated release.");
            }
            TestUpdateRecovery(require);
            using(var form=new ProductForm("fixture://verify-install",fixture:new RecordingRuntime(),fixtureProfiles:new()))
            {
                form.Show();Application.DoEvents();form.Canvas.DownloadedUpdate=update;bool launched=false;
                var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var install=form.InstallDownloadedUpdateAsync(()=>{},()=>launched=true,_=>gate.Task);
                CompleteGuiTask(form.HandlePowerEventAsync(4),"Power event fixture hung.");CompleteGuiTask(form.HandlePowerEventAsync(18),"Power event fixture hung.");gate.SetResult();
                PumpUntil(()=>install.IsCompleted,"Verify/suspend hung.");require(!launched&&form.Canvas.DownloadedUpdate==update,"Suspend during verification auto-installed or lost the cache.");
                var corrupt=form.InstallDownloadedUpdateAsync(()=>{},()=>launched=true,_=>Task.FromException(new InvalidDataException("SHA-256 mismatch")));
                PumpUntil(()=>corrupt.IsCompleted,"Corrupt verification hung.");require(!launched&&form.Canvas.DownloadedUpdate is null,"Changed saved EXE was admitted.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Verify fixture exit hung.");
            }
            Console.WriteLine("Updates page: PASS (query-only, saved installer retained on offline checks, preview opt-in/stale-channel guards, separate download/install, no-update/offline/digest failures, duplicate/dirty guards, navigation, suspend during download and clean handoff; no network/hardware IO).");
        }
        finally{Environment.ExitCode=exitCode;if(File.Exists(preferencePath))File.Delete(preferencePath);if(Directory.Exists(Path.GetDirectoryName(preferencePath)!))Directory.Delete(Path.GetDirectoryName(preferencePath)!);}
    }
    private static void TestUpdateRecovery(Action<bool,string> require)
    {
        foreach(var scenario in new[]{"success","runner-failure","changed","invalid","suspend","close","verify-failure"})
        {
            Console.WriteLine("Automatic update recovery fixture: " + scenario);
            var selected=new ProductRecoverySelection(Guid.NewGuid(),Guid.Empty);
            if(scenario=="invalid") selected=selected with{Fans=[new("fixture-foreign","Unknown","hash","invalid ownership")]};
            var pending=selected; int reads=0,runs=0,factories=0,launches=0;
            var old=new RecordingRuntime();
            var recovery=new TaskCompletionSource<ProductRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var form=new ProductForm("fixture://update-recovery",fixture:old,fixtureProfiles:new(){RecoverSessionsAutomatically=true});
            form.RecoverySelectionReader=()=>{reads++;return scenario=="changed"&&reads>1?selected with{CpuSession=Guid.NewGuid()}:pending;};
            form.RecoveryRunner=s=>{runs++;require(old.Disposals==1&&s==selected,"Update recovery bypassed release or changed selection.");return recovery.Task;};
            form.RecoveryRuntimeFactory=_=>{factories++;throw new IOException("Update recovery must not reopen telemetry.");};
            form.Show();Application.DoEvents();
            old.Publish(old.State with{LifecycleBlocked=true,Runtime="RecoveryRequired",PerformanceRecovery=new(true,"pending",selected.CpuSession,FanRecords:selected.Fans)});
            var update=FutureUpdate(1024);form.Canvas.DownloadedUpdate=update;
            var install=form.InstallDownloadedUpdateAsync(()=>{},()=>launches++,
                _=>scenario=="verify-failure"?Task.FromException(new InvalidDataException("invalid installer")):Task.CompletedTask);
            // Verification/pending reads and the recovery coordinator all share one installation request.
            if(!install.IsCompleted)
            {
                var duplicate=form.InstallDownloadedUpdateAsync(()=>{},()=>launches++,_=>Task.CompletedTask);
                require(duplicate.IsCompleted,"A duplicate installation was not rejected immediately.");
                duplicate.GetAwaiter().GetResult();
                var clock=Environment.TickCount64;form.TryAutomaticRecovery(clock);form.TryAutomaticRecovery(clock+10000);
            }
            if(scenario is "invalid" or "verify-failure" or "changed")
            {
                PumpUntil(()=>install.IsCompleted,"Rejected automatic update recovery hung.");
                require(runs==0&&launches==0&&factories==0,"Invalid selection/installer or changed records reached recovery/launch.");
                require(old.Disposals==(scenario=="changed"?1:0),"Read/verification rejection shut down a usable runtime.");
                require((form.Canvas.DownloadedUpdate is null)==(scenario=="verify-failure"),"A rejected recovery lost the valid installer.");
            }
            else
            {
                PumpUntil(()=>runs==1,"Update did not recover its pending session automatically.");
                require(launches==0&&factories==0&&!install.IsCompleted,"Update passed an unfinished recovery or reopened telemetry.");
                Task? exit=null;
                if(scenario=="suspend")
                {CompleteGuiTask(form.HandlePowerEventAsync(4),"Power event fixture hung.");CompleteGuiTask(form.HandlePowerEventAsync(18),"Power event fixture hung.");}
                if(scenario=="close")
                {exit=form.RequestExitAsync();require(!exit.IsCompleted,"Close did not drain update recovery.");}
                if(scenario!="runner-failure")pending=new(Guid.Empty,Guid.Empty);
                recovery.SetResult(new(scenario!="runner-failure","fixture-evidence",scenario=="runner-failure"?"native completion unknown":"released"));
                PumpUntil(()=>install.IsCompleted,"Automatic update recovery never completed.");
                require(runs==1&&factories==0&&launches==(scenario=="success"?1:0)&&old.Disposals==1,"Update duplicated recovery, reacquired hardware or launched after an interruption/failure.");
                if(scenario!="success")require(form.Canvas.DownloadedUpdate==update,"Interrupted/failed recovery lost the downloaded installer.");
                if(exit is not null)PumpUntil(()=>exit.IsCompleted,"Exit failed to finish after update recovery.");
            }
            if(!form.IsDisposed){var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Update recovery fixture exit hung.");}
        }
        Console.WriteLine("Automatic update recovery: PASS (one confirmed install, verify before recovery, exact inventory, no controller reacquisition, failed release/invalid records/suspend/close fences and retained installer; zero hardware IO).");
    }
    private static void TestUpdateHandoff(Action<bool,string> require)
    {
        var exitCode=Environment.ExitCode;
        try
        {
            var runtime=new RecordingRuntime{DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            using(var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles()))
            {
                form.Show();Application.DoEvents();bool checkedRelease=false,launched=false;
                var install=form.InstallUpdateFixtureAsync(()=>{require(runtime.Disposals==1,"Release check preceded cleanup.");checkedRelease=true;},()=>{require(checkedRelease,"Installer opened before release check.");launched=true;});
                PumpUntil(()=>runtime.Disposals==1,"Update cleanup did not begin.");
                require(!launched&&!checkedRelease&&form.Canvas.Busy,"Installer opened before disposal completed.");
                form.HandleCommand("firmware");require(runtime.Commands==0,"Update cleanup admitted a hardware command.");
                runtime.DisposeGate.SetResult();PumpUntil(()=>install.IsCompleted,"Update handoff hung.");install.GetAwaiter().GetResult();
                require(launched&&form.IsDisposed&&runtime.Disposals==1,"Clean update did not close exactly once.");
            }
            foreach(var failure in new[]{"fence","cleanup","journal","launch","suspend"})
            {
                runtime=new RecordingRuntime{FenceFailure=failure=="fence"?"fence failed":null,DisposeFailure=failure=="cleanup"?"unresolved release":null,DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
                using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles());
                form.Show();Application.DoEvents();bool launched=false;
                var install=form.InstallUpdateFixtureAsync(()=>{if(failure=="journal")throw new IOException("retained journal");},()=>{if(failure=="launch")throw new IOException("UAC cancelled");launched=true;});
                PumpUntil(()=>runtime.Disposals==1,"Failed update cleanup did not begin.");
                if(failure=="suspend")CompleteGuiTask(form.HandlePowerEventAsync(4),"Power event fixture hung.");
                runtime.DisposeGate.SetResult();PumpUntil(()=>install.IsCompleted,"Failed update handoff hung.");
                require(!form.Canvas.State.PerformanceActive&&!form.Canvas.State.PerformanceProcessPresent&&!form.Canvas.State.AutomaticPreparing,"Failed update retained active presentation.");
                require(install.IsFaulted&&!launched&&!form.IsDisposed&&form.Canvas.State.LifecycleBlocked,"Failed release/journal/UAC/lifecycle opened installer or reused runtime.");
                require(form.RecoveryMonitorActive,"Failed installation stopped background monitoring permanently.");
                form.HandleCommand("performance-apply");require(runtime.Commands==0,"Failed update reused disposed runtime.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed update could not exit.");
                require(runtime.Disposals==1,"Failed update repeated cleanup on exit.");
            }
            Console.WriteLine("Product update handoff: PASS (cleanup drain, journal/UAC/suspend failure fences, normal close and single disposal; no hardware IO).");
        }
        finally{Environment.ExitCode=exitCode;}
    }
}
