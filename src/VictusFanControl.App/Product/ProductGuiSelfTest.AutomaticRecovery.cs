using VictusFanControl.Product;
using VictusFanControl.Recovery;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestBackgroundRecovery(Action<bool,string> require)
    {
        var exitCode=Environment.ExitCode;
        try
        {
            foreach(var scenario in new[]{"resume","stop","disabled-resume","runner-failure","suspend","close","changed","disabled-recovery"})
            {
                Console.WriteLine("Background recovery fixture: " + scenario);
                var selected=new ProductRecoverySelection(Guid.NewGuid(),Guid.NewGuid()); var pending=selected;
                var old=new RecordingRuntime{DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
                var next=new RecordingRuntime();int runs=0,created=0;
                var released=new TaskCompletionSource<ProductRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var profiles=new ProductProfiles {RecoverSessionsAutomatically=scenario!="disabled-recovery",
                    Protections=new(){ResumeAutomatic=scenario!="disabled-resume"}};
                using var form=new ProductForm("fixture://automatic-recovery",fixture:old,fixtureProfiles:profiles);
                form.RecoverySelectionReader=()=>scenario=="changed" ? selected with{GpuSession=Guid.NewGuid()} : pending;
                form.RecoveryRunner=s=>{runs++;require(old.Disposals==1 && s==selected,"Background recovery bypassed release or exact selection.");return released.Task;};
                form.RecoveryRuntimeFactory=p=>{created++;require(p.Ac.CpuPl1Watts==profiles.Ac.CpuPl1Watts,"Background recovery applied an unsaved draft.");return Task.FromResult<IProductRuntime>(next);};
                form.Show();Application.DoEvents();
                var at=DateTimeOffset.UtcNow.AddSeconds(-2);
                ProductRuntimeState Ready(int i)=>new(){Target=ProductRecoverySelection.Target,AutomaticAuthorized=true,Runtime="Healthy",Source="Ac",
                    FanAuthority="Firmware",CanApplyPerformance=true,Snapshot=new TelemetrySnapshot(at.AddSeconds(i),"CPU",55,20,30,"GPU",45,15,20,2000,2000)
                    {CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",55)]}};
                old.Publish(Ready(2) with{FanMode="Automatic"});form.EditValue("pl1",38);
                old.Publish(Ready(2) with{LifecycleBlocked=true,Runtime="Failed",PerformanceRecovery=new(true,"pending",selected.CpuSession,selected.GpuSession)});
                var clock=Environment.TickCount64;form.TryAutomaticRecovery(clock);form.TryAutomaticRecovery(clock+10000);
                if(scenario=="disabled-recovery")
                {
                    require(!form.Canvas.Busy && runs==0,"Disabled recovery launched unattended work.");
                    old.DisposeGate.SetResult();var disabledExit=form.RequestExitAsync();PumpUntil(()=>disabledExit.IsCompleted,"Disabled fixture exit hung.");continue;
                }
                if(scenario=="changed")
                {
                    PumpUntil(()=>!form.Canvas.Busy,"Changed background inventory hung.");
                    require(runs==0 && created==0 && old.Disposals==0,"Changed inventory launched hardware recovery.");
                    old.DisposeGate.SetResult();var changedExit=form.RequestExitAsync();PumpUntil(()=>changedExit.IsCompleted,"Changed fixture exit hung.");continue;
                }
                PumpUntil(()=>old.Disposals==1,"Background recovery did not drain the old runtime.");
                require(runs==0 && created==0,"Background recovery passed an unresolved disposal.");
                old.DisposeGate.SetResult();PumpUntil(()=>runs==1,"Background recovery runner did not launch.");
                if(scenario=="stop")form.HandleCommand("firmware");
                if(scenario=="suspend")CompleteGuiTask(form.HandlePowerEventAsync(4),"Power event fixture hung.");
                Task? exit=scenario=="close"?form.RequestExitAsync():null;
                if(exit is not null)require(!exit.IsCompleted,"Exit did not retain the idle recovery coordinator.");
                if(scenario!="runner-failure")pending=new(Guid.Empty,Guid.Empty);
                released.SetResult(new(scenario!="runner-failure","fixture-evidence",scenario=="runner-failure"?"unconfirmed reset":"released"));
                PumpUntil(()=>!form.Canvas.Busy||exit?.IsCompleted==true,"Background recovery never completed.");
                if(scenario is "runner-failure" or "suspend" or "close")require(created==0,"Failed/interrupted recovery recreated a controller.");
                else
                {
                    require(created==1 && next.Commands==0 && form.Draft.Ac.CpuPl1Watts==38,"Recovery activated early or lost unsaved preferences.");
                    // Recovery includes disk/IPC work. Its old samples must not
                    // become eligible merely because cleanup eventually finished.
                    at=DateTimeOffset.UtcNow-VictusFanControl.Safety.SafetyGate.MaximumTelemetryAge-TimeSpan.FromSeconds(3);
                    next.Publish(Ready(0));next.Publish(Ready(1));next.Publish(Ready(2));
                    require(next.Commands==0,"Recovery admitted expired observations.");
                    // Generate the independent fresh stream after recovery, not
                    // before its variable-duration cleanup/diagnostic operations.
                    at=DateTimeOffset.UtcNow.AddSeconds(-2);
                    next.Publish(Ready(0));next.Publish(Ready(1));next.Publish(Ready(2));PumpUntil(()=>!form.Canvas.Busy,"Recovery activation hung.");
                    require(next.Commands==(scenario=="resume"?1:0),"Recovery ignored prior Automatic intent, stop or disabled resumption.");
                }
                form.TryAutomaticRecovery(clock+120000);
                require(runs==1,"Background recovery repeated an unconfirmed hardware operation.");
                exit??=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Background recovery fixture exit hung.");
                require(old.Disposals==1,"Background recovery disposed the previous runtime twice.");
            }
            foreach (var scenario in new[]{"firmware","activate","stop-before","resolved-while-waiting"})
            {
                Console.WriteLine("Startup recovery fixture: " + scenario);
                var activate=scenario!="firmware";
                var pending=new ProductRecoverySelection(Guid.NewGuid(),Guid.Empty);var next=new RecordingRuntime();int initial=0,runs=0;
                using var form=new ProductForm("fixture://startup-recovery",fixtureProfiles:new(){ActivateAutomaticOnStart=activate},
                    runtimeFactory:()=>{initial++;throw new IOException("Pending startup opened the normal controller.");},
                    recoveryPreflight:()=>new(true,"pending",pending.CpuSession));
                form.RecoverySelectionReader=()=>pending;
                form.RecoveryRunner=_=>{runs++;pending=new(Guid.Empty,Guid.Empty);return Task.FromResult(new ProductRecoveryResult(true,"fixture","released"));};
                form.RecoveryRuntimeFactory=_=>Task.FromResult<IProductRuntime>(next);
                form.Show();Application.DoEvents();var clock=Environment.TickCount64;
                if(scenario=="stop-before")form.HandleCommand("firmware");
                form.TryAutomaticRecovery(clock);form.TryAutomaticRecovery(clock+10000);
                if(scenario=="resolved-while-waiting")pending=new(Guid.Empty,Guid.Empty);
                PumpUntil(()=>next.Starts==1 && !form.Canvas.Busy,"Pending startup did not recover in background.");
                require(runs==(scenario=="resolved-while-waiting"?0:1),"Resolved startup inventory invoked a recovery write.");
                require(initial==0 && next.Starts==1 && next.Commands==0,"Startup recovered by opening an unresolved controller or activating early.");
                var at=DateTimeOffset.UtcNow.AddSeconds(-2);
                for(int i=0;i<3;i++)next.Publish(new(){Target=ProductRecoverySelection.Target,Source="Ac",Runtime="Healthy",AutomaticAuthorized=true,
                    FanAuthority="Firmware",CanApplyPerformance=true,Snapshot=new TelemetrySnapshot(at.AddSeconds(i),"CPU",55,20,30,"GPU",45,15,20,2000,2000)
                    {CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",55)]}});
                PumpUntil(()=>!form.Canvas.Busy,"Startup recovery activation hung.");
                require(next.Commands==(scenario is "activate" or "resolved-while-waiting"?1:0),"Startup recovery ignored the saved start preference or user stop during its wait.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Startup recovery exit hung.");
            }
            TestBackgroundInventoryRetry(require);
            Console.WriteLine("Background GUI recovery: PASS (release drain, exact inventory, saved/applied profiles, fresh reentry, user stop, disabled preferences, unconfirmed reset, suspend and exit; zero hardware IO).");
        }
        finally{Environment.ExitCode=exitCode;}
    }
    private static void TestBackgroundInventoryRetry(Action<bool,string> require)
    {
        foreach(var malformed in new[]{false,true})
        {
            var selected=new ProductRecoverySelection(Guid.NewGuid(),Guid.Empty);var pending=selected;
            var old=new RecordingRuntime();var next=new RecordingRuntime();int reads=0,runs=0;
            using var form=new ProductForm("fixture://inventory-retry",fixture:old,fixtureProfiles:new(){RecoverSessionsAutomatically=true});
            form.RecoverySelectionReader=()=>
            {
                reads++;
                if(malformed)throw new InvalidDataException("malformed record");
                if(reads==1)throw new IOException("temporary sharing failure");
                return pending;
            };
            form.RecoveryRunner=_=>{runs++;pending=new(Guid.Empty,Guid.Empty);return Task.FromResult(new ProductRecoveryResult(true,"fixture","released"));};
            form.RecoveryRuntimeFactory=_=>Task.FromResult<IProductRuntime>(next);
            form.Show();Application.DoEvents();
            old.Publish(old.State with{LifecycleBlocked=true,Runtime="RecoveryRequired",PerformanceRecovery=new(true,"pending",selected.CpuSession)});
            var clock=Environment.TickCount64;form.TryAutomaticRecovery(clock);form.TryAutomaticRecovery(clock+10000);
            PumpUntil(()=>!form.Canvas.Busy,"Inventory read failure left the recovery coordinator busy.");
            require(reads==1&&runs==0&&old.Disposals==0&&form.RecoveryMonitorActive,"Read failure reached release or stopped monitoring.");
            form.TryAutomaticRecovery(clock+20000);
            require(reads==1&&runs==0,"Inventory read retried without progressive delay.");
            form.TryAutomaticRecovery(clock+40000);
            if(malformed)require(reads==1&&runs==0&&old.Disposals==0,"Malformed records were retried as transient IO.");
            else
            {
                PumpUntil(()=>runs==1&&!form.Canvas.Busy,"Transient read failure did not recover automatically.");
                require(old.Disposals==1&&next.Starts==1&&next.Commands==0,"Read retry duplicated release or acquired control.");
            }
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Inventory retry fixture exit hung.");
        }
        Console.WriteLine("Recovery inventory retry: PASS (transient read resumes after backoff, malformed record retained, no release before valid exact inventory; zero hardware IO).");
    }
}
