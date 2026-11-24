using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static partial class ProductRuntimePerformanceSelfTest
{
    // No Form, worker.Start(), production reader or hardware command is used.
    // Both detached helpers are restricted to their explicit fixture entries.
    private sealed class RepairFixture : IAsyncDisposable
    {
        internal readonly Source Source=new();
        internal readonly PerformanceGuardianClient Client;
        internal readonly WmiFanGuiGuardianClient? Raw;
        internal readonly ProductWmiLifetime? Shared;
        internal ProductRuntime Runtime=null!;
        internal TelemetryWorker Worker=null!;
        internal int Launches;
        internal bool RefuseLaunch;
        internal bool RejectRetainedJournals;
        internal string? Report;
        private static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated repair inventory.");
        internal RepairFixture(ProductProfiles p,bool wmi=false)
        {
            Client=new("fixture://repairs",start=>
            {
                Require(start.ArgumentList[0]=="--gui-session","Unexpected helper entry.");
                Launches++;
                if(RefuseLaunch)throw new IOException("Fixture launch refused before child creation.");
                start.ArgumentList[0]="--gui-fixture-session";
                Report=start.ArgumentList[start.ArgumentList.IndexOf("--report")+1];return Process.Start(start);
            },Inventory);
            if(wmi){Raw=new(fixture:true);Shared=new(Raw,Source);}
            Create(p);
        }
        private void Create(ProductProfiles p,ProductRuntime? previous=null)
        {
            Worker=new("fixture://repairs",_=>ValueTask.FromResult(Sample()));
            Worker.StateMachine.Transition(SystemState.Healthy,"Synthetic repair observations.");
            Runtime=new("fixture://repairs",p,releasedRuntime:previous,preservePerformanceFromReleasedRuntime:previous is not null,
                dependencies:new(Hardware(),new Backend(),Worker,Client,Source,Inventory,
                    ()=>new(RejectRetainedJournals?Guid.NewGuid():Client.LastStatus?.CpuSession,Client.LastStatus?.GpuSession),Shared));
            Fresh();
        }
        internal void Fresh(TelemetrySnapshot? sample=null)=>Worker.PublishAcknowledgementSnapshot(sample??Sample());
        internal Task Reconcile()=>(Task)typeof(ProductRuntime).GetMethod("ReconcileFunctionsCoreAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Runtime,null)!;
        internal async Task Pump(int count=6)
        {for(var i=0;i<count;i++){await Worker.ProcessAndPublishSnapshotAsync(Sample(),default);await Task.Delay(300);}}
        internal async Task ReplaceFans(ProductProfiles p)
        {
            var old=Runtime;old.FenceFanRetry("Isolated fan-only replacement");await old.RetireFanRuntimeAsync();
            try{Create(p,old);}finally{await old.DisposeAsync();}
        }
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if(Report is not null&&File.Exists(Report))
            {
                using var r=JsonDocument.Parse(File.ReadAllText(Report));
                Require(r.RootElement.GetProperty("CpuHardwareWriteAttempts").GetInt32()==0&&
                    r.RootElement.GetProperty("GpuHardwareWriteAttempts").GetInt32()==0,"Repair fixture escaped hardware isolation.");
            }
        }
    }
    private static ProductProfiles RepairProfiles()=>new(){CpuEnabled=false,GpuEnabled=false,Functions=new ProductFunctionPolicies().Unlinked()};
    internal static async Task<int> RunRepairChecksAsync()
    {
        try
        {
            await PendingValuesAsync();await CtgpIntentAndFailureAsync();await AppliedPoliciesAsync();
            RepairProfileShapes();await RetainedStartupAndRecoveryAsync();await LegacyBaseAndMaintenanceAsync();
            await CtgpStaleRetryAsync();await RetainedFaultsAsync();
            await FailedFanPreparationOwnershipAsync();
            Console.WriteLine("PASS independent repair regressions A01-A05, B01-B04, B07, C01-C02; no desktop or hardware IO.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    private static async Task PendingValuesAsync()
    {
        foreach(var cpu in new[]{true,false})
        {
            var p=RepairProfiles();await using var f=new RepairFixture(p);await f.Runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,p);
            var edited=p with{Ac=p.Ac with{CpuPl1Watts=28,CpuPl2Watts=38,GpuMaximumMHz=1100}};
            f.Fresh(cpu?Sample() with{CpuTemperatureC=null,CpuExpectedPhysicalCoreCount=null,CpuCoreTemperatures=[]}:
                Sample() with{GpuTemperatureC=null,GpuPowerW=null,GpuName=null});
            await f.Runtime.SetPerformanceDomainAsync(cpu,true,edited);Require(!f.Client.HasProcess,"Pending function ignored absent own sensors.");
            var peer=cpu?p with{Ac=p.Ac with{GpuMaximumMHz=1200}}:p with{Ac=p.Ac with{CpuPl1Watts=30,CpuPl2Watts=40}};
            await f.Runtime.SetPerformanceDomainAsync(!cpu,true,peer);
            f.Fresh();await f.Reconcile();
            Require(cpu?f.Client.AppliedConfiguration is{CpuEnabled:true,AcPl1Watts:28,AcPl2Watts:38}:
                f.Client.AppliedConfiguration is{GpuEnabled:true,AcGpuMaximumMHz:1100},"A01 pending activation lost requested values.");
        }
        Console.WriteLine("PASS A01: pending CPU/GPU use the explicit values after own sensors return.");
    }
    private static async Task CtgpIntentAndFailureAsync()
    {
        foreach(var fault in new[]{false,true})
        {
            var p=RepairProfiles() with{CtgpEnabled=true,Functions=RepairProfiles().Functions! with{Ctgp=new(ProductFunctionBinding.StartWithAutomatic,true)}};
            await using var f=new RepairFixture(p,wmi:true);await f.Runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,p);await f.Pump(3);
            Require(f.Shared!.CtgpWanted,"No initial cTGP intent in fixture.");
            if(fault)typeof(ProductWmiLifetime).GetField("_ctgpFaulted",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(f.Shared,true);
            else await f.Runtime.SetCtgpAsync(false);
            f.Source.Current=PerformancePowerSourceKind.Battery;await f.Pump();
            Require(!f.Runtime.State.LifecycleBlocked&&f.Runtime.State.FanMode=="Automatic","A04 local cTGP failure interrupted independent fan control.");
            if(!fault)Require(!f.Shared.CtgpWanted&&!f.Runtime.State.FunctionRequests.Ctgp&&f.Raw!.Ctgp is{Active:false},"A02 source transition replayed cTGP ON after OFF.");
        }
        Console.WriteLine("PASS A02/A04: source transition preserves OFF and contains local independent cTGP failure; native uncertainty remains a separate fence.");
    }
    private static async Task AppliedPoliciesAsync()
    {
        var p=RepairProfiles();await using var f=new RepairFixture(p);
        var draft=p with{Functions=p.Functions! with{ExternalOutputMode=ProductExternalOutputMode.Fixed,ExternalFixedOutput=87}};
        await f.Runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,draft);
        Require(f.Runtime.State.FunctionPolicies==p.Functions,"A05 Automatic applied draft links without Apply.");
        await f.Runtime.ConfigureFunctionsAsync(p.Functions! with{StopAutomaticOnPerformanceFailure=true});await f.Pump(3);
        Require(!f.Client.HasProcess&&!f.Runtime.State.LifecycleBlocked&&f.Runtime.State.FanMode=="Automatic","A03 nonexistent inactive domains interrupted Automatic.");
        await f.Runtime.ConfigureFunctionsAsync(draft.Functions!);
        Require(f.Runtime.State.FunctionPolicies==draft.Functions,"Explicit Apply no longer changes coordination.");
        await f.Runtime.ConfigureFunctionsAsync(draft.Functions! with{StopAutomaticOnPerformanceFailure=true});
        f.Fresh();await f.Runtime.SetPerformanceDomainAsync(true,true,p);await f.Pump(1);
        Require(!f.Runtime.State.LifecycleBlocked,"A valid later CPU activation broke coupled Automatic.");
        typeof(PerformanceGuardianClient).GetProperty("LastStatus",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(f.Client,f.Client.LastStatus! with{CpuState="Failed"});
        await f.Pump(1);Require(f.Runtime.State.LifecycleBlocked,"Coupled real CPU failure no longer interrupts Automatic.");
        Console.WriteLine("PASS A03/A05: only explicit Apply changes links; no-limit coupling does not require an absent supervisor.");
    }
    private static void RepairProfileShapes()
    {
        var directory=Path.Combine(Path.GetTempPath(),"Victus-repair-shapes-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        foreach(var shape in new[]{"[]","null","42","\"broken\""})
        {
            var path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".json");File.WriteAllText(path,shape);
            _=ProductProfilesStore.Load(path,out var notice);Require(notice is not null,"Malformed profile was accepted.");
            ProductProfilesStore.Save(RepairProfiles(),path);var parsed=ProductProfilesStore.Parse(File.ReadAllText(path));
            Require(parsed.Functions==RepairProfiles().Functions,"B01 explicit functions could not repair non-object JSON.");
        }
        Console.WriteLine("PASS B01: explicit save repairs array/null/number/string profile roots.");
    }
    private static void QualifyStartup(ProductRuntimeState state,bool resumption)
    {
        var gate=new ProductStartupAutomatic(0,manualRetry:!resumption,protections:new(),resumption:resumption);
        Require(gate.EntryReason==ProductFunctionEntryReason.AutomaticRecovery&&new ProductStartupAutomatic(0).EntryReason==ProductFunctionEntryReason.ApplicationStartup,
            "Manual retry repeated the application-start shortcut.");
        var now=DateTimeOffset.UtcNow;var span=resumption?ProductRecoveryEpisode.StableMilliseconds:2000;
        // Each observation is newly acquired, using a synthetic clock. No real wait or power event.
        for(var i=0;i<=span/1000;i++)
        {
            var at=now.AddSeconds(i);var s=Sample() with{Timestamp=at};
            gate.Observe(state with{Snapshot=s},at,i*1000L);
        }
        Require(gate.Finished&&!gate.Cancelled,"B02 confirmed retained supervisor blocked startup/retry/resumption.");
        var waiting=new ProductStartupAutomatic(0);
        waiting.Observe(state with{PerformanceSessionConfirmed=false},now,0);
        Require(!waiting.Finished&&!waiting.Cancelled,"Unconfirmed owner was admitted or permanently cancelled instead of waiting.");
        var waitState=state with{FanMode="Firmware",FunctionRequests=new(CpuLimits:true),FunctionPolicies=new(){CpuLimits=new(ProductFunctionBinding.FollowAutomatic,true)}};
        Require(ProductCanvas.FunctionWaitText(waitState,ProductFunction.CpuLimits,"Disabled")=="En espera de Automático"&&
            ProductCanvas.FunctionWaitText(waitState,ProductFunction.CpuLimits,"RecoveryRequired")=="Recuperación pendiente","Wait explanation hid either the binding or a real failure.");
    }
    private static async Task RetainedStartupAndRecoveryAsync()
    {
        var p=RepairProfiles();await using var f=new RepairFixture(p);await f.Runtime.SetPerformanceDomainAsync(true,true,p);
        var id=f.Client.LastStatus!.CpuSession;QualifyStartup(f.Runtime.State,false);QualifyStartup(f.Runtime.State,true);
        var degraded=f.Runtime.State with{Runtime="Degraded",LifecycleBlocked=true};
        Require(ProductAutomaticRecovery.PreserveIndependentFunctions(degraded),"B04 independent sensor failure selected global recovery.");
        Require(ProductAutomaticRecovery.KeepOwnerAfterFanPreparationFailure(degraded,true,true,false,false)&&
            !ProductAutomaticRecovery.KeepOwnerAfterFanPreparationFailure(degraded,true,true,true,false)&&
            !ProductAutomaticRecovery.KeepOwnerAfterFanPreparationFailure(degraded,false,true,false,false),"Failed fan preparation closed independent owners or blocked an explicit global exit.");
        var episode=new ProductRecoveryEpisode();var recovery=new ProductAutomaticRecovery(episode);
        Require(!recovery.TakeSessionRequest(degraded,0)&&recovery.TakeSessionRequest(degraded,10000),"Recovery fixture did not reserve.");
        recovery.DeferSessionPreparation();
        Require(!episode.Uncertain&&episode.Attempts==0&&!recovery.TakeSessionRequest(degraded,10001)&&recovery.TakeSessionRequest(degraded,20001),
            "Waiting for retained status consumed an attempt or latched native uncertainty before doing work.");
        Require(!ProductAutomaticRecovery.PreserveIndependentFunctions(degraded with{FunctionPolicies=new(),FunctionCoordinationExplicit=false}),"Legacy coupled recovery policy changed.");
        await f.ReplaceFans(p);
        Require(f.Client.LastStatus!.CpuSession==id&&f.Client.LimitsActive&&f.Launches==1,"B04 fan recovery released independent CPU ownership.");
        QualifyStartup(f.Runtime.State,false);
        Console.WriteLine("PASS B02/B04: qualified retained owner enters startup/retry/resumption; fan-only recovery preserves exact CPU session. Routing policy and actual retention tested without constructing a Form.");
    }
    private static async Task LegacyBaseAndMaintenanceAsync()
    {
        var p=new ProductProfiles{CpuEnabled=false,GpuEnabled=false};await using var legacy=new RepairFixture(p);
        var projectionSample=Sample();
        ProductRuntimeState Projection()=>legacy.Runtime.State with{Runtime="Healthy",FanMode="Manual",FanAuthority="Custom",ManualAuthorized=true,
            ManualAppliedLevel=30,FanLevel=30,CommandedCpuLevel=30,CommandedGpuLevel=30,Snapshot=projectionSample};
        var export=new TelemetryExportService(Projection,new MatrixMemory());
        Require(export.Capture(DateTimeOffset.UtcNow).SolicitudValida,"Legacy fixture has no fan evidence.");
        await legacy.Runtime.ConfigureFunctionsAsync(p.FunctionPolicies());
        Require(legacy.Runtime.State.FunctionRequests.ExternalCooling&&export.Capture(DateTimeOffset.UtcNow).SolicitudValida,"B03 first Apply lost inherited external intent.");
        await legacy.Runtime.SetExternalCoolingRequestedAsync(false);await legacy.Runtime.ConfigureFunctionsAsync(p.FunctionPolicies());
        Require(!legacy.Runtime.State.FunctionRequests.ExternalCooling&&!export.Capture(DateTimeOffset.UtcNow).SolicitudValida,"Explicit base OFF was overwritten by migration.");
        foreach(var pending in new[]{false,true})
        {
            var profiles=RepairProfiles() with{Functions=RepairProfiles().Functions! with{CpuLimits=new(ProductFunctionBinding.StartWithInternalFans,true)}};
            await using var f=new RepairFixture(profiles);
            if(pending){await f.Runtime.ConfigureFunctionsAsync(profiles.Functions! with{CpuLimits=new(ProductFunctionBinding.FollowInternalFans,true)});await f.Runtime.SetPerformanceDomainAsync(true,true,profiles);}
            var before=f.Runtime.State.FunctionRequests;var cooling=new ProductShutdownCooling(f.Runtime,()=>0,()=>true);cooling.Start();
            try{await cooling.Preparation;}catch(InvalidOperationException){/* Fake backend has no physical RPM acknowledgement. */}
            await f.Reconcile();Require(f.Runtime.State.FunctionRequests==before&&!f.Client.HasProcess,"B07 shutdown maintenance acquired linked or pending CPU.");
        }
        Console.WriteLine("PASS B03/B07: first legacy Apply preserves intent, explicit OFF wins, cooling changes no linked intent and acquires no pending domain.");
    }
    private static async Task CtgpStaleRetryAsync()
    {
        await using var f=new RepairFixture(RepairProfiles(),wmi:true);await f.Runtime.SetCtgpAsync(true);
        var gate=(SemaphoreSlim)typeof(ProductWmiLifetime).GetField("_commands",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Shared)!;
        var pending=typeof(ProductWmiLifetime).GetField("_ctgpPendingCommands",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await gate.WaitAsync();var off=f.Runtime.SetCtgpAsync(false);var queued=false;
        try
        {
            var clock=Stopwatch.StartNew();
            while((int)pending.GetValue(f.Shared)!<2&&clock.ElapsedMilliseconds<4000)
            {
                WmiFanExperiment.WriteJson(Path.Combine(f.Raw!.SessionDirectory,"ctgp-status.json"),new CtgpStatus("Waiting",false,false,null,DateTimeOffset.UtcNow));
                await Task.Delay(15);
            }
            queued=(int)pending.GetValue(f.Shared)!>=2;
        }
        finally{gate.Release();}
        await off;var drain=Stopwatch.StartNew();while(f.Shared!.CtgpChanging&&drain.ElapsedMilliseconds<5000)await Task.Delay(10);
        Require(queued&&!f.Shared.CtgpWanted&&!f.Shared.CtgpChanging,"C01 retry fixture did not drain OFF correctly.");
        await f.DisposeAsync();using var report=JsonDocument.Parse(File.ReadAllText(f.Raw!.ReportPath));
        var setters=report.RootElement.GetProperty("FixtureRequests").EnumerateArray().Where(x=>x.GetProperty("CommandType").GetInt32()==0x22)
            .Select(x=>(int)x.GetProperty("Payload").GetBytesFromBase64()[0]).ToArray();
        Require(setters.SequenceEqual(new[]{1,0}),"C01 a queued retry reacquired cTGP after OFF.");
        Console.WriteLine("PASS C01: queued retry discarded after OFF; fake policy trace is exactly ON, OFF.");
    }
    private static async Task RetainedFaultsAsync()
    {
        foreach(var cpu in new[]{true,false})
        {
            var p=RepairProfiles();await using var f=new RepairFixture(p);f.RefuseLaunch=true;
            try{await f.Runtime.SetPerformanceDomainAsync(cpu,true,p);throw new Exception("Fixture launch unexpectedly succeeded.");}catch(IOException){}
            f.RefuseLaunch=false;var function=cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks;
            Require(f.Runtime.State.FunctionFaults.Get(function),"Missing original failure latch.");
            await f.Reconcile();Require(f.Launches==1&&!f.Client.HasProcess,"Original failure did not prevent retry.");
            await f.ReplaceFans(p);await f.Reconcile();
            Require(f.Runtime.State.FunctionFaults.Get(function)&&f.Launches==1&&!f.Client.HasProcess,"C02 fan replacement cleared independent failure or retried its acquisition.");
            f.Fresh();await f.Runtime.SetPerformanceDomainAsync(cpu,true,p);
            Require(!f.Runtime.State.FunctionFaults.Get(function)&&f.Launches==2&&f.Client.LimitsActive,"Explicit per-domain retry no longer recovers after a pre-launch refusal.");
        }
        Console.WriteLine("PASS C02: CPU/GPU failures survive fan-only replacement; explicit user retry still works.");
    }
    private static async Task FailedFanPreparationOwnershipAsync()
    {
        var p=RepairProfiles();await using var f=new RepairFixture(p);
        await f.Runtime.SetPerformanceDomainAsync(true,true,p);f.Fresh();await f.Runtime.SetPerformanceDomainAsync(false,true,p);
        var before=f.Runtime.State;var cpu=f.Client.LastStatus!.CpuSession;var gpu=f.Client.LastStatus.GpuSession;
        f.RejectRetainedJournals=true;f.Runtime.FenceFanRetry("Isolated replacement proof refusal");
        var rejected=false;try{await f.Runtime.RetireFanRuntimeAsync();}catch(IOException){rejected=true;}
        Require(rejected&&f.Client.LimitsActive,"Refused ownership proof did not preserve the original native owner.");
        f.RejectRetainedJournals=false;var globalCloses=0;
        async Task Close(){globalCloses++;await f.Runtime.DisposeAsync();}
        var kept=await ProductAutomaticRecovery.CleanupFailedFanPreparationAsync(before,true,true,false,false,Close);
        Require(kept&&globalCloses==0&&f.Client.LimitsActive&&f.Client.LastStatus!.CpuSession==cpu&&f.Client.LastStatus.GpuSession==gpu,
            "D04 failed fan preparation globally closed or replaced exact independent sessions.");
        kept=await ProductAutomaticRecovery.CleanupFailedFanPreparationAsync(before,true,true,true,false,Close);
        Require(!kept&&globalCloses==1&&!f.Client.HasProcess,"Preserving independent owners blocked explicit global exit.");
        Console.WriteLine("PASS D04 actual cleanup coordinator: rejected replacement proof keeps exact CPU/GPU owner and sessions; explicit exit closes once; fixture IPC only.");
    }
}
