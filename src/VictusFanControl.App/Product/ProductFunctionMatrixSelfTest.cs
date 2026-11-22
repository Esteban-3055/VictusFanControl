using System.Diagnostics;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

internal static partial class ProductRuntimePerformanceSelfTest
{
    private sealed class MatrixMemory : IPhysicalMemoryUsage {public double? ReadPercent()=>50;}
    private static async Task WholeFunctionMatrixAsync()
    {
        static PerformanceRecoveryPreview Inventory()=>new(false,"Isolated whole-function matrix.");
        for(var mask=0;mask<16;mask++)
        {
            var client=new PerformanceGuardianClient("fixture://function-matrix",start=>
            {start.ArgumentList[0]="--gui-fixture-session";return Process.Start(start);},Inventory);
            var source=new Source();var raw=new WmiFanGuiGuardianClient(fixture:true);var shared=new ProductWmiLifetime(raw,source);
            var profiles=new ProductProfiles{CpuEnabled=false,GpuEnabled=false,
                Functions=new ProductFunctionPolicies().Unlinked() with{ExternalOutputMode=ProductExternalOutputMode.Fixed,ExternalFixedOutput=87}};
            var worker=new TelemetryWorker("fixture://function-matrix",_=>ValueTask.FromResult(Sample()));
            worker.StateMachine.Transition(SystemState.Healthy,"Isolated whole-function snapshots.");
            var runtime=new ProductRuntime("fixture://function-matrix",profiles,
                dependencies:new(Hardware(),new Backend(),worker,client,source,Inventory,WmiLifetime:shared));
            try
            {
                if((mask&1)!=0){worker.PublishAcknowledgementSnapshot(Sample());await runtime.SetPerformanceDomainAsync(true,true,profiles);}
                if((mask&2)!=0){worker.PublishAcknowledgementSnapshot(Sample());await runtime.SetPerformanceDomainAsync(false,true,profiles);}
                if((mask&4)!=0)await runtime.SetCtgpAsync(true);
                await runtime.SetExternalCoolingRequestedAsync((mask&8)!=0);
                await runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
                using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while(runtime.State.FanAuthority!="Custom")
                {await worker.ProcessAndPublishSnapshotAsync(Sample(),budget.Token);await Task.Delay(100,budget.Token);}
                var cpu=client.LastStatus?.CpuSession;var gpu=client.LastStatus?.GpuSession;
                // Exercise the real immediate thermal handoff with synthetic
                // measurements. Neither the fake fan backend nor the detached
                // fixture domains can dispatch a hardware command.
                await worker.ProcessAndPublishSnapshotAsync(Sample() with{CpuTemperatureC=99},budget.Token);
                Require(runtime.State.LifecycleBlocked&&runtime.State.FanMode=="Firmware",$"Mask {mask}: fan interruption did not release internal authority.");
                Require((client.AppliedConfiguration?.CpuEnabled==true)==((mask&1)!=0)&&
                    (client.AppliedConfiguration?.GpuEnabled==true)==((mask&2)!=0)&&
                    client.LastStatus?.CpuSession==cpu&&client.LastStatus?.GpuSession==gpu,$"Mask {mask}: fan interruption changed exact CPU/GPU ownership.");
                Require((raw.Ctgp?.Active==true)==((mask&4)!=0),$"Mask {mask}: fan interruption changed independent cTGP.");
                var output=new TelemetryExportService(()=>runtime.State,new MatrixMemory()).Capture(DateTimeOffset.UtcNow);
                Require((output.IndependentExternal is{Mode:"Fixed",Duty:87})==((mask&8)!=0)&&!output.SolicitudValida,
                    $"Mask {mask}: external fixed control followed fan failure or fabricated fan authority.");
                Require(runtime.State.FunctionRequests==new ProductFunctionFlags((mask&1)!=0,(mask&2)!=0,(mask&4)!=0,(mask&8)!=0),
                    $"Mask {mask}: fan interruption lost independent intent.");
            }
            finally{await runtime.DisposeAsync();}
            Require(!client.HasProcess,$"Mask {mask}: global cleanup leaked CPU/GPU owner.");
        }
        Console.WriteLine("PASS all 16 function selections through real runtime, detached fixture IPC and simulated thermal fan handoff: exact CPU/GPU sessions, cTGP, external fixed output and intent preserved; global cleanup; zero hardware IO.");
    }
}
