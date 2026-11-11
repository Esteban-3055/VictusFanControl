using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Performance;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunSessionEndChecks()
    {
        try{TestWindowsSessionEnd((ok,detail)=>{if(!ok)throw new InvalidOperationException(detail);});return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void TestWindowsSessionEnd(Action<bool,string> require)
    {
        var originalExitCode=Environment.ExitCode;
        try
        {
            TestShutdownOrder(require);
            TestShutdownCooling(require);
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
    private static void TestShutdownOrder(Action<bool,string> require)
    {
        var logs=new List<string>();
        var original=VictusFanControl.Scheduling.ProductShutdownOrder.Read();
        try
        {
            require(VictusFanControl.Scheduling.ProductShutdownOrder.ApplicationLevel>
                VictusFanControl.Scheduling.ProductShutdownOrder.GuardianLevel,"GUI must receive session end before its guardians.");
            foreach(bool guardian in new[]{false,true})
                require(VictusFanControl.Scheduling.ProductShutdownOrder.Apply(guardian,logs.Add),"Native shutdown order readback failed in isolated process.");
        }
        finally{VictusFanControl.Scheduling.ProductShutdownOrder.Restore(original.Level,original.Flags);}
        require(VictusFanControl.Scheduling.ProductShutdownOrder.Read()==original,"Isolated process shutdown order was not restored.");
        require(!VictusFanControl.Scheduling.ProductShutdownOrder.Apply(false,logs.Add,(_,_)=>false),"Failed registration was reported confirmed.");
        require(!VictusFanControl.Scheduling.ProductShutdownOrder.Apply(true,logs.Add,(level,flags)=>
        {
            require(level==0x100&&flags==0,"Guardian changed reserved level or enabled no-retry termination.");return true;
        },()=> (0x280u,0u)),"Mismatched readback was reported confirmed.");
        Console.WriteLine("PASS shutdown order: GUI before both production guardians; native set/readback/restoration; failure and mismatch diagnostics; flags zero; no OS shutdown or hardware IO.");
    }
    private static void TestShutdownCooling(Action<bool,string> require)
    {
        static ProductRuntimeState Ready()=>new(){Target=Hp8C40TargetProfile.Instance.Id,Runtime="Healthy",Source="Ac",ManualAuthorized=true,FanAuthority="Firmware"};
        static void Send(ProductForm form,int kind,bool ended=false,long flags=0)
        {var message=Message.Create(form.Handle,kind,ended?(IntPtr)1:IntPtr.Zero,(IntPtr)flags);form.DispatchSessionEndFixture(ref message);}
        static void Exit(ProductForm form){var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Cooling fixture exit did not drain.");}
        static void Accept(RecordingRuntime runtime,int level)
        {
            // This is the same guard called at the production HP native setter.
            FanDispatchAdmissionScope.EnsureNativeRequestAllowed(Hp8C40ProductFanTargets.BuildRequest(level));
            runtime.StateOverride=runtime.State with{FanMode="Manual",FanAuthority="Custom",ManualAppliedLevel=level};
        }
        foreach(var source in new[]{PerformancePowerSourceKind.Battery,PerformancePowerSourceKind.Unknown})
        {
            var runtime=new RecordingRuntime{StateOverride=Ready()};long clock=0;
            using var form=new ProductForm("fixture://cooling-skip-source",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();form.Canvas.Editing=ProductPowerProfile.Ac;
            form.SetSessionCoolingFixture(()=>source,()=>clock,()=>clock+=1000);
            Send(form,0x11);require(runtime.Commands==0&&clock==0,"Battery/unknown used the AC editor tab as power authority.");
            Send(form,0x16,true);require(runtime.Disposals==1,"Battery/unknown skipped normal shutdown release.");Exit(form);
        }
        foreach(var flags in new[]{1L,0x80000000L,0x40000000L,0x40000001L})
        {
            var runtime=new RecordingRuntime{StateOverride=Ready()};long clock=0;
            using var form=new ProductForm("fixture://cooling-forced-logoff-servicing",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();form.SetSessionCoolingFixture(()=>PerformancePowerSourceKind.Ac,()=>clock,()=>clock+=1000);
            Send(form,0x11,flags:flags);require(runtime.Commands==0&&clock==0,"Forced/logoff/servicing query started optional fan writes.");
            Send(form,0x16);require(runtime.Disposals==0,"Cancelled non-cooling query released runtime.");Exit(form);
        }
        foreach(var state in new[]{Ready() with{LifecycleBlocked=true},Ready() with{Target="Unsupported"},Ready() with{ManualAuthorized=false},
            Ready() with{Runtime="Initializing"},Ready() with{AutomaticPreparing=true},Ready() with{FanAuthority="Faulted"}})
        {
            var runtime=new RecordingRuntime{StateOverride=state};long clock=0;
            using var form=new ProductForm("fixture://cooling-closed-admission",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();form.SetSessionCoolingFixture(()=>PerformancePowerSourceKind.Ac,()=>clock,()=>clock+=1000);
            Send(form,0x11);require(runtime.Commands==0&&clock==0,"Shutdown cooling reopened unsafe/unsupported/initializing authority.");Exit(form);
        }
        {
            var runtime=new RecordingRuntime{StateOverride=Ready()};runtime.ManualCommandFixture=level=>{Accept(runtime,level);return Task.CompletedTask;};
            long clock=0;bool duplicate=false;
            using var form=new ProductForm("fixture://cooling-full-phase",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();form.Canvas.Editing=ProductPowerProfile.Battery;
            var draft=ProductProfilesStore.Serialize(form.Draft);
            form.SetSessionCoolingFixture(()=>PerformancePowerSourceKind.Ac,()=>clock,()=>
            {
                require(runtime.Disposals==0&&runtime.Fences==0,"Releasing resources before the cooling interval ended.");
                form.HandleCommand("performance-apply");form.PresentationTick();
                if(!duplicate){duplicate=true;Send(form,0x11);}clock+=1000;
            });
            Send(form,0x11);require(clock==20_000&&runtime.Commands==2&&runtime.LastManualLevel==50,"AC cooling did not hold its separate 20s budget and one 50/50 request.");
            Send(form,0x11);require(clock==20_000&&runtime.Commands==2,"Repeated query repeated cooling or extended the delay.");
            require(ProductProfilesStore.Serialize(form.Draft)==draft&&runtime.Disposals==0,"Cooling changed saved presets or released before confirmation.");
            Send(form,0x16,true);require(runtime.Disposals==1,"AC cooling failed to run normal release afterwards.");Exit(form);
        }
        foreach(var afterAdmission in new[]{PerformancePowerSourceKind.Battery,PerformancePowerSourceKind.Unknown})
        {
            var runtime=new RecordingRuntime{StateOverride=Ready()};long clock=0;var source=PerformancePowerSourceKind.Ac;
            runtime.ManualCommandFixture=level=>{Accept(runtime,level);return Task.CompletedTask;};
            using var form=new ProductForm("fixture://cooling-continue-source-change",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();
            form.SetSessionCoolingFixture(()=>source,()=>clock,()=>
            {
                require(runtime.Disposals==0&&runtime.Fences==0,"Source change released controls before the original cooling deadline.");
                clock+=1000;
                if(clock==3000){source=afterAdmission;runtime.StateOverride=runtime.State with{Source=afterAdmission.ToString()};}
            });
            Send(form,0x11);
            require(clock==20_000&&runtime.Commands==2&&runtime.LastManualLevel==50,"Unplug/unknown after AC admission cancelled, restarted or extended cooling.");
            Send(form,0x11);require(clock==20_000&&runtime.Commands==2,"Repeated battery query restarted the admitted phase.");
            Send(form,0x16,true);require(runtime.Disposals==1,"Cooling continued on battery but skipped final release.");Exit(form);
        }
        foreach(var stop in new[]{"cancel","force","suspend","failure","guardian-loss"})
        {
            var runtime=new RecordingRuntime{StateOverride=Ready()};long clock=0;var source=PerformancePowerSourceKind.Ac;
            runtime.ManualCommandFixture=level=>{if(stop=="failure")throw new IOException("fixture write refused");Accept(runtime,level);return Task.CompletedTask;};
            using var form=new ProductForm("fixture://cooling-stop-"+stop,fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();
            form.SetSessionCoolingFixture(()=>source,()=>clock,()=>
            {
                clock+=1000;if(clock!=3000)return;
                if(stop=="cancel")Send(form,0x16);
                else if(stop=="force")Send(form,0x11,flags:0x40000000);
                else if(stop=="suspend")form.HandlePowerEventAsync(4).GetAwaiter().GetResult();
                else if(stop=="guardian-loss")runtime.StateOverride=runtime.State with{FanAuthority="Faulted"};
            });
            Send(form,0x11);require(clock<=3000&&runtime.Disposals==0,"Cooling ignored cancel/force/suspend/failure/guardian-loss.");
            if(stop=="cancel")require(runtime.Releases==1&&runtime.Fences==1,"Cancelled cooling retained its target or fabricated a resumed Automatic session.");
            else Send(form,0x16,true);
            Exit(form);
        }
        foreach(var pending in new[]{"mode","native"})
        {
            var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runtime=new RecordingRuntime{StateOverride=Ready(),ManualGate=pending=="mode"?gate:null};long clock=0;int nativeWrites=0;
            runtime.ManualCommandFixture=async level=>
            {
                if(pending=="native")await gate.Task;
                Accept(runtime,level);nativeWrites++;
            };
            using var form=new ProductForm("fixture://cooling-late-"+pending,fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();form.SetSessionCoolingFixture(()=>PerformancePowerSourceKind.Ac,()=>clock,()=>clock+=1000);
            Send(form,0x11);require(clock==20_000&&!form.SessionCoolingPreparationInFixture.IsCompleted,"Pending native preparation was declared drained at cooling timeout.");
            // Completion through the STA must remain possible during final cleanup.
            form.BeginInvoke(()=>gate.SetResult());Send(form,0x16,true);
            require(nativeWrites==0&&runtime.Disposals==1,"Queued cooling wrote after expiry or skipped final resource drainage.");Exit(form);
        }
        Console.WriteLine("PASS shutdown cooling: initial AC authority versus editor, source-change continuation of the original 20s phase then normal release, 50/50 native admission, duplicate/cancel/force/logoff/update/suspend/failure and late-native fences; no OS shutdown or hardware IO.");
    }
}
