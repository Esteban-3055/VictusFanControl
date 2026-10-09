using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;
using VictusFanControl.OemShadow;

namespace VictusFanControl.PlatformThermalReplay;

public static class PhysicalExperimentSelfTest
{
    public static int Run(string? output=null)
    {
        int checks=0;
        void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
        void Refused(Action action,string why){bool refused=false;try{action();}catch(InvalidOperationException){refused=true;}Check(refused,why);}
        Check(ExperimentProtocol.TotalSeconds==2580,"43-minute protocol");
        Check(ExperimentProtocol.At(119).Controller=="firmware"&&ExperimentProtocol.At(120).Controller=="baseline","start boundary");
        Check(ExperimentProtocol.At(660).Controller=="both-retention"&&ExperimentProtocol.At(1200).Controller=="both-retention"&&
            ExperimentProtocol.At(1740).Controller=="baseline"&&ExperimentProtocol.At(2280).Controller=="firmware","ABBA and terminal boundaries");
        Check(ExperimentProtocol.At(240).Activity.StartsWith("Carga")&&ExperimentProtocol.At(480).Activity.StartsWith("Enfriamiento"),"workload instructions bounded");
        var fan=ProductProfiles.DefaultProfile(ProductPowerProfile.Ac).Fan;
        var config=Hp8C40AutomaticPolicy.Create(fan.BuildPolicy(),10);
        var start=DateTimeOffset.UnixEpoch;
        TelemetrySnapshot Sample(int seconds,double cpu=50,double gpu=40,double cpuPower=10,double gpuPower=8,double load=5)=>
            new(start.AddSeconds(seconds),"synthetic-i7-13700H",cpu,cpuPower,load,"synthetic-RTX4060",gpu,gpuPower,load,2500,2300)
            {
                CpuExpectedPhysicalCoreCount=14,CpuCoreTemperatures=Enumerable.Range(0,14).Select(i=>new CpuCoreTemperatureSample(i,i,i<6?"Performance":"Efficiency",cpu)).ToArray(),
                CpuFanSpeedLevel=25,GpuFanSpeedLevel=23,FanSampledAtUtc=start.AddSeconds(seconds),FanSampleAgeMilliseconds=0,FanRpmResolution=100,FanTelemetrySource="HP-WMI-ACPI-2D"
            };
        AdaptiveFanPolicyInput Input(TelemetrySnapshot s)=>new(s.Timestamp,s.CpuTemperatureC!.Value,s.CpuPackagePowerW!.Value,s.CpuLoadPercent!.Value,s.GpuTemperatureC!.Value,s.GpuPowerW!.Value,s.GpuLoadPercent!.Value)
            {CpuRawControlTemperatureC=s.CpuControlTemperatureC};
        output??=Path.Combine(Path.GetTempPath(),"vfc-physical-fixture-"+Guid.NewGuid().ToString("N"));
        using(var experiment=new PhysicalPlatformExperiment(output,fan))
        {
            var baseline=new AdaptiveFanInertiaPolicy(config,fan.Tuning);int previousStage=-1;int? lastLevel=null;int extraRows=0;
            for(int seconds=0;seconds<ExperimentProtocol.TotalSeconds;seconds++)
            {
                var stage=ExperimentProtocol.At(seconds);
                experiment.SetStage(stage);
                int within=seconds>=120?(seconds-120)%540:0;
                bool loaded=stage.Custom&&within>=120&&within<360;
                // Long platform cooldown after a reproducible synthetic workload.
                double platform=loaded?67:stage.Custom&&within>=360?67-(within-360)*.06:44;
                var at=start.AddSeconds(seconds);
                // Reproduce observer subsampling: the selected DTT epochs are
                // 3.1056265s apart, but a real 241.5788012s acquisition bridges them.
                long dttTicks=seconds switch{240 or 241=>2_395_000_000L,242=>2_415_788_012L,243=>2_426_056_265L,_=>seconds*TimeSpan.TicksPerSecond};
                experiment.SetSources(new(platform+8,at),new(50,at),new(48,at),new(platform,start.AddTicks(dttTicks)));
                // Also exercise Firmware requalification after a main-frame gap:
                // the host resets before consuming the still-fresh acquisitions.
                if(seconds is 2 or 3 or 4 or 242)continue;
                var snapshot=Sample(seconds,loaded?80:50,loaded?70:40,loaded?30:10,loaded?60:8,loaded?80:5);
                experiment.ObserveTelemetry(snapshot,"Ac");
                if(!stage.Custom)continue;
                if(stage.Index!=previousStage&&previousStage==-1)baseline.Reset();
                var input=Input(snapshot);var b=baseline.Evaluate(input);var a=experiment.Evaluate(input,b);
                experiment.EnsureDispatchAllowed(at,at.AddMilliseconds(100));
                Check(a.RawDemandLevel>=b.RawDemandLevel,"MAX keeps protected baseline raw demand");
                Check(a.ThermalOverride==b.ThermalOverride,"raw thermal parity");
                Check(a.EqualFanLevel is >=10 and <=50,"physical range");
                if(lastLevel.HasValue)Check(a.EqualFanLevel-lastLevel<=1&&lastLevel-a.EqualFanLevel<=1,"nonthermal switch preserves bounded step");
                if(a.EqualFanLevel>b.EqualFanLevel)extraRows++;
                var action=lastLevel is null?AdaptiveFanProductionActionKind.EnterCustomAndApply:lastLevel==a.EqualFanLevel?AdaptiveFanProductionActionKind.HoldCustom:AdaptiveFanProductionActionKind.ApplyChangedLevel;
                experiment.NoteApplied(new(AdaptiveFanProductionMode.Automatic,action,true,a.EqualFanLevel,a.RawDemandLevel,FanAuthority.Custom,"synthetic ACK; no hardware IO"),at);
                lastLevel=a.EqualFanLevel;previousStage=stage.Index;
            }
            Check(extraRows>0,"retention produces evidence beyond baseline");
            experiment.SetStage(ExperimentProtocol.At(ExperimentProtocol.TotalSeconds));
            Refused(()=>experiment.EnsureDispatchAllowed(start.AddSeconds(2279),start.AddSeconds(2280)),"final Firmware blocks dispatch");
            experiment.RecordHost("cleanup",new{succeeded=true,atUtc=start.AddSeconds(ExperimentProtocol.TotalSeconds)});
            experiment.Complete(new{succeeded=true,scope="synthetic-no-hardware"},protocolComplete:true);
            byte[] ReadLiveTrace()
            {
                // The writer permits readers; on Windows the reader must also
                // share the existing writer's write access. Keep production
                // FileShare.Read unchanged so other writers remain excluded.
                using var file=new FileStream(Path.Combine(output,"experiment.jsonl"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                using var copy=new MemoryStream();file.CopyTo(copy);return copy.ToArray();
            }
            var finalTrace=ReadLiveTrace();
            var finalSummary=File.ReadAllBytes(Path.Combine(output,"summary.json"));
            experiment.Close("Cierre de ventana");experiment.RecordHost("unexpected-after-completion",new{});
            experiment.ObserveTelemetry(Sample(ExperimentProtocol.TotalSeconds),"Ac");
            experiment.NoteApplied(new(AdaptiveFanProductionMode.Automatic,AdaptiveFanProductionActionKind.HoldCustom,true,50,50,FanAuthority.Custom,"late synthetic callback"),start.AddSeconds(ExperimentProtocol.TotalSeconds));
            experiment.Complete(new{succeeded=false});
            Check(ReadLiveTrace().SequenceEqual(finalTrace)&&
                File.ReadAllBytes(Path.Combine(output,"summary.json")).SequenceEqual(finalSummary)&&!experiment.Ready,
                "Completion is final; later UI close, telemetry, host callbacks and repeated completion cannot mutate evidence");
        }
        void Fixture(string name,Action<PhysicalPlatformExperiment> action)
        {using var e=new PhysicalPlatformExperiment(output+"-"+name,fan);action(e);}
        void Qualify(PhysicalPlatformExperiment e)
        {
            for(int i=0;i<2;i++){var at=start.AddSeconds(i);e.SetSources(new(55,at),new(50,at),new(50,at),new(50,at));e.ObserveTelemetry(Sample(i),"Ac");}
            Check(e.Ready,"preflight qualifies distinct epochs");e.SetStage(ExperimentProtocol.At(120));
        }
        Fixture("stale",e=>{Qualify(e);var s=Sample(2);e.ObserveTelemetry(s,"Ac");var b=new AdaptiveFanInertiaPolicy(config,fan.Tuning).Evaluate(Input(s));e.Evaluate(Input(s),b);
            Refused(()=>e.EnsureDispatchAllowed(s.Timestamp,start.AddSeconds(4)),"stale auxiliary input denies dispatch");});
        Fixture("stage",e=>{Qualify(e);var s=Sample(2);e.ObserveTelemetry(s,"Ac");var b=new AdaptiveFanInertiaPolicy(config,fan.Tuning).Evaluate(Input(s));e.Evaluate(Input(s),b);e.SetStage(ExperimentProtocol.At(660));
            Refused(()=>e.EnsureDispatchAllowed(s.Timestamp,s.Timestamp),"old stage cannot dispatch");});
        Fixture("missing",e=>{Qualify(e);e.SetSources(new(55,start.AddSeconds(2)),new(),new(),new(null,start.AddSeconds(2)));e.ObserveTelemetry(Sample(2),"Ac");Check(e.Failure is not null,"missing source permanently interrupts");});
        Fixture("nan",e=>{Qualify(e);e.SetSources(new(double.NaN,start.AddSeconds(2)),new(),new(),new(50,start.AddSeconds(2)));e.ObserveTelemetry(Sample(2),"Ac");Check(e.Failure is not null,"nonfinite source is retained as evidence and interrupts");});
        Fixture("source",e=>{Qualify(e);e.ObserveTelemetry(Sample(2),"Battery");Check(e.Failure is not null,"AC change interrupts");});
        Fixture("gap",e=>{Qualify(e);e.ObserveTelemetry(Sample(6),"Ac");Check(e.Failure is not null,"gap interrupts");});
        Fixture("future",e=>{e.SetSources(new(55,start.AddSeconds(5)),new(),new(),new(50,start.AddSeconds(5)));e.ObserveTelemetry(Sample(0),"Ac");Check(!e.Ready,"future values never join earlier snapshot");});
        Fixture("future-cache",e=>{Qualify(e);e.SetSources(new(80,start.AddSeconds(5)),new(),new(),new(75,start.AddSeconds(5)));e.ObserveTelemetry(Sample(2),"Ac");Check(e.Ready,"bounded prior epoch avoids future-join false interruption");});
        Console.WriteLine($"Physical platform experiment self-test: PASS ({checks} checks; ABBA, stage/age/source faults, trace completion; no hardware IO). Output: {output}");
        return checks;
    }
}
