using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

internal static partial class ProductRuntimePerformanceSelfTest
{
    private sealed class LifetimeFixture
    {
        internal readonly PerformanceGuardianClient Client;
        internal readonly Backend Backend=new();
        internal string? Report;
        internal int Launches;
        internal bool AlterJournal;
        internal LifetimeFixture()
        {
            Client=new("fixture://lifetime",start=>
            {
                start.ArgumentList[0]="--gui-fixture-session";Launches++;
                Report=start.ArgumentList[start.ArgumentList.IndexOf("--report")+1];return Process.Start(start);
            },Inventory);
        }
        private static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated lifetime inventory.");
        internal PerformanceRetainedJournals Journals()=>new(AlterJournal?Guid.NewGuid():Client.LastStatus?.CpuSession,Client.LastStatus?.GpuSession);
        internal ProductRuntime Create(ProductProfiles profiles,ProductRuntime? previous=null,Backend? backend=null)
        {
            var worker=new TelemetryWorker("fixture://lifetime",_=>ValueTask.FromResult(Sample()));
            worker.StateMachine.Transition(SystemState.Healthy,"Fresh lifetime fixture.");
            return new("fixture://lifetime",profiles,dependencies:new(Hardware(),backend??Backend,worker,Client,new Source(),Inventory,Journals),
                releasedRuntime:previous,preservePerformanceFromReleasedRuntime:previous is not null);
        }
        internal void CheckReport()
        {
            using var report=JsonDocument.Parse(File.ReadAllText(Report!));var r=report.RootElement;
            Require(Launches==1&&r.GetProperty("CpuHardwareWriteAttempts").GetInt32()==0&&r.GetProperty("GpuHardwareWriteAttempts").GetInt32()==0&&
                r.GetProperty("ReleaseCalls").GetInt32()==1&&r.GetProperty("FinalPhase").GetString()=="Stopped",
                "Retained lifetime launched another child, released more than once, touched hardware or failed cleanup.");
        }
    }
    internal static async Task<int> RunLifetimeChecksAsync()
    {
        try
        {
            foreach(var mask in new[]{(false,false),(true,false),(false,true),(true,true)})await RetainedOwnerAsync(mask.Item1,mask.Item2);
            await RejectedTransferAsync();await ExitDuringRetirementAsync();await LateReadAsync();
            Console.WriteLine("PASS: separate fan/performance lifetimes, exact sessions, retained background status, transfers, cancellation and one global release. Zero hardware IO.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static async Task RetainedOwnerAsync(bool cpu,bool gpu)
    {
        var fixture=new LifetimeFixture();var profiles=new ProductProfiles{CpuEnabled=cpu,GpuEnabled=gpu};
        var current=fixture.Create(profiles);
        try
        {
            await current.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
            var original=await fixture.Client.VerifyRetainedSessionAsync(fixture.Journals);
            Require(fixture.Client.StillOwns(original),"New session did not confirm exact process and journals.");
            foreach(var changed in new[]{original with{OwnerPid=original.OwnerPid+1},original with{OwnerStart=original.OwnerStart+1},
                original with{GuardianPid=original.GuardianPid+1},original with{GuardianStart=original.GuardianStart+1},original with{Nonce=Guid.NewGuid()},
                original with{Cpu=Guid.NewGuid()},original with{Gpu=Guid.NewGuid()}})
                Require(!fixture.Client.StillOwns(changed),"Changed retention identity was accepted.");
            for(var retry=0;retry<3;retry++)
            {
                var previous=current;
                previous.FenceLifecycle("Isolated fan interruption");
                await previous.RetireFanRuntimeAsync();
                Require(fixture.Client.LimitsActive&&!File.Exists(fixture.Report),"Fan retirement shut down the detached performance owner.");
                if(retry==0)
                {
                    var response=fixture.Client.LastStatus!.RequestId;
                    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while(fixture.Client.LastStatus?.RequestId==response)await Task.Delay(50,budget.Token);
                    Require(fixture.Client.LastStatusFresh,"Retained owner stopped being polled between fan runtimes.");
                }
                await previous.VerifyRetainedPerformanceAsync();
                current=fixture.Create(profiles,previous,new Backend());
                await previous.DisposeAsync(); // Old references cannot release the transferred owner.
                var retained=await fixture.Client.VerifyRetainedSessionAsync(fixture.Journals);
                Require(retained==original&&fixture.Client.LimitsActive&&fixture.Launches==1,
                    "A fan restart changed the process, selection or domain sessions.");
                Require(current.State.CpuState==(cpu?"Active":"Disabled")&&current.State.GpuState==(gpu?"ActiveUnverified":"Disabled"),
                    "Replacement runtime hid the independently active domains.");
                await current.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
            }
        }
        finally{await current.DisposeAsync();}
        Require(!fixture.Client.HasProcess,"Global exit did not close retained performance.");fixture.CheckReport();
        Console.WriteLine($"PASS: actual runtime kept CPU={cpu}, GPU={gpu} in the same detached owner through three fan replacements; exact sessions and one final release.");
    }
    private static async Task RejectedTransferAsync()
    {
        foreach(var reason in new[]{"journal-changed","global-close"})
        {
            var fixture=new LifetimeFixture();var profiles=new ProductProfiles();var runtime=fixture.Create(profiles);
            try
            {
                await runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);await runtime.RetireFanRuntimeAsync();
                if(reason=="journal-changed")fixture.AlterJournal=true;else await runtime.DisposeAsync();
                var rejected=false;
                try{_ = fixture.Create(profiles,runtime,new Backend());}catch(IOException){rejected=true;}
                Require(rejected,"A changed journal or global close still allowed transfer.");
            }
            finally{fixture.AlterJournal=false;await runtime.DisposeAsync();}
            fixture.CheckReport();
        }
        var status=new PerformanceGuardianResponse(1,Guid.NewGuid(),CpuPowerProductDefaults.TargetProfileId,true,"fixture","fixture","SessionEnabled",true,true,false);
        Require(!PerformanceRetainedSession.MatchesJournals(status,new(null,null)),"Missing session ID was accepted for an active domain.");
        Require(!PerformanceRetainedSession.MatchesJournals(status with{CpuSession=Guid.Empty},new(Guid.Empty,null)),"Empty session ID was accepted.");
        var id=Guid.NewGuid();Require(!PerformanceRetainedSession.MatchesJournals(status with{CpuSession=id},new(id,Guid.NewGuid())),"Unexpected disabled-domain journal was ignored.");
        Console.WriteLine("PASS: changed journals, stopped owner, missing/empty identity and unexpected records prevent retention; cleanup remains available.");
    }
    private static async Task ExitDuringRetirementAsync()
    {
        var fixture=new LifetimeFixture();var profiles=new ProductProfiles();var runtime=fixture.Create(profiles);
        try
        {
            await runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
            fixture.Backend.DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var retiring=runtime.RetireFanRuntimeAsync();
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while(fixture.Backend.Disposals==0)await Task.Delay(10,budget.Token);
            var closing=runtime.DisposeAsync().AsTask();fixture.Backend.DisposeGate.SetResult();
            var rejected=false;try{await retiring;}catch(IOException){rejected=true;}
            await closing;
            Require(rejected&&!fixture.Client.HasProcess&&fixture.Backend.Disposals==1,"Global close failed to veto retention or disposed local resources twice.");
        }
        finally{fixture.Backend.DisposeGate?.TrySetResult();await runtime.DisposeAsync();}
        fixture.CheckReport();Console.WriteLine("PASS: global exit arriving during fan retirement vetoes handoff and closes the detached owner once.");
    }
    private static async Task LateReadAsync()
    {
        var fixture=new LifetimeFixture();var profiles=new ProductProfiles();var previous=fixture.Create(profiles);ProductRuntime? next=null;var returned=false;
        try
        {
            await previous.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
            fixture.Backend.DisposeFailure=new WmiFanReadDrainPendingException(new IOException("Isolated pending read"),()=>returned?Task.CompletedTask:Task.FromException(new IOException("Still pending")));
            var blocked=false;try{await previous.RetireFanRuntimeAsync();}catch(AggregateException){blocked=true;}
            Require(blocked&&((IProductLateReadDrain)previous).HasPendingReadDrain&&fixture.Client.HasProcess,"Pending local read did not fence fan replacement or lost the independent owner.");
            var pending=false;try{await ((IProductLateReadDrain)previous).ConfirmReadDrainAsync();}catch(IOException){pending=true;}
            Require(pending,"An unreturned read was confirmed.");returned=true;
            await ((IProductLateReadDrain)previous).ConfirmReadDrainAsync();await previous.RetireFanRuntimeAsync();
            next=fixture.Create(profiles,previous,new Backend());await previous.DisposeAsync();
            Require(fixture.Backend.Disposals==1&&fixture.Client.LimitsActive,"Confirmed late read repeated native cleanup or released the transferred limits.");
        }
        finally{if(next is not null)await next.DisposeAsync();else await previous.DisposeAsync();}
        fixture.CheckReport();Console.WriteLine("PASS: a pending local read blocks replacement; exact read confirmation permits transfer without repeating cleanup.");
    }
}
