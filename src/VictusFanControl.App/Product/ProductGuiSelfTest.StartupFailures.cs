using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestStartupFailurePaths(Action<bool,string> require)
    {
        var originalExitCode=Environment.ExitCode;
        try
        {
            var directory=Path.Combine(Path.GetTempPath(),"vfc-startup-log-fixture-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var blocked=Path.Combine(directory,"file-instead-of-directory");File.WriteAllText(blocked,"preserve original");
                require(AppLog.TryInitializeSessionFiles(blocked,new{fixture=true}) is not null&&File.ReadAllText(blocked)=="preserve original",
                    "Unavailable diagnostic directory escaped or damaged existing evidence.");
                var valid=Path.Combine(directory,"healthy");
                require(AppLog.TryInitializeSessionFiles(valid,new{fixture=true}) is null&&File.Exists(Path.Combine(valid,"session.json")),
                    "Diagnostic initialization could not recover at a valid location.");
            }
            finally{Directory.Delete(directory,true);}
            foreach(var error in new Exception[]{new UnauthorizedAccessException("Task query denied"),new OperationCanceledException("Task query timed out"),new System.Xml.XmlException("Malformed task response")})
            {
                var runtime=new RecordingRuntime();int reads=0;
                using var form=new ProductForm("fixture://optional-startup-query",fixture:runtime,fixtureProfiles:new(),
                    startupRegistrationReader:()=>++reads==1?Task.FromException<bool>(error):Task.FromResult(true));
                form.Show();PumpUntil(()=>reads==1&&form.Canvas.Notice.Contains("No se pudo consultar",StringComparison.Ordinal),"Optional status query did not finish.");
                require(runtime.Starts==1&&runtime.Fences==0&&runtime.Releases==0&&runtime.Disposals==0,
                    "Optional Windows task status released the hardware runtime.");
                require(!form.Canvas.StartupKnown&&!form.Canvas.State.LifecycleBlocked&&form.Canvas.State.Failure is null,
                    "Optional task query became a runtime/startup failure.");
                form.HandleCommand("startup-toggle");PumpUntil(()=>form.Canvas.StartupKnown,"Task status could not be retried without restarting.");
                require(reads==2&&form.Canvas.StartupEnabled&&runtime.Commands==0&&runtime.Fences==0,"Query retry wrote hardware or changed control.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Optional status fixture exit hung.");
                require(runtime.Disposals==1,"Optional task status duplicated final disposal.");
            }
            var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);int pendingReads=0;
            var healthy=new RecordingRuntime();
            using(var form=new ProductForm("fixture://slow-task-query",fixture:healthy,fixtureProfiles:new(),
                startupRegistrationReader:()=>{pendingReads++;return pending.Task;}))
            {
                form.Show();PumpUntil(()=>pendingReads==1,"Slow optional query never started.");
                var query=form.CheckStartupRegistrationAsync();require(ReferenceEquals(query,form.CheckStartupRegistrationAsync())&&pendingReads==1,"Query click duplicated the in-flight check.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Optional query held runtime exit hostage.");
                require(!query.IsCompleted&&healthy.Disposals==1,"Optional query prevented final cleanup.");
                pending.SetResult(true);PumpUntil(()=>query.IsCompleted,"Late optional query completion hung.");
                require(!form.Canvas.StartupKnown,"Late task response modified a closed form.");
            }
            foreach(var finalFailure in new[]{false,true})
            {
                var runtime=new RecordingRuntime {StartFailure="Initial Start failed",FenceFailure="Fence also failed",
                    ReleaseFailure="Initial release failed",DisposeFailure=finalFailure?"Final release unconfirmed":null};
                using var form=new ProductForm("fixture://failed-start-cleanup",fixture:runtime,fixtureProfiles:new());
                form.Show();PumpUntil(()=>runtime.Releases==1,"Fence failure skipped startup cleanup.");
                require(form.RecoveryMonitorActive&&form.Canvas.State.Runtime=="Failed"&&form.Canvas.State.LifecycleBlocked&&
                    form.Canvas.State.Failure?.Contains("Initial Start failed")==true,"Startup failure lost its original reason or recovery monitor.");
                runtime.Publish(new(){Runtime="Healthy",CanApplyPerformance=true});
                require(form.Canvas.State.LifecycleBlocked&&!form.Canvas.State.CanApplyPerformance,"Late startup publication removed the failure fence.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed startup release prevented final disposal.");
                require(runtime.Disposals==1&&runtime.Commands==0,"Earlier release exception skipped or duplicated final disposal.");
                require(Environment.ExitCode==(finalFailure?171:originalExitCode),"Final release failure was hidden or earlier failure masked confirmed final cleanup.");
            }
            Console.WriteLine("PASS startup failures: optional task denied/timeout/malformed queries preserve runtime; query retry/single-flight/late-close; failed fence still releases; earlier release fault still awaits final Dispose; final unknown release stays failed; zero hardware IO.");
            TestStartupInventoryRefresh(require);
            TestStartupToggleFlow(require);
        }
        finally{Environment.ExitCode=originalExitCode;}
    }

    private static void TestStartupToggleFlow(Action<bool,string> require)
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-startup-toggle-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach(var scenario in new[]{"enable","disable","write-failure","read-failure","mismatch"})
            {
                var persisted=scenario=="disable";int writes=0;
                var modules=Path.Combine(root,"custom modules");
                var runtime=new RecordingRuntime();bool? lastMinimized=null;
                using var form=new ProductForm(modules,fixture:runtime,fixtureProfiles:new(),profilesPath:Path.Combine(root,scenario+".json"),
                    startupRegistrationReader:()=>scenario=="read-failure"&&writes>0
                        ? Task.FromException<bool>(new IOException("Fixture readback failed")):Task.FromResult(persisted));
                form.StartupRegistrationWriterInFixture=(enabled,path,minimized)=>
                {
                    writes++;lastMinimized=minimized;require(path==modules,"Startup registration lost custom module path.");
                    if(scenario=="write-failure")return Task.FromException(new IOException("Fixture registration failed"));
                    if(scenario!="mismatch")persisted=enabled;
                    return Task.CompletedTask;
                };
                form.Show();PumpUntil(()=>form.Canvas.StartupKnown,"Initial startup status never arrived.");
                form.HandleCommand("startup-toggle");PumpUntil(()=>writes==1&&!form.Canvas.Busy,"Startup toggle did not complete.");
                var success=scenario is "enable" or "disable";
                require(form.Canvas.StartupKnown==success&&runtime.Commands==0&&runtime.Fences==0&&runtime.Disposals==0,
                    "Startup checkbox lost confirmation state or affected hardware authority: "+scenario);
                if(success)require(form.Canvas.StartupEnabled==(scenario=="enable")&&form.Canvas.Notice==(scenario=="enable"?"Inicio con Windows activado.":"Inicio con Windows desactivado."),
                    "Startup toggle did not reflect confirmed state or retained unrelated Save Automatic message.");
                if(scenario=="enable")
                {
                    form.HandleCommand("minimized-toggle");form.HandleCommand("save");PumpUntil(()=>writes==2&&!form.Canvas.Busy,"Enabled startup preferences save did not finish.");
                    require(lastMinimized==form.Draft.StartMinimized&&form.Canvas.StartupEnabled,"Save lost startup state or minimized preference.");
                    form.HandleCommand("startup-toggle");PumpUntil(()=>writes==3&&!form.Canvas.Busy,"Second toggle failed.");
                    require(form.Canvas.StartupKnown&&!form.Canvas.StartupEnabled&&form.Canvas.Notice=="Inicio con Windows desactivado.","Confirmed checkbox could not be disabled.");
                }
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Startup toggle fixture exit failed.");
            }
            Console.WriteLine("PASS startup checkbox: confirmed enable/disable/save, custom modules, minimized preferences, write/read/mismatch failures become unknown; clear confirmation without Save Automatic; zero task registration or hardware IO.");
        }
        finally { Directory.Delete(root,true); }
    }

    private static void TestStartupInventoryRefresh(Action<bool,string> require)
    {
        foreach(var scenario in new[]{"valid","gone","invalid","disabled","close","suspend","read-exception"})
        {
            int reads=0,runs=0,created=0;
            var pending=new ProductRecoverySelection(Guid.NewGuid(),Guid.Empty);
            var next=new RecordingRuntime();
            var refreshed=new TaskCompletionSource<PerformanceRecoveryPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var form=new ProductForm("fixture://startup-inventory-refresh",fixtureProfiles:new(){RecoverSessionsAutomatically=scenario!="disabled"},
                runtimeFactory:()=>throw new IOException("Unknown inventory opened the initial controller."),
                recoveryPreflight:()=>
                {
                    if(++reads==1)return new(true,"Initial record temporarily unavailable");
                    if(scenario=="read-exception")throw new IOException("Read still unavailable");
                    return refreshed.Task.GetAwaiter().GetResult();
                });
            form.RecoverySelectionReader=()=>pending;
            form.RecoveryRunner=_=>{runs++;pending=new(Guid.Empty,Guid.Empty);return Task.FromResult(new ProductRecoveryResult(true,"fixture","confirmed"));};
            form.RecoveryRuntimeFactory=_=>{created++;return Task.FromResult<IProductRuntime>(next);};
            form.Show();Application.DoEvents();var clock=Environment.TickCount64;
            form.TryAutomaticRecovery(clock);form.TryAutomaticRecovery(clock+9999);
            require(reads==1&&created==0&&runs==0,"Unavailable inventory bypassed the read delay or opened a controller.");
            form.TryAutomaticRecovery(clock+10000);
            if(scenario=="disabled")
            {
                require(reads==1&&!form.StartupInventoryRefreshPending,"Disabled automatic recovery refreshed records.");
                var disabledExit=form.RequestExitAsync();PumpUntil(()=>disabledExit.IsCompleted,"Disabled preflight fixture exit hung.");continue;
            }
            PumpUntil(()=>reads==2,"Read-only startup inventory refresh never started.");
            form.TryAutomaticRecovery(clock+20000);
            require(reads==2&&runs==0&&created==0,"Concurrent refresh duplicated a read or started recovery.");
            Task? exit=null;
            if(scenario=="close") {exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"A read-only inventory check held exit hostage.");}
            if(scenario=="suspend")CompleteGuiTask(form.HandlePowerEventAsync(4),"Refresh suspend fence hung.");
            if(scenario=="gone")pending=new(Guid.Empty,Guid.Empty);
            refreshed.TrySetResult(scenario=="gone"?new(false,"Resolved externally"):
                scenario is "valid" or "close" or "suspend"?new(true,"Now readable",pending.CpuSession):new(true,"Still invalid"));
            PumpUntil(()=>!form.StartupInventoryRefreshPending,"Read-only refresh never completed.");
            if(scenario is "valid" or "gone")
            {
                form.TryAutomaticRecovery(clock+20000);form.TryAutomaticRecovery(clock+30000);
                PumpUntil(()=>created==1&&!form.Canvas.Busy,"Fresh inventory could not leave startup recovery.");
                require(runs==(scenario=="valid"?1:0)&&next.Starts==1&&next.Commands==0,"Refresh wrote without exact valid inventory or activated control early.");
            }
            else
            {
                require(runs==0&&created==0&&form.Canvas.State.PerformanceRecovery?.Pending==true,"Invalid/late/interrupted inventory authorized recovery.");
                if(scenario is "close" or "suspend")require(form.Canvas.State.PerformanceRecovery?.CpuSession is null,"Late/interrupted read changed the startup inventory.");
                if(scenario is "invalid" or "read-exception")
                {
                    foreach(var offset in new[]{40000,100000})
                    {form.TryAutomaticRecovery(clock+offset);PumpUntil(()=>!form.StartupInventoryRefreshPending,"Bounded refresh failed to finish.");}
                    form.TryAutomaticRecovery(clock+1000000);
                    require(reads==4&&runs==0&&created==0,"Read-only refresh exceeded three attempts or wrote unknown records.");
                }
            }
            exit??=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Inventory refresh fixture exit hung.");
        }
        Console.WriteLine("PASS unknown startup inventory: read-only 10/30/60s retry, valid/absent revalidation, three-read bound, disabled preference, read failures, single-flight, late close and suspend fences; zero hardware IO.");
    }
}
