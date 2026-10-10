using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestUpdatePage(Action<bool,string> require)
    {
        var exitCode=Environment.ExitCode;
        var preferencePath=Path.Combine(Path.GetTempPath(),"Victus-update-fixture-"+Guid.NewGuid().ToString("N"),"profiles.json");
        var update=new ProductUpdate(new Version(1,2,0),new Uri("https://github.com/Esteban-3055/VictusFanControl/releases/download/v1.2.0/VictusFanControl-1.2.0-Setup-win-x64.exe"),1024,new string('a',64));
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
                form.HandleCommand("updates-page");form.Canvas.Refresh();require(form.Canvas.Hits.Single(h=>h.Id=="updates-install").Enabled,"Available update install button disabled.");
                form.HandleCommand("updates-prerelease-toggle");form.Canvas.Refresh();
                require(form.Draft.IncludePrereleaseUpdates&&form.Canvas.Dirty&&form.Canvas.AvailableUpdate is null&&runtime.Commands==0,"Channel toggle did not invalidate result or touched hardware.");
                var channelGate=new TaskCompletionSource<ProductUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var channelCheck=form.CheckUpdateAsync(_=>channelGate.Task);
                form.HandleCommand("updates-prerelease-toggle");require(form.Draft.IncludePrereleaseUpdates,"Busy channel toggle was admitted.");
                form.HandleCommand("discard");channelGate.SetResult(update with{IsPrerelease=true});PumpUntil(()=>channelCheck.IsCompleted,"Channel query hung.");
                require(!form.Draft.IncludePrereleaseUpdates&&form.Canvas.AvailableUpdate is null&&!form.Canvas.UpdateBusy,"Query from discarded preview channel was published.");
                form.Canvas.AvailableUpdate=update with{IsPrerelease=true};int blockedPreviewDownloads=0;
                form.DownloadAndInstallUpdateAsync((_,_)=>{blockedPreviewDownloads++;return Task.FromResult("fixture");}).GetAwaiter().GetResult();
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
                int downloads=0;form.DownloadAndInstallUpdateAsync((_,_)=>{downloads++;return Task.FromResult("fixture");}).GetAwaiter().GetResult();require(downloads==0,"Dirty draft downloaded installer.");
                form.HandleCommand("discard");
                var failed=form.CheckUpdateAsync(_=>Task.FromException<ProductUpdate?>(new IOException("offline")));PumpUntil(()=>failed.IsCompleted,"Failed check hung.");
                require(form.Canvas.AvailableUpdate is null&&form.Canvas.UpdateStatus.Contains("offline")&&!form.Canvas.UpdateBusy,"Failed check retained stale update/admission.");
                var latest=form.CheckUpdateAsync(_=>Task.FromResult<ProductUpdate?>(null));PumpUntil(()=>latest.IsCompleted,"No-update check hung.");require(form.Canvas.AvailableUpdate is null&&form.Canvas.UpdateStatus.Contains("No hay"),"Latest-version status missing.");
                form.Canvas.AvailableUpdate=update;
                var bad=form.DownloadAndInstallUpdateAsync((_,_)=>Task.FromException<string>(new InvalidDataException("SHA-256 mismatch")));
                PumpUntil(()=>bad.IsCompleted,"Bad download hung.");require(!form.IsDisposed&&runtime.Disposals==0&&form.Canvas.UpdateStatus.Contains("SHA-256")&&!form.Canvas.UpdateBusy,"Bad download released runtime or lost error.");
                var downloadGate=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);bool launched=false;
                var interrupted=form.DownloadAndInstallUpdateAsync((_,_)=>downloadGate.Task,()=>{},()=>launched=true);
                form.HandlePowerEventAsync(4).GetAwaiter().GetResult();form.HandlePowerEventAsync(18).GetAwaiter().GetResult();
                downloadGate.SetResult("fixture");PumpUntil(()=>interrupted.IsCompleted,"Interrupted download hung.");
                require(!launched&&runtime.Disposals==0&&!form.IsDisposed,"Suspend during download launched installer after resume.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Update page could not exit.");
            }
            runtime=new RecordingRuntime();
            using(var form=new ProductForm("fixture://updates",fixture:runtime,fixtureProfiles:new ProductProfiles{IncludePrereleaseUpdates=true}))
            {
                form.Show();Application.DoEvents();form.Canvas.AvailableUpdate=update with{IsPrerelease=true};bool released=false,launched=false;
                var install=form.DownloadAndInstallUpdateAsync((_,_)=>Task.FromResult("fixture"),()=>{released=true;require(runtime.Disposals==1,"Download skipped cleanup.");},()=>{require(released,"Download launch preceded release.");launched=true;});
                PumpUntil(()=>install.IsCompleted,"Download/install hung.");require(launched&&form.IsDisposed,"Page download did not hand off to installer.");
            }
            Console.WriteLine("Updates page: PASS (query-only, preview opt-in/stale-channel guards, separate preview install, no-update/offline/digest failures, duplicate/dirty guards, navigation, suspend during download and clean handoff; no network/hardware IO).");
        }
        finally{Environment.ExitCode=exitCode;if(File.Exists(preferencePath))File.Delete(preferencePath);if(Directory.Exists(Path.GetDirectoryName(preferencePath)!))Directory.Delete(Path.GetDirectoryName(preferencePath)!);}
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
            foreach(var failure in new[]{"cleanup","journal","launch","suspend"})
            {
                runtime=new RecordingRuntime{DisposeFailure=failure=="cleanup"?"unresolved release":null,DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
                using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles());
                form.Show();Application.DoEvents();bool launched=false;
                var install=form.InstallUpdateFixtureAsync(()=>{if(failure=="journal")throw new IOException("retained journal");},()=>{if(failure=="launch")throw new IOException("UAC cancelled");launched=true;});
                PumpUntil(()=>runtime.Disposals==1,"Failed update cleanup did not begin.");
                if(failure=="suspend")form.HandlePowerEventAsync(4).GetAwaiter().GetResult();
                runtime.DisposeGate.SetResult();PumpUntil(()=>install.IsCompleted,"Failed update handoff hung.");
                require(install.IsFaulted&&!launched&&!form.IsDisposed&&form.Canvas.State.LifecycleBlocked,"Failed release/journal/UAC/lifecycle opened installer or reused runtime.");
                form.HandleCommand("performance-apply");require(runtime.Commands==0,"Failed update reused disposed runtime.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed update could not exit.");
                require(runtime.Disposals==1,"Failed update repeated cleanup on exit.");
            }
            Console.WriteLine("Product update handoff: PASS (cleanup drain, journal/UAC/suspend failure fences, normal close and single disposal; no hardware IO).");
        }
        finally{Environment.ExitCode=exitCode;}
    }
}
