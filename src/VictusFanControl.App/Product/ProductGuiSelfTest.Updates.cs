using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
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
