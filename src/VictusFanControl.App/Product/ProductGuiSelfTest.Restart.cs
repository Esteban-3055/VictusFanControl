using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestSessionRestart(Action<bool,string> require)
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-session-restart-"+Guid.NewGuid().ToString("N"));
        var directory=Path.Combine(root,"sessions",AppLog.SessionId);Directory.CreateDirectory(directory);
        var oldExitCode=Environment.ExitCode;
        try
        {
            var preferences=Path.Combine(root,"preferences.json");var baseline=new ProductProfiles();ProductProfilesStore.Save(baseline,preferences);
            var original=File.ReadAllText(preferences);
            var runtime=new RecordingRuntime{ManualGate=new(TaskCreationOptions.RunContinuationsAsynchronously),DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            using(var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:baseline,profilesPath:preferences,restartDirectory:directory,automaticReview:ProductAutomaticReviewMode.Extended))
            {
                form.Show();Application.DoEvents();form.EditValue("pl1",38);form.Canvas.Page=ProductPage.Settings;
                var draft=ProductProfilesStore.Serialize(form.Draft);
                runtime.Publish(runtime.State with{ManualAuthorized=true,Runtime="Healthy",FanMode="Manual",LifecycleBlocked=false});
                form.HandleCommand("fan-mode-1");require(runtime.Commands==1&&form.Canvas.Busy,"Pending restart fixture command did not start.");
                form.Canvas.Refresh();require(form.Canvas.Hits.Single(h=>h.Id=="session-restart").Enabled,"Restart button was disabled by a pending command.");
                var restart=form.RestartSessionAsync();require(runtime.Fences==1&&form.Canvas.Busy,"Restart did not fence immediately.");
                require(ReferenceEquals(restart,form.RestartSessionAsync()),"Double restart created another shutdown.");
                form.HandleCommand("firmware");form.HandleCommand("performance-apply");require(runtime.Commands==1,"Restart admitted another hardware command.");
                PumpUntil(()=>File.Exists(Path.Combine(directory,"diagnostic-before-restart.zip")),"Pre-restart diagnostic was not retained.");
                require(runtime.Disposals==0&&!restart.IsCompleted,"Restart disposed before the pending command drained.");
                runtime.ManualGate.SetResult();PumpUntil(()=>runtime.Disposals==1,"Restart cleanup did not start.");
                require(!File.Exists(Path.Combine(directory,ProductSessionRestart.StateFileName))&&!restart.IsCompleted,"Restart receipt was written before cleanup completed.");
                runtime.DisposeGate.SetResult();PumpUntil(()=>restart.IsCompleted,"Restart did not finish.");restart.GetAwaiter().GetResult();
                require(form.IsDisposed&&form.RestartRequest?.Review==ProductAutomaticReviewMode.Extended&&runtime.Disposals==1,"Clean restart failed or duplicated cleanup.");
                require(File.ReadAllText(preferences)==original,"Restart silently saved the draft to preferences.");
                var state=ProductSessionRestart.ReadReleasedState(form.RestartRequest!.StatePath,Path.Combine(root,"sessions"));
                require(state.DraftJson==draft&&state.SavedJson==ProductProfilesStore.Serialize(baseline)&&state.Dirty,"Restart lost the unsaved draft or saved baseline.");
                using var zip=ZipFile.OpenRead(Path.Combine(directory,"diagnostic-before-restart.zip"));
                require(zip.GetEntry("gui-state.json") is not null&&zip.GetEntry("profiles-draft.json") is not null,"Automatic pre-restart evidence missing.");
                using(var next=new ProductForm("fixture://modules",fixture:new RecordingRuntime(),fixtureProfiles:baseline,restartState:state))
                {
                    next.Show();Application.DoEvents();require(next.Visible&&next.Dirty&&ProductProfilesStore.Serialize(next.Draft)==draft&&next.Canvas.State.FanMode=="Firmware"&&!next.Canvas.State.PerformanceActive,"Restored draft acquired hardware authority or was hidden.");
                    next.HandleCommand("discard");require(ProductProfilesStore.Serialize(next.Draft)==state.SavedJson,"Discard after restart lost the original saved baseline.");
                    var exit=next.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Restored fixture exit failed.");
                }
                // Launch contract includes only the current binary, module path, receipt and explicit review mode.
                foreach(var mode in new ProductAutomaticReviewMode?[]{null,ProductAutomaticReviewMode.Short,ProductAutomaticReviewMode.Extended})
                {
                    var request=form.RestartRequest with{Review=mode,Modules="C:\\modules with spaces\\$literal"};
                    var info=ProductSessionRestart.CreateStartInfo(request,"C:\\app with spaces\\VictusFanControl.App.exe","unused.dll");
                    require(info.ArgumentList.Count==(mode is null?4:5)&&info.ArgumentList[1]==request.Modules&&info.ArgumentList[3]==request.StatePath&&!info.UseShellExecute,"Restart altered literal paths or copied unwanted flags.");
                }
                TestFreshRestartProcess(state,form.RestartRequest.StatePath,root,require);
                var invalid=state with{ReleaseCompleted=false};var invalidPath=Path.Combine(directory,"invalid.json");File.WriteAllText(invalidPath,JsonSerializer.Serialize(invalid));
                try{ProductSessionRestart.ReadReleasedState(invalidPath,Path.Combine(root,"sessions"));throw new InvalidOperationException("Invalid receipt accepted.");}catch(InvalidDataException){}
                var bytes=File.ReadAllText(form.RestartRequest.StatePath);File.WriteAllText(form.RestartRequest.StatePath,JsonSerializer.Serialize(invalid));
                try{ProductSessionRestart.ReadReleasedState(form.RestartRequest.StatePath,Path.Combine(root,"sessions"));throw new InvalidOperationException("Incomplete release accepted.");}catch(InvalidDataException){}
                File.WriteAllText(form.RestartRequest.StatePath,bytes);
            }
            // A retained recovery record is a blocker, never a file to delete or overwrite.
            var journal=Path.Combine(root,"retained-journal.json");File.WriteAllText(journal,"original");
            try{ProductSessionRestart.EnsureAbsent(new[]{journal});throw new InvalidOperationException("Retained journal accepted.");}catch(IOException){}
            require(File.ReadAllText(journal)=="original","Restart changed a recovery record.");
            ProductSessionRestart.EnsureAbsent(new[]{Path.Combine(root,"absent.json")});
            TestFailedRestart(Path.Combine(root,"failure"),require);
            TestRestartDiagnosticFailure(Path.Combine(root,"disk-failure"),require);
            TestRestartStartupAndExit(Path.Combine(root,"startup"),require);
            TestRestartLifecycle(Path.Combine(root,"lifecycle"),require);
            Console.WriteLine("PASS: session restart drains commands, disposes once, retains draft/baseline and diagnostic, opens a fresh Firmware process, rejects incomplete release and lifecycle races; zero hardware IO.");
        }
        finally{Environment.ExitCode=oldExitCode;Directory.Delete(root,true);}
    }
    private static void TestFailedRestart(string directory,Action<bool,string> require)
    {
        var runtime=new RecordingRuntime{DisposeFailure="fixture GPU reset unresolved"};
        using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles(),restartDirectory:directory);
        form.Show();Application.DoEvents();var restart=form.RestartSessionAsync();PumpUntil(()=>restart.IsCompleted,"Failed restart hung.");
        require(form.RestartRequest is null&&!form.IsDisposed&&!form.Canvas.RestartAvailable&&form.Canvas.State.LifecycleBlocked&&form.Canvas.Notice.Contains("GPU reset unresolved"),"Failed cleanup reopened a process or hid its reason.");
        require(!File.Exists(Path.Combine(directory,ProductSessionRestart.StateFileName))&&File.Exists(Path.Combine(directory,"diagnostic-before-restart.zip")),"Failed cleanup fabricated a release receipt or lost evidence.");
        form.RestartSessionAsync().GetAwaiter().GetResult();form.HandleCommand("fan-mode-2");form.HandleCommand("performance-apply");
        require(runtime.Disposals==1&&runtime.Commands==0,"Failed runtime was reused or blindly released again.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed restart could not exit.");require(runtime.Disposals==1,"Exit repeated a failed restart release.");
    }
    private static void TestRestartStartupAndExit(string directory,Action<bool,string> require)
    {
        var ready=new TaskCompletionSource<IProductRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://modules",fixtureProfiles:new ProductProfiles(),runtimeFactory:()=>ready.Task,restartDirectory:directory);
        form.Show();Application.DoEvents();var restart=form.RestartSessionAsync();require(!restart.IsCompleted,"Restart abandoned runtime construction.");
        var exit=form.RequestExitAsync();require(!exit.IsCompleted,"Exit abandoned restart cleanup.");ready.SetResult(runtime);
        PumpUntil(()=>exit.IsCompleted&&restart.IsCompleted,"Startup/restart/exit race hung.");
        require(runtime.Starts==0&&runtime.Disposals==1&&form.RestartRequest is null&&!File.Exists(Path.Combine(directory,ProductSessionRestart.StateFileName)),"Exit race launched a child, started hardware or duplicated disposal.");
    }
    private static void TestRestartDiagnosticFailure(string path,Action<bool,string> require)
    {
        File.WriteAllText(path,"directory blocked by fixture");var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles(),restartDirectory:path);
        form.Show();Application.DoEvents();var restart=form.RestartSessionAsync();PumpUntil(()=>restart.IsCompleted,"Diagnostic failure skipped or stalled cleanup.");
        require(runtime.Disposals==1&&form.RestartRequest is null&&form.Canvas.State.LifecycleBlocked&&File.ReadAllText(path)=="directory blocked by fixture","Diagnostic failure abandoned cleanup, overwrote the blocker or launched a process.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Diagnostic failure could not exit.");require(runtime.Disposals==1,"Diagnostic failure cleanup repeated on exit.");
    }
    private static void TestRestartLifecycle(string directory,Action<bool,string> require)
    {
        var runtime=new RecordingRuntime{DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles(),restartDirectory:directory);
        form.Show();Application.DoEvents();var restart=form.RestartSessionAsync();PumpUntil(()=>runtime.Disposals==1,"Lifecycle restart cleanup did not start.");
        form.HandlePowerEventAsync(4).GetAwaiter().GetResult();form.HandlePowerEventAsync(18).GetAwaiter().GetResult();runtime.DisposeGate.SetResult();
        PumpUntil(()=>restart.IsCompleted,"Lifecycle restart did not finish.");
        require(form.RestartRequest is null&&runtime.Disposals==1&&runtime.Releases==0&&runtime.Resumes==0&&!form.Canvas.RestartAvailable,"Lifecycle raced a disposed runtime or automatically reopened control.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Lifecycle fixture exit failed.");
    }
    private static void TestFreshRestartProcess(ProductRestartState state,string path,string root,Action<bool,string> require)
    {
        var output=Path.Combine(root,"fresh-process.json");
        var info=ProductSessionRestart.CreateStartInfo(new(path,"fixture://modules",null),Environment.ProcessPath!,typeof(ProductGuiSelfTest).Assembly.Location);
        var host=Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?info.ArgumentList[0]:null;
        info.ArgumentList.Clear();if(host is not null)info.ArgumentList.Add(host);
        info.ArgumentList.Add("--product-restart-fixture-child");info.ArgumentList.Add(path);info.ArgumentList.Add(output);
        using var child=Process.Start(info)??throw new IOException("Restart fixture child did not launch.");
        PumpUntil(()=>child.HasExited,"Restart fixture child did not finish.");
        require(child.ExitCode==0&&File.Exists(output),"Restart fixture child failed.");
        using var observed=JsonDocument.Parse(File.ReadAllText(output));var result=observed.RootElement;
        require(result.GetProperty("sessionId").GetString()!=state.PreviousSessionId&&result.GetProperty("commands").GetInt32()==0&&result.GetProperty("fanMode").GetString()=="Firmware"&&result.GetProperty("draftJson").GetString()==state.DraftJson,"New process reused the old session, lost the draft or acquired authority.");
    }
    internal static int RunRestartFixtureChild(string statePath,string output)
    {
        try
        {
            var root=Path.GetDirectoryName(Path.GetDirectoryName(statePath))!;var state=ProductSessionRestart.ReadReleasedState(statePath,root);
            var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles(),restartState:state);
            form.Show();Application.DoEvents();File.WriteAllText(output,JsonSerializer.Serialize(new{sessionId=AppLog.SessionId,commands=runtime.Commands,fanMode=form.Canvas.State.FanMode,draftJson=ProductProfilesStore.Serialize(form.Draft)}));
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Fixture child exit failed.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
