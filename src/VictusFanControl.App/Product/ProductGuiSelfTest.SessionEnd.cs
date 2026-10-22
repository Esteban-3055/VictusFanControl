namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestWindowsSessionEnd(Action<bool,string> require)
    {
        var originalExitCode=Environment.ExitCode;
        try
        {
            var runtime=new RecordingRuntime { DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using(var form=new ProductForm("fixture://windows-session-end",fixture:runtime,fixtureProfiles:new()))
            {
                form.Show();Application.DoEvents();
                // Send only to our isolated form. No Windows logoff/shutdown API is called.
                var query=Message.Create(form.Handle,0x11,IntPtr.Zero,IntPtr.Zero);
                form.DispatchSessionEndFixture(ref query);
                var cancelled=Message.Create(form.Handle,0x16,IntPtr.Zero,IntPtr.Zero);
                form.DispatchSessionEndFixture(ref cancelled);
                require(query.Result==(IntPtr)1&&runtime.Disposals==0&&runtime.Fences==0,"Cancelled Windows shutdown released the active runtime.");
                require(!form.WaitForWindowsSessionEnd(TimeSpan.FromMilliseconds(40)),"Pending native owner was called drained.");
                require(runtime.Disposals==1&&!form.IsDisposed,"Session-end deadline killed/disposed the app or repeated cleanup.");
                form.HandleCommand("performance-apply");require(runtime.Commands==0,"Session-end wait allowed new commands.");
                // Complete through the UI queue: waiting must pump STA continuations.
                form.BeginInvoke(()=>runtime.DisposeGate.SetResult());
                require(form.WaitForWindowsSessionEnd(TimeSpan.FromSeconds(5))&&runtime.Disposals==1,"Confirmed session-end failed to drain a UI continuation exactly once.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Session-end fixture exit hung.");
            }
            runtime=new RecordingRuntime { DisposeFailure="Unconfirmed release" };
            using(var form=new ProductForm("fixture://windows-session-end-failure",fixture:runtime,fixtureProfiles:new()))
            {
                form.Show();Application.DoEvents();
                var ended=Message.Create(form.Handle,0x16,(IntPtr)1,IntPtr.Zero);
                form.DispatchSessionEndFixture(ref ended);
                require(runtime.Disposals==1&&Environment.ExitCode==171,"Windows session-end hid unresolved release or skipped cleanup.");
                require(runtime.CompletedRecoveries==0,"Windows shutdown fabricated hardware recovery.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed session-end fixture exit hung.");
            }
            Console.WriteLine("PASS Windows session end: cancelled query preserves session; confirmed end waits/pumps STA; bounded wait retains unresolved state; one disposal; no OS shutdown or hardware IO.");
        }
        finally { Environment.ExitCode=originalExitCode; }
    }
}
