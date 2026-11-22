using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

internal static partial class ProductRuntimePerformanceSelfTest
{
    internal static async Task<int> RunIndependentChecksAsync()
    {
        try
        {
            static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated independent inventory.");
            var launches=0;string? report=null;
            var client=new PerformanceGuardianClient("fixture://independent",start=>
            {start.ArgumentList[0]="--gui-fixture-session";launches++;report=start.ArgumentList[start.ArgumentList.IndexOf("--report")+1];return Process.Start(start);},Inventory);
            var source=new Source();var sample=Sample();
            var worker=new TelemetryWorker("fixture://independent",_=>ValueTask.FromResult(sample));
            worker.StateMachine.Transition(SystemState.Degraded,"Only unrelated fan telemetry is absent.");
            var backend=new Backend();var profiles=new ProductProfiles{CpuEnabled=false,GpuEnabled=false};
            var runtime=new ProductRuntime("fixture://independent",profiles,dependencies:new(Hardware(),backend,worker,client,source,Inventory));
            try
            {
                void Publish(VictusFanControl.Telemetry.TelemetrySnapshot value){sample=value;worker.PublishAcknowledgementSnapshot(value);}
                Require(!runtime.State.FunctionCoordinationExplicit,"Legacy coordination was silently replaced at startup.");
                await runtime.SetExternalCoolingRequestedAsync(false);
                Require(runtime.State.FunctionCoordinationExplicit&&!runtime.State.FunctionRequests.ExternalCooling,
                    "An explicit base-off command remained ignored by legacy export mode.");
                var offState=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow) with{FunctionCoordinationExplicit=runtime.State.FunctionCoordinationExplicit,
                    FunctionRequests=runtime.State.FunctionRequests,FunctionPolicies=runtime.State.FunctionPolicies};
                Require(!new TelemetryExportService(()=>offState,new MatrixMemory()).Capture(DateTimeOffset.UtcNow).SolicitudValida,
                    "Legacy base following retained a remote request after explicit off.");
                Publish(Sample() with{CpuTemperatureC=null,CpuCoreTemperatures=[]});
                await runtime.SetPerformanceDomainAsync(true,true,profiles);
                Require(runtime.State.FunctionRequests.CpuLimits&&!client.HasProcess,"A missing own sensor did not preserve intent without activation.");
                await runtime.ReleasePerformanceAsync();
                Require(!runtime.State.FunctionRequests.CpuLimits&&!client.HasProcess,"Global withdrawal failed to cancel an unstarted pending function.");
                Publish(Sample() with{CpuFanRpm=null,GpuFanRpm=null});
                await runtime.SetPerformanceDomainAsync(true,true,profiles);
                var cpuSession=client.LastStatus!.CpuSession;
                Require(client.AppliedConfiguration is{CpuEnabled:true,GpuEnabled:false}&&runtime.State.FanMode=="Firmware"&&backend.ApplyCalls==0,
                    "CPU activation required RPM, activated the GPU or changed fan mode.");
                Publish(Sample() with{CpuTemperatureC=null,CpuPackagePowerW=null,CpuExpectedPhysicalCoreCount=null,CpuCoreTemperatures=[],CpuFanRpm=null,GpuFanRpm=null});
                await runtime.SetPerformanceDomainAsync(false,true,profiles);
                var gpuSession=client.LastStatus!.GpuSession;
                Require(client.AppliedConfiguration is{CpuEnabled:true,GpuEnabled:true}&&client.LastStatus.CpuSession==cpuSession,
                    "GPU-only activation required CPU telemetry or recreated CPU ownership.");
                Publish(Sample() with{GpuName=null,GpuTemperatureC=null,GpuPowerW=null,CpuFanRpm=null,GpuFanRpm=null});
                var changed=profiles with{Ac=profiles.Ac with{CpuPl1Watts=28}};
                await runtime.SetPerformanceDomainAsync(true,true,changed);
                Require(client.AppliedConfiguration?.AcPl1Watts==28&&client.LastStatus?.GpuSession==gpuSession,
                    "CPU-only update needed GPU data or changed its session.");
                runtime.FenceLifecycle("Isolated power suspension");
                var blocked=false;try{await runtime.SetPerformanceDomainAsync(true,true,changed with{Ac=changed.Ac with{CpuPl1Watts=27}});}catch(InvalidOperationException){blocked=true;}
                Require(blocked,"Activation crossed the explicit suspension fence.");
                source.Current=PerformancePowerSourceKind.Unknown;Publish(Sample() with{Timestamp=DateTimeOffset.UtcNow.AddSeconds(-20),CpuFanRpm=null,GpuFanRpm=null});
                await runtime.SetPerformanceDomainAsync(true,false,profiles);
                Require(client.AppliedConfiguration is{CpuEnabled:false,GpuEnabled:true}&&client.LastStatus?.GpuSession==gpuSession,
                    "CPU withdrawal needed source/sensors or released the unrelated GPU.");
                await runtime.ReleasePerformanceAsync();
                Require(client.AppliedConfiguration is{CpuEnabled:false,GpuEnabled:false}&&runtime.State.FanMode=="Firmware"&&launches==1,
                    "Both-off withdrawal failed, replaced the owner or changed fan control.");
                runtime.ResumeTelemetry("Isolated power resume");source.Current=PerformancePowerSourceKind.Ac;
                worker.StateMachine.Transition(SystemState.Degraded,"Own CPU data has returned; fans remain unavailable.");
                Publish(Sample() with{CpuFanRpm=null,GpuFanRpm=null});
                await runtime.SetPerformanceDomainAsync(true,true,profiles);
                Require(client.AppliedConfiguration is{CpuEnabled:true,GpuEnabled:false}&&client.LastStatus?.CpuSession is not null,
                    "CPU did not recover independently after its own data returned.");
                Require(backend.ApplyCalls==0,"Independent limits dispatched a fan target.");
            }
            finally{await runtime.DisposeAsync();}
            using var result=JsonDocument.Parse(File.ReadAllText(report!));
            Require(result.RootElement.GetProperty("CpuHardwareWriteAttempts").GetInt32()==0&&result.RootElement.GetProperty("GpuHardwareWriteAttempts").GetInt32()==0,
                "Independent runtime fixture escaped hardware isolation.");
            await CoordinationAndSharedWmiAsync();
            await IndependentAutomaticSourcesAsync();
            await WholeFunctionMatrixAsync();
            await LateCtgpSelectionFaultAsync();
            ProfilePolicyRoundTrip();
            Console.WriteLine("PASS: actual runtime/IPC activate and update each domain using only its sensors; no RPM dependency; suspension blocks activation; sensor/source loss cannot block domain withdrawal; peers and fan mode preserved. Zero hardware IO.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void ProfilePolicyRoundTrip()
    {
        var path=Path.Combine(Path.GetTempPath(),"Victus-functions-"+Guid.NewGuid().ToString("N"),"profiles.json");
        ProductProfilesStore.Save(new(),path);var before=File.ReadAllBytes(path);
        var policies=new ProductFunctionPolicies().Independent() with{CpuLimits=new(ProductFunctionBinding.Independent,false,true)};
        ProductProfilesStore.Save(new(){Functions=policies},path);
        var after=ProductProfilesStore.Parse(File.ReadAllText(path));
        Require(after.Functions==policies&&Directory.GetFiles(Path.GetDirectoryName(path)!,"*.pre-functions-*.json").Single() is{} backup&&
            File.ReadAllBytes(backup).SequenceEqual(before),"Function preferences failed roundtrip or exact legacy backup.");
        Require(!ProductProfilesStore.Serialize(new()).Contains("\"functions\"",StringComparison.Ordinal),"Legacy profiles were silently rewritten with new functions.");
    }
    private static async Task CoordinationAndSharedWmiAsync()
    {
        static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated shared runtime.");
        var source=new Source();var client=new PerformanceGuardianClient("fixture://coordination",start=>
        {start.ArgumentList[0]="--gui-fixture-session";return Process.Start(start);},Inventory);
        var raw=new WmiFanGuiGuardianClient(fixture:true);var shared=new ProductWmiLifetime(raw,source);
        var policies=new ProductFunctionPolicies().Unlinked();
        var profiles=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,Functions=policies};
        TelemetryWorker? currentWorker=null;
        ProductRuntime Create(ProductRuntime? old=null,bool unavailableFans=false)
        {
            var worker=new TelemetryWorker("fixture://coordination",_=>ValueTask.FromResult(Sample()));
            currentWorker=worker;
            worker.StateMachine.Transition(SystemState.Healthy,"Isolated shared runtime.");
            worker.PublishAcknowledgementSnapshot(Sample());
            return new("fixture://coordination",profiles,releasedRuntime:old,preservePerformanceFromReleasedRuntime:old is not null,
                dependencies:new(Hardware(),new Backend(),worker,client,source,Inventory,
                    ()=>new(client.LastStatus?.CpuSession,client.LastStatus?.GpuSession),shared),
                fanControlUnavailableReason:unavailableFans?"Fixture writable fan backend unavailable.":null);
        }
        var runtime=Create();
        try
        {
            await runtime.SetCtgpAsync(true);
            Require(runtime.State.CtgpState=="Active"&&runtime.State.FanMode=="Firmware"&&!client.HasProcess,"Standalone cTGP required fans or created CPU/GPU control.");
            await runtime.SelectFanModeAsync(VictusFanControl.Control.Adaptive.AdaptiveFanProductionMode.Automatic,profiles);
            Require(runtime.State.FanMode=="Automatic"&&!client.HasProcess,"Independent Automatic still required a CPU/GPU owner.");
            await runtime.SelectFanModeAsync(VictusFanControl.Control.Adaptive.AdaptiveFanProductionMode.Firmware,profiles);
            Require(raw.Ctgp is{Active:true},"Stopping Automatic released independent cTGP.");
            await runtime.SetPerformanceDomainAsync(true,true,profiles);
            var cpu=client.LastStatus!.CpuSession;
            await runtime.ConfigureFunctionsAsync(policies with{CpuLimits=new(ProductFunctionBinding.FollowAutomatic,false)});
            Require(runtime.State.FunctionRequests.CpuLimits&&client.AppliedConfiguration?.CpuEnabled==false&&raw.Ctgp is{Active:true},
                "Following Automatic lost CPU intent or released independent cTGP.");
            currentWorker!.PublishAcknowledgementSnapshot(Sample());
            await runtime.ConfigureFunctionsAsync(policies);
            Require(client.AppliedConfiguration?.CpuEnabled==true&&client.LastStatus?.CpuState=="Active","Unlink did not reacquire the requested CPU domain.");
            var receipt=client.LastStatus!.CpuSession;
            for(var i=0;i<3;i++)
            {
                var old=runtime;old.FenceFanRetry("Isolated fan-only retry");await old.RetireFanRuntimeAsync();await old.VerifyRetainedPerformanceAsync();
                runtime=Create(old);await old.DisposeAsync();
                await shared.SetCtgpAsync(true,true,default);
                Require(raw.Ctgp is{Active:true,Managed:true}&&client.LastStatus?.CpuSession==receipt&&runtime.State.FunctionRequests.Ctgp,
                    "A fan-runtime replacement closed cTGP, changed CPU ownership or lost independent intent.");
            }
            var retired=runtime;await retired.RetireFanRuntimeAsync();runtime=Create(retired,true);await retired.DisposeAsync();
            currentWorker!.StateMachine.Transition(SystemState.Degraded,"Isolated fan backend failure.");
            currentWorker.PublishAcknowledgementSnapshot(Sample() with{CpuFanRpm=null,GpuFanRpm=null});
            await runtime.SetPerformanceDomainAsync(true,true,profiles with{Ac=profiles.Ac with{CpuPl1Watts=27}});
            Require(runtime.State.LifecycleBlocked&&client.AppliedConfiguration?.AcPl1Watts==27&&raw.Ctgp is{Active:true},
                "Unavailable fan backend prevented independent CPU control or released cTGP.");
            runtime.FenceLifecycle("Isolated suspension boundary");
            await shared.SetCtgpAsync(true,true,default);
            Require(raw.Ctgp is{Active:false,Managed:false},"A queued acquisition escaped the shared WMI system fence.");
            await runtime.ReleaseForLifecycleAsync("Isolated suspension boundary");
            Require(raw.Ctgp is{Active:false,Managed:false}&&runtime.State.FunctionRequests.Ctgp,
                "Suspension left independent cTGP acquired or erased its intent.");
            runtime.ResumeTelemetry("Isolated resume");await runtime.ConfigureFunctionsAsync(policies);
            Require(raw.Ctgp is{Active:true,Managed:true},"Independent cTGP did not requalify after explicit lifecycle release.");
            await runtime.SetCtgpAsync(false);
            Require(raw.Ctgp is{Managed:false,Active:false}&&client.LastStatus?.CpuSession==receipt,"cTGP withdrawal released CPU or failed.");
        }
        finally{await runtime.DisposeAsync();}
        Require(!client.HasProcess&&File.Exists(raw.LastReleasedReportPath),"Global close failed to release retained supervisors.");
        Console.WriteLine("PASS: real runtime independently acquires cTGP in Firmware; Automatic needs no limits in independent mode; follow/unlink retains intent; three fan-runtime transfers preserve cTGP and exact CPU session; global cleanup and exact migration backup.");
    }
    private static async Task IndependentAutomaticSourcesAsync()
    {
        static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated independent source transition.");
        var client=new PerformanceGuardianClient("fixture://independent-source",start=>
        {start.ArgumentList[0]="--gui-fixture-session";return Process.Start(start);},Inventory);
        var worker=new TelemetryWorker("fixture://independent-source",_=>ValueTask.FromResult(Sample()));
        var source=new Source();var backend=new Backend();var profiles=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,Functions=new ProductFunctionPolicies().Unlinked()};
        var runtime=new ProductRuntime("fixture://independent-source",profiles,dependencies:new(Hardware(),backend,worker,client,source,Inventory));
        try
        {
            worker.StateMachine.Transition(SystemState.Healthy,"Isolated independent source snapshots.");
            await runtime.SelectFanModeAsync(VictusFanControl.Control.Adaptive.AdaptiveFanProductionMode.Automatic,profiles);
            foreach(var target in new[]{PerformancePowerSourceKind.Battery,PerformancePowerSourceKind.Ac})
            {
                source.Current=target;using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(12));
                while(runtime.State.AppliedFanProfile!=target.ToString())
                {
                    Require(!runtime.State.LifecycleBlocked,"Independent fan source transition required a CPU/GPU confirmation: "+runtime.State.Failure);
                    await worker.ProcessAndPublishSnapshotAsync(Sample(),budget.Token);
                    await Task.Delay(300,budget.Token);
                }
                Require(!client.HasProcess&&runtime.State.FanMode=="Automatic","Independent fan profile transition activated limits or stopped Automatic.");
            }
        }
        finally{await runtime.DisposeAsync();}
        Console.WriteLine("PASS actual independent Automatic AC/Battery roundtrip without CPU/GPU owner or fabricated confirmation; fan native IO simulated.");
    }
    private static async Task LateCtgpSelectionFaultAsync()
    {
        static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated late cTGP selection.");
        var client=new PerformanceGuardianClient("fixture://late-ctgp",start=>{start.ArgumentList[0]="--gui-fixture-session";return Process.Start(start);},Inventory);
        var source=new Source();var raw=new WmiFanGuiGuardianClient(fixture:true);var shared=new ProductWmiLifetime(raw,source);
        var policies=new ProductFunctionPolicies().Unlinked() with{StopAutomaticOnCtgpFailure=true};
        var profiles=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,Functions=policies};
        var worker=new TelemetryWorker("fixture://late-ctgp",_=>ValueTask.FromResult(Sample()));
        worker.StateMachine.Transition(SystemState.Healthy,"Isolated late cTGP selection.");
        var runtime=new ProductRuntime("fixture://late-ctgp",profiles,dependencies:new(Hardware(),new Backend(),worker,client,source,Inventory,WmiLifetime:shared));
        try
        {
            await runtime.SelectFanModeAsync(VictusFanControl.Control.Adaptive.AdaptiveFanProductionMode.Automatic,profiles);
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(runtime.State.FanAuthority!="Custom")
            {await worker.ProcessAndPublishSnapshotAsync(Sample(),budget.Token);await Task.Delay(100,budget.Token);}
            var selection=runtime.SetCtgpAsync(true);
            while(!selection.IsCompleted)
            {
                await worker.ProcessAndPublishSnapshotAsync(Sample(),budget.Token);
                Require(!runtime.State.LifecycleBlocked,"Pending late cTGP selection was mistaken for a lost confirmation.");
                await Task.Delay(20,budget.Token);
            }
            await selection;
            Require(raw.Ctgp is{Active:true}&&!shared.CtgpChanging,"Late cTGP activation did not finish cleanly.");
            // Substitute only the fixture's observation publication. Native
            // policy and all subsequent cleanup remain the fake host's own.
            WmiFanExperiment.WriteJson(Path.Combine(raw.SessionDirectory,"ctgp-status.json"),
                new VictusFanControl.Hardware.Hp.CtgpStatus("RecoveryRequired",false,true,"Fixture late policy loss.",DateTimeOffset.UtcNow));
            await worker.ProcessAndPublishSnapshotAsync(Sample(),budget.Token);
            Require(runtime.State.LifecycleBlocked&&runtime.State.FanMode=="Firmware"&&runtime.State.CtgpFaulted,
                "Stop-on-cTGP-failure ignored a cTGP selection made after Automatic entry.");
        }
        finally{await runtime.DisposeAsync();}
        Console.WriteLine("PASS late cTGP selection honors its stop-Automatic-on-failure option; changing selection is distinguished from confirmed/lost ownership; native policy simulated.");
    }
}
