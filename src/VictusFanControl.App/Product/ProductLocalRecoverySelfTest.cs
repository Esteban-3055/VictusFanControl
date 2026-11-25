using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Product;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

internal static partial class ProductRuntimePerformanceSelfTest
{
    internal static async Task<int> RunLocalRecoveryChecksAsync()
    {
        try
        {
            RetryLanePolicy();await FollowingSurvivesFanFailureAsync();
            await EmptyOwnershipProofAsync();
            foreach(var cpu in new[]{true,false})await RecoverOnlyFailedPerformanceAsync(cpu);
            await PendingFailureDoesNotStarvePeerAsync();
            await PartialUpdateFaultIsLocalAsync();
            ReadFailureDiagnostics();
            foreach(var manual in new[]{false,true})await CtgpLocalRecoveryAsync(manual);
            await CtgpLocalRecoveryAsync(false,cancelQueued:true);
            Console.WriteLine("PASS local recovery: linked intent survives fan-only faults; per-domain retry and peer identity; OFF wins; exact completed WMI cleanup; typed read diagnostics; no desktop or hardware IO.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static async Task EmptyOwnershipProofAsync()
    {
        await using var f=new RepairFixture(RepairProfiles());
        var absent=await f.Client.VerifyRetainedSessionAsync(()=>new(null,null));
        Require(f.Client.StillOwns(absent)&&!f.Client.StillOwns(absent with{OwnerPid=1})&&
            !f.Client.StillOwns(absent with{Nonce=Guid.NewGuid()})&&!f.Client.StillOwns(absent with{Cpu=Guid.NewGuid()}),
            "An empty owner receipt accepted a fabricated identity.");
    }
    private static void RetryLanePolicy()
    {
        Require(new ProductProfiles().FunctionPolicies() is{StopAutomaticOnCtgpFailure:false,StopAutomaticOnPerformanceFailure:false,ContinueLinkedFunctionsDuringFanRecovery:true}&&
            ProductFunctionPolicies.Legacy(new()) is{StopAutomaticOnCtgpFailure:true,StopAutomaticOnPerformanceFailure:true,ContinueLinkedFunctionsDuringFanRecovery:false},
            "Default fault isolation or explicit legacy option was lost.");
        var cpu=new ProductFunctionRetry();var gpu=new ProductFunctionRetry();
        cpu.Failed("CPU",0);gpu.Failed("GPU",0);
        Require(!cpu.TryBegin(9999,true)&&!cpu.TryBegin(10000,false)&&cpu.TryBegin(10000,true),"Retry skipped clock or prerequisites.");
        Require(gpu.State.Attempts==0&&!gpu.State.Running&&!cpu.TryBegin(10001,true),"Retry lanes interfered or ran concurrently.");
        cpu.Failed("CPU",10000);Require(!cpu.TryBegin(39999,true)&&cpu.TryBegin(40000,true),"Second backoff changed.");
        cpu.Failed("CPU",40000);Require(!cpu.TryBegin(99999,true)&&cpu.TryBegin(100000,true),"Third backoff changed.");
        cpu.Failed("CPU",100000);Require(!cpu.TryBegin(long.MaxValue,true),"Retry budget was unbounded.");
        gpu.WaitForConfirmation("unknown native completion");Require(!gpu.TryBegin(long.MaxValue,true),"Uncertain completion was retried.");
        cpu.Cancel();Require(!cpu.TryBegin(long.MaxValue,true)&&!cpu.State.Pending,"Explicit OFF retained queued retry.");
        var flapping=new ProductFunctionRetry();flapping.Failed("read",0);Require(flapping.TryBegin(10000,true),"Retry did not start.");
        flapping.Succeeded(10000);flapping.Failed("read again",11000);
        Require(flapping.State.Attempts==1&&!flapping.TryBegin(21000,true)&&flapping.TryBegin(41000,true),"Brief success reset a flapping retry budget.");
        flapping.Succeeded(41000);flapping.ObserveHealthy(101001,true);
        Require(flapping.State.Attempts==0,"Fresh stable operation did not reset the local budget.");
        Console.WriteLine("PASS separate retry clocks, prerequisite wait, three bounded attempts, uncertainty and OFF.");
    }
    private static async Task FollowingSurvivesFanFailureAsync()
    {
        var rules=RepairProfiles().FunctionPolicies() with{CpuLimits=new(ProductFunctionBinding.FollowAutomatic,true),
            GpuClocks=new(ProductFunctionBinding.FollowInternalFans,true),Ctgp=new(ProductFunctionBinding.FollowAutomatic,true),
            ExternalCooling=new(ProductFunctionBinding.FollowAutomatic,true),ExternalOutputMode=ProductExternalOutputMode.Fixed,ExternalFixedOutput=91};
        var p=RepairProfiles() with{Functions=rules};await using var f=new RepairFixture(p,wmi:true);
        await f.Runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,p);await f.Pump(3);f.Fresh();await f.Reconcile();
        var cpu=f.Client.LastStatus!.CpuSession;var gpu=f.Client.LastStatus.GpuSession;
        Require(cpu.HasValue&&gpu.HasValue&&f.Raw!.Ctgp is{Active:true},"Linked setup was not active.");
        f.Runtime.FenceFanRetry("Synthetic fan-only fault");
        await (Task)typeof(ProductRuntime).GetMethod("ResetInterruptedFanAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(f.Runtime,["Synthetic fan-only fault"] )!;
        await f.Reconcile();
        Require(f.Client.LastStatus!.CpuSession==cpu&&f.Client.LastStatus.GpuSession==gpu&&f.Raw!.Ctgp is{Active:true}&&
            f.Runtime.State.RequestedFanMode==ProductFunctionFanMode.Automatic,"A fan fault withdrew healthy linked peers.");
        var projected=f.Runtime.State with{Snapshot=Sample()};
        var exported=new TelemetryExportService(()=>projected,new MatrixMemory()).Capture(DateTimeOffset.UtcNow);
        Require(exported.IndependentExternal?.Mode=="Fixed","Fan-only fault withdrew the fixed external function.");
        await f.Runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Firmware,p);await f.Reconcile();
        Require(f.Client.AppliedConfiguration is{CpuEnabled:false,GpuEnabled:false}&&f.Raw!.Ctgp is{Active:false}&&
            f.Runtime.State.RequestedFanMode==ProductFunctionFanMode.Firmware,"An explicit Firmware command failed to withdraw followers.");
        Console.WriteLine("PASS fan-only fault keeps exact CPU/GPU sessions, cTGP and external fixed output; user Firmware still withdraws followers.");
    }
    private static async Task RecoverOnlyFailedPerformanceAsync(bool cpu)
    {
        var p=RepairProfiles();await using var f=new RepairFixture(p,wmi:true);
        await f.Runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,p);await f.Runtime.SetCtgpAsync(true);
        await f.Runtime.SetPerformanceDomainAsync(true,true,p);f.Fresh();await f.Runtime.SetPerformanceDomainAsync(false,true,p);
        var own=cpu?f.Client.LastStatus!.CpuSession:f.Client.LastStatus!.GpuSession;
        var peer=cpu?f.Client.LastStatus!.GpuSession:f.Client.LastStatus!.CpuSession;var automatic=f.Runtime.State.AutomaticSessionId;
        long now=0;f.Runtime.FunctionRecoveryClock=()=>now;
        typeof(ProductRuntime).GetMethod("RecordFunctionFailure",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(f.Runtime,
            [cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks,new IOException("Synthetic local channel failure")]);
        now=10000;f.Fresh();await f.Client.StatusAsync();await f.Reconcile();
        var current=cpu?f.Client.LastStatus!.CpuSession:f.Client.LastStatus!.GpuSession;
        // The detached fixture deliberately reuses synthetic per-domain IDs.
        // The real retry lane must run exactly once; the peer must not retry.
        Require(current.HasValue&&(cpu?f.Client.LastStatus!.GpuSession:f.Client.LastStatus!.CpuSession)==peer&&
            f.Runtime.State.FunctionRetries![(int)(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks)].TotalAttempts==1&&
            f.Runtime.State.FunctionRetries![(int)(cpu?ProductFunction.GpuClocks:ProductFunction.CpuLimits)].TotalAttempts==0&&
            f.Runtime.State.AutomaticSessionId==automatic&&f.Runtime.State.FanMode=="Automatic"&&f.Raw!.Ctgp is{Active:true}&&f.Launches==1,
            "Local recovery replaced a healthy peer, guardian or Automatic session.");
        Require(!f.Runtime.State.FunctionFaults.Get(cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks),"Recovered function remained faulted.");
        Console.WriteLine("PASS local "+(cpu?"CPU":"GPU")+" recovery restores only its selection; exact healthy peer, cTGP, fan intent and process preserved.");
    }
    private static async Task PendingFailureDoesNotStarvePeerAsync()
    {
        var p=RepairProfiles();await using var f=new RepairFixture(p);long now=0;f.Runtime.FunctionRecoveryClock=()=>now;
        f.RefuseLaunch=true;try{await f.Runtime.SetPerformanceDomainAsync(true,true,p);}catch(IOException){}
        Require(f.Runtime.State.FunctionFaults.CpuLimits,"Fixture did not fault CPU before launch.");
        f.RefuseLaunch=false;f.Fresh();await f.Runtime.SetPerformanceDomainAsync(false,true,p);
        var peer=f.Client.LastStatus!.GpuSession;now=10000;f.Fresh();await f.Client.StatusAsync();await f.Reconcile();
        Require(f.Client.AppliedConfiguration is{CpuEnabled:true,GpuEnabled:true}&&f.Client.LastStatus!.GpuSession==peer,"CPU retry blocked or replaced the healthy GPU.");
        typeof(ProductRuntime).GetMethod("RecordFunctionFailure",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(f.Runtime,[ProductFunction.CpuLimits,new IOException("Second fixture failure")]);
        await f.Runtime.SetPerformanceDomainAsync(true,false,p);now=100000;f.Fresh();await f.Reconcile();
        Require(!f.Runtime.State.FunctionRequests.CpuLimits&&f.Client.AppliedConfiguration is{CpuEnabled:false,GpuEnabled:true}&&f.Client.LastStatus!.GpuSession==peer,"Delayed retry overrode explicit CPU OFF.");
        Console.WriteLine("PASS pending failed CPU recovers beside active GPU; OFF cancels further retries.");
    }
    private static void ReadFailureDiagnostics()
    {
        byte[] policy=[0,1,1,0];bool fail=false;
        var control=new Hp8C40CtgpControl(r=>
        {
            if(fail&&r.CommandType==0x21)throw new COMException("",unchecked((int)0x80041001));
            if(r.CommandType==0x22)policy=r.Payload.ToArray();
            var data=r.CommandType==0x28?new byte[128]:policy.ToArray();if(r.CommandType==0x28)data[0]=200;
            return new(0,data);
        },()=>true,()=>{},()=>{});
        control.Apply(true);fail=true;try{control.Observe();}catch(Exception e){control.Fail(e,"Observe");}
        Require(control.Status.AllowsAutomaticRetry&&control.Status.Diagnostic is{HResult:unchecked((int)0x80041001),Operation:"Observe"}&&
            !string.IsNullOrWhiteSpace(control.Status.Failure)&&control.NeedsRestore,"An empty native message lost cause or restore obligation.");
        fail=false;control.Restore();control.Fail(new CtgpExternalPolicyChangedException(),"Observe");
        Require(control.Status.RequiresManualRecovery&&!control.Status.AllowsAutomaticRetry,"External policy interference was automatically reacquired.");
        Console.WriteLine("PASS empty COM read diagnostic retains HRESULT/type/operation; read retry requires cleanup, foreign policy stays latched.");
    }
    private static async Task PartialUpdateFaultIsLocalAsync()
    {
        var p=RepairProfiles() with{CpuEnabled=true,GpuEnabled=true};await using var f=new RepairFixture(p);
        f.FixtureEntryOverride="--gui-fixture-partial-update";
        await f.Runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,p);await f.Runtime.ApplyPerformanceAsync(p);
        var cpu=f.Client.LastStatus!.CpuSession;var gpu=f.Client.LastStatus.GpuSession;
        var changed=p with{Ac=p.Ac with{CpuPl1Watts=28,CpuPl2Watts=38,GpuMaximumMHz=1500}};
        bool failed=false;try{await f.Runtime.ApplyPerformanceAsync(changed);}catch(Exception e)when(e is IOException or InvalidOperationException){failed=true;}
        Require(failed&&!f.Runtime.State.FunctionFaults.CpuLimits&&f.Runtime.State.FunctionFaults.GpuClocks&&
            f.Client.LastStatus!.CpuSession==cpu&&f.Client.LastStatus.GpuSession==gpu&&f.Runtime.State.FanMode=="Automatic"&&
            f.Client.AppliedConfiguration is{AcPl1Watts:28,AcPl2Watts:38},"A partial GPU rejection faulted the confirmed CPU or stopped fan control.");
        Console.WriteLine("PASS partial combined update: CPU confirmation preserved; only rejected GPU enters recovery; Automatic and both owner IDs unchanged.");
    }
    private static async Task CtgpLocalRecoveryAsync(bool manual,bool cancelQueued=false)
    {
        var raw=new WmiFanGuiGuardianClient(fixture:true){RearmFixtureAfterCompletion=true};
        var source=new Source();var shared=new ProductWmiLifetime(raw,source);long now=0;shared.RecoveryClock=()=>now;
        var fanReleased=false;shared.CanRestartAfterFailure=()=>fanReleased;
        try
        {
            await raw.StartAsync(default);await raw.SetCtgpAsync(true,default);raw.BeginFanSession();raw.PersistIntent(30);
            var directory=raw.SessionDirectory;using var ready=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"ready.json")));
            using var child=Process.GetProcessById(ready.RootElement.GetProperty("GuardianPid").GetInt32());
            WmiFanGuiGuardianHost.RequestStop(directory,"FIXTURE_LOCAL_FAILURE");
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(15));await child.WaitForExitAsync(budget.Token);
            var reportPath=Path.Combine(directory,"guardian-report.json");var report=JsonNode.Parse(File.ReadAllText(reportPath))!;
            var failure=new CtgpStatus("RecoveryRequired",false,true,"Synthetic policy observation failure",DateTimeOffset.UtcNow)
                {FailureKind=manual?"ExternalPolicyChanged":"ObservationReadFailure"};
            report["SupervisionCtgp"]=JsonSerializer.SerializeToNode(failure);report["SupervisionFailure"]=failure.Failure;File.WriteAllText(reportPath,report.ToJsonString());
            typeof(ProductWmiLifetime).GetField("_ctgpWanted",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(shared,true);
            // Backend and lifetime can observe the same completion in either order.
            var receipt=await raw.ConfirmCompletedReleaseAsync(default);
            Require(await raw.ReleaseAsync(default)==receipt,"A second release observer changed the exact completion receipt.");
            await shared.ReconcileAsync();
            Require(receipt.GuardianLeaseRetired&&shared.CtgpFaulted==manual&&shared.RecoveryState.Pending!=manual,"Completed cleanup lost local fault classification.");
            if(manual){shared.EnsureTransferAllowed();Require(!raw.HasChild,"Manual policy fault restarted WMI.");return;}
            await shared.SetCtgpAsync(true,true,default);now=10000;await shared.ReconcileAsync();
            Require(!raw.HasChild&&shared.RecoveryState.Attempts==0,"cTGP restarted before fan cleanup confirmation.");
            fanReleased=true;
            if(cancelQueued)
            {
                var gate=(SemaphoreSlim)typeof(ProductWmiLifetime).GetField("_commands",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(shared)!;
                await gate.WaitAsync();Task recovery,off;
                try
                {
                    recovery=shared.ReconcileAsync();
                    Require(shared.RecoveryState.Running,"Fixture failed to queue the timed local retry.");
                    off=shared.SetCtgpAsync(false,false,default);
                }
                finally{gate.Release();}
                await Task.WhenAll(recovery,off);
                Require(!raw.HasChild&&!shared.CtgpWanted&&!shared.RecoveryState.Pending,"Queued local recovery overrode the later explicit OFF.");
                Console.WriteLine("PASS queued cTGP local recovery loses to a later OFF without starting a new guardian.");
                return;
            }
            await shared.ReconcileAsync();
            Require(raw.Ctgp is{Active:true,Managed:true}&&!shared.RecoveryState.Pending&&!shared.CtgpFaulted,"cTGP did not recover its own function after verified cleanup.");
            await shared.SetCtgpAsync(false,false,default);now=100000;await shared.ReconcileAsync();
            Require(raw.Ctgp is{Active:false}&&!shared.CtgpWanted,"cTGP retry overrode explicit OFF.");
            Console.WriteLine("PASS cTGP reads recover after exact completed receipt and fan cleanup; independent backoff, manual-fault transfer, OFF and fixture-only rearm.");
        }
        finally{await shared.DisposeAsync();}
    }
}
