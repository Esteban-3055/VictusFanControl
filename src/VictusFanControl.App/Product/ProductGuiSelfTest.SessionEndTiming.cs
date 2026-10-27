using System.Diagnostics;
using System.Runtime.InteropServices;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    [DllImport("user32.dll",EntryPoint="SendMessageW")]
    private static extern IntPtr SendTimingMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",EntryPoint="ShutdownBlockReasonCreate",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateTimingBlockReason(IntPtr window,string reason);
    [DllImport("user32.dll",EntryPoint="ShutdownBlockReasonDestroy",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyTimingBlockReason(IntPtr window);

    // Explicitly separate from fast regression tests: uses 20 real seconds and
    // actual child/IPC clients, but all hardware domains are fixture-only.
    internal static int RunSessionEndTimingChecks()
    {
        var originalExitCode=Environment.ExitCode;
        var observers=new List<Mutex>();
        Process? performanceProcess=null;
        Process? performanceObserver=null;
        var fan=new WmiFanGuiGuardianClient(fixture:true,productUpperRange:true);
        var performance=new PerformanceGuardianClient(Path.GetTempPath(),start=>
        {
            start.ArgumentList[0]="--gui-fixture-session";
            var nonce=Guid.Parse(start.ArgumentList[start.ArgumentList.IndexOf("--nonce")+1]);
            observers.Add(new Mutex(false,"Local\\VFC.GuiFixture."+nonce.ToString("N")));
            performanceProcess=Process.Start(start);
            if(performanceProcess is not null)
            {
                performanceObserver=Process.GetProcessById(performanceProcess.Id);
                _=performanceObserver.Handle; // Retain a separate exit witness after client disposal.
            }
            return performanceProcess;
        },recoveryPreview:()=>new(false,"Timing test; isolated fixture inventory."));
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);}
        try
        {
            fan.StartAsync(default).GetAwaiter().GetResult();
            var enabled=performance.EnableAsync(new(){AcPl1Watts=20,AcPl2Watts=40}).GetAwaiter().GetResult();
            Require(enabled.Ok&&performance.LimitsActive,"Fixture performance domains not ready.");
            long releaseAt=-1,targetAt=-1,confirmedAt=-1;
            int statuses=0;
            bool releaseConfirmed=false;
            var timer=new Stopwatch();
            var runtime=new RecordingRuntime{StateOverride=new(){Target=Hp8C40TargetProfile.Instance.Id,
                Runtime="Healthy",Source="Ac",ManualAuthorized=true,FanMode="Automatic",FanAuthority="Custom"}};
            runtime.ManualCommandFixture=level=>
            {
                FanDispatchAdmissionScope.EnsureNativeRequestAllowed(Hp8C40ProductFanTargets.BuildRequest(level));
                fan.EnsureAlive();fan.PersistIntent(level);targetAt=timer.ElapsedMilliseconds;
                runtime.StateOverride=runtime.State with{FanMode="Manual",FanAuthority="Custom",ManualAppliedLevel=level};
                return Task.CompletedTask;
            };
            runtime.DisposeFixture=async()=>
            {
                releaseAt=timer.ElapsedMilliseconds;
                Require(releaseAt>=20_000,"Release started before the real cooling interval ended.");
                try
                {
                    var receipt=await fan.ReleaseAsync(default);
                    Require(receipt is{ReleaseRequestAccepted:true,LegacyDefaultRequestAccepted:true,
                        GuardianLeaseRetired:true,IndependentFirmwareOwnershipVerified:false},"Fan fixture release receipt invalid.");
                }
                finally{await performance.CloseAsync();} // Both domains must be attempted.
                Require(!performance.HasProcess,"Performance IPC still owns a process after release.");
                Require(performanceObserver is{HasExited:true,ExitCode:0},"Performance child exit not confirmed.");
                // Exercise a delayed cleanup acknowledgement through the STA.
                await Task.Delay(1500);
                confirmedAt=timer.ElapsedMilliseconds;releaseConfirmed=true;
            };
            using var form=new ProductForm("fixture://real-time-session-end",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();
            Require(CreateTimingBlockReason(form.Handle,ProductShutdownCooling.CoolingReason),
                "Native Windows shutdown reason registration failed: "+Marshal.GetLastWin32Error());
            Require(DestroyTimingBlockReason(form.Handle),"Native Windows shutdown reason removal failed.");
            long lastStatus=-1000;
            form.SetSessionCoolingFixture(()=>PerformancePowerSourceKind.Ac,()=>Environment.TickCount64,()=>
            {
                fan.Heartbeat();fan.EnsureAlive();
                Require(runtime.Disposals==0&&!releaseConfirmed,"Dependencies released during cooling.");
                if(timer.ElapsedMilliseconds-lastStatus>=1000)
                {
                    var status=performance.StatusAsync().GetAwaiter().GetResult();
                    Require(status?.Ok==true&&performance.LimitsActive,"Performance child lost during cooling.");
                    statuses++;lastStatus=timer.ElapsedMilliseconds;
                }
                Thread.Sleep(25);
            });
            timer.Start();
            // Native messages target only this owned fixture window. No broadcast,
            // ExitWindowsEx, shutdown command, or installed-program handle is used.
            var query=SendTimingMessage(form.Handle,0x11,IntPtr.Zero,IntPtr.Zero);
            var cooledAt=timer.ElapsedMilliseconds;
            Require(query==(IntPtr)1&&cooledAt>=20_000&&cooledAt<30_000,"Native query did not hold 20 real seconds.");
            Require(targetAt>=0&&targetAt<2000&&runtime.Disposals==0&&statuses>=10,"Target or dependency monitoring incomplete.");
            var ended=SendTimingMessage(form.Handle,0x16,(IntPtr)1,IntPtr.Zero);
            Require(ended==IntPtr.Zero&&runtime.Disposals==1&&releaseConfirmed&&confirmedAt-releaseAt>=1500,
                "WM_ENDSESSION returned before confirmed fixture cleanup.");
            Require(Environment.ExitCode==originalExitCode,"Normal timing test left an unresolved-cleanup exit code.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Timing fixture exit hung.");
            Require(runtime.Disposals==1,"Repeated exit disposed the same owner again.");
            Console.WriteLine($"PASS real-time session end: targetAtMs={targetAt}; coolingMs={cooledAt}; releaseAtMs={releaseAt}; confirmedAtMs={confirmedAt}; statusChecks={statuses}; native reason registration/removal and window messages; both real fixture children/IPC and release receipts; hardware IO=0; OS shutdown=0; physical RPM unverified.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally
        {
            try{fan.ReleaseAsync(default).GetAwaiter().GetResult();}catch(Exception error){Console.Error.WriteLine("Timing fixture fan cleanup: "+error.Message);}
            fan.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try{performance.CloseAsync().GetAwaiter().GetResult();}catch(Exception error){Console.Error.WriteLine("Timing fixture performance cleanup: "+error.Message);}
            foreach(var observer in observers)observer.Dispose();
            performanceObserver?.Dispose();performanceProcess?.Dispose();Environment.ExitCode=originalExitCode;
        }
    }
}
