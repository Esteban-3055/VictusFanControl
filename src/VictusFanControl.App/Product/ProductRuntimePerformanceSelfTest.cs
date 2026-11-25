using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static partial class ProductRuntimePerformanceSelfTest
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            foreach(var scenario in new[]{"partial","rejected","lost-reply","success"})await UpdateAsync(scenario);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    private sealed class Source : IPerformancePowerSourceReader
    {internal PerformancePowerSourceKind Current=PerformancePowerSourceKind.Ac;
        public PerformancePowerSourceObservation Read()=>new(true,Current,Current==PerformancePowerSourceKind.Ac?(byte)1:(byte)0,80,0,"ISOLATED_FIXTURE_SOURCE");}
    private sealed class Backend : IFanControlBackend
    {
        private bool _active;
        internal int ApplyCalls;
        internal int Disposals;
        internal TaskCompletionSource? DisposeGate;
        internal Exception? DisposeFailure;
        public string Name=>"coordinator-no-hardware";
        public bool CanWrite=>true;
        public FanBackendCapabilities Capabilities=>new("8C40",10,54,false);
        public ValueTask ProbeControlDependencyAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken token)=>ValueTask.FromResult(new FanBackendStatus(Name,true,_active,true,true,"no hardware"));
        public ValueTask EnterCustomModeAsync(CancellationToken token){_active=true;return ValueTask.CompletedTask;}
        public ValueTask ApplyAsync(FanCommand command,CancellationToken token){ApplyCalls++;return ValueTask.CompletedTask;}
        public ValueTask RestoreFirmwareAutoAsync(CancellationToken token){_active=false;return ValueTask.CompletedTask;}
        public async ValueTask DisposeAsync(){Disposals++;if(DisposeGate is not null)await DisposeGate.Task;if(DisposeFailure is not null)throw DisposeFailure;}
    }
    private static HardwareIdentity Hardware()=>new(Hp8C40TargetProfile.BoardManufacturer,Hp8C40TargetProfile.BoardProduct,
        Hp8C40TargetProfile.BoardVersion,Hp8C40TargetProfile.SystemManufacturer,Hp8C40TargetProfile.SystemProductName,
        Hp8C40TargetProfile.SystemSkuPrefix+"#AKH",Hp8C40TargetProfile.ValidatedBiosVersion);
    private static TelemetrySnapshot Sample()=>new(DateTimeOffset.UtcNow,"Intel Core i7-13700H",50,10,20,
        Hp8C40TargetProfile.ExpectedGpuName,40,10,10,2000,2000)
    {CpuExpectedPhysicalCoreCount=14,CpuCoreTemperatures=Enumerable.Range(0,14).Select(i=>new CpuCoreTemperatureSample(i,i,i<6?"Performance":"Efficiency",50)).ToArray()};
    private static async Task UpdateAsync(string scenario)
    {
        static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated coordinator inventory.");
        string? report=null;
        var client=new PerformanceGuardianClient("fixture://modules",start=>
        {
            start.ArgumentList[0]=scenario switch{"partial"=>"--gui-fixture-partial-update","rejected"=>"--gui-fixture-update-failure",
                "lost-reply"=>"--gui-fixture-lost-update-reply",_=>"--gui-fixture-session"};
            report=start.ArgumentList[start.ArgumentList.IndexOf("--report")+1];
            return Process.Start(start);
        },Inventory);
        var worker=new TelemetryWorker("fixture://modules",_=>ValueTask.FromResult(Sample()));
        worker.StateMachine.Transition(SystemState.Healthy,"Isolated fresh acquisitions.");
        var backend=new Backend();var original=new ProductProfiles{Functions=ProductFunctionPolicies.Legacy(new(true,true,false,false))};
        var runtime=new ProductRuntime("fixture://modules",original,dependencies:new(Hardware(),backend,worker,client,new Source(),Inventory));
        try
        {
            await runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,original);
            Require(runtime.State.FanMode=="Automatic"&&runtime.State.AutomaticResumeProfiles?.PerformanceConfiguration()==original.PerformanceConfiguration(),
                "The actual runtime did not establish its Automatic session against isolated IPC.");
            var active=runtime.State;
            var requested=original with{CpuEnabled=false,GpuEnabled=false,
                Ac=original.Ac with{CpuPl1Watts=28,CpuPl2Watts=38,GpuMaximumMHz=1100}};
            var failed=false;
            try{await runtime.ApplyPerformanceAsync(requested);}catch(Exception error) when(error is InvalidOperationException or OperationCanceledException or IOException){failed=true;}
            var released=original.PerformanceConfiguration() with{CpuEnabled=false,GpuEnabled=false};
            var mixed=released with{AcPl1Watts=28,AcPl2Watts=38};
            if(scenario=="success")
                Require(!failed&&!runtime.State.LifecycleBlocked&&runtime.State.FanMode=="Automatic"&&
                    runtime.State.AutomaticResumeProfiles?.PerformanceConfiguration()==requested.PerformanceConfiguration(),"Confirmed live update lost its resume preferences or interrupted Automatic.");
            else
            {
                Require(failed&&runtime.State.LifecycleBlocked&&runtime.State.FanMode=="Firmware","An incomplete update did not interrupt and release the fan mode.");
                if(scenario=="lost-reply")
                {
                    var resume=new ProductAutomaticResumption();resume.Arm();Require(resume.ConfirmActivation(),"Fixture could not arm Automatic resumption.");
                    resume.Observe(active,0);resume.Observe(runtime.State,1000);
                    Require(runtime.State.AutomaticResumptionBlocked&&runtime.State.AutomaticResumeProfiles is null&&!resume.Wanted,
                        "An unanswered update authorized unattended Automatic resumption from old limits.");
                    Require((await client.StatusAsync())?.Configuration==requested.PerformanceConfiguration(),"STATUS did not recover the actual committed values after reply loss.");
                    Require(runtime.State.AutomaticResumptionBlocked,"A later read silently cleared the explicit recovery requirement.");
                }
                else
                {
                    var expected=scenario=="partial"?mixed:released;
                    Require(client.AppliedConfiguration==expected,"An update failure fabricated configuration or rollback.");
                    Require(!runtime.State.AutomaticResumptionBlocked&&runtime.State.AutomaticResumeProfiles?.PerformanceConfiguration()==expected,
                        "R01 REPRODUCED: Automatic resumption retained old limits after an independently committed partial CPU/GPU update.");
                }
            }
            Require(original.CpuEnabled&&original.GpuEnabled&&backend.ApplyCalls==0,"Coordinator test changed input preferences or dispatched fan levels.");
        }
        finally{await runtime.DisposeAsync();}
        using var result=JsonDocument.Parse(File.ReadAllText(report!));
        Require(result.RootElement.GetProperty("CpuHardwareWriteAttempts").GetInt32()==0&&
            result.RootElement.GetProperty("GpuHardwareWriteAttempts").GetInt32()==0&&
            result.RootElement.GetProperty("FinalPhase").GetString()=="Stopped","Fixture cleanup or hardware isolation failed.");
        Console.WriteLine("PASS: actual ProductRuntime and real guardian IPC, "+scenario+" update, truthful resume preferences, fan lifecycle and confirmed cleanup. Zero hardware IO.");
    }
}
