using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class SelectableGuiPerformanceDomainsSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            var initial=new PerformanceGuiSessionConfiguration{CpuEnabled=false,GpuEnabled=false};
            var cpus=new List<Fake>();var gpus=new List<Fake>();
            GuiPerformanceDomain Factory(bool isCpu)
            {
                var fake=new Fake(isCpu);(isCpu?cpus:gpus).Add(fake);
                return new(fake,(_,_)=>fake.Updates++,fake,fake,()=>fake.Disposals++);
            }
            using var domains=new SelectableGuiPerformanceDomains(initial,_=>Factory(true),_=>Factory(false));
            domains.EnableAsync(false,false,PerformancePowerSourceKind.Ac,default).GetAwaiter().GetResult();
            Require(cpus.Count==0&&gpus.Count==0&&domains.Snapshot is{CpuState:"Disabled",GpuState:"Disabled"},"Empty GUI selection acquired a hardware domain.");
            var cpuOnly=initial with{CpuEnabled=true};domains.UpdateConfiguration(cpuOnly,PerformancePowerSourceKind.Ac);
            Require(cpus.Count==1&&gpus.Count==0&&cpus[0].Enables==1,"CPU-only enable initialized GPU.");
            var both=cpuOnly with{GpuEnabled=true};domains.UpdateConfiguration(both,PerformancePowerSourceKind.Ac);
            Require(gpus.Count==1&&cpus[0] is{Enables:1,Releases:0,Updates:0},"GPU enable changed the existing CPU owner.");
            ((ICpuPowerSourceTransitionSink)domains).HandleConfirmedSourceChange(PerformancePowerSourceKind.Battery);
            ((IGpuClockSourceTransitionSink)domains).HandleConfirmedSourceChange(PerformancePowerSourceKind.Battery);
            Require(cpus[0].Sources==1&&gpus[0].Sources==1,"Selected source callbacks were lost.");
            var gpuOnly=both with{CpuEnabled=false};domains.UpdateConfiguration(gpuOnly,PerformancePowerSourceKind.Battery);
            Require(cpus[0] is{Releases:1,Disposals:1}&&gpus[0] is{Enables:1,Releases:0,Updates:0},"CPU disable reset/reapplied GPU or failed to retire CPU.");
            ((ICpuPowerSourceTransitionSink)domains).HandleConfirmedSourceChange(PerformancePowerSourceKind.Ac);
            Require(cpus[0].Sources==1,"A disabled CPU received source Apply authority.");
            domains.UpdateConfiguration(initial,PerformancePowerSourceKind.Battery);
            Require(gpus[0] is{Releases:1,Disposals:1}&&domains.Snapshot is{CpuState:"Disabled",GpuState:"Disabled"},"Both-off failed to release independently.");
            domains.UpdateConfiguration(both,PerformancePowerSourceKind.Ac);
            Require(cpus.Count==2&&gpus.Count==2&&cpus[1].Enables==1&&gpus[1].Enables==1,"Explicit re-enable did not acquire fresh domain owners.");
            domains.ReleaseAsync(true,true,"DISPLAY_OFF",default).GetAwaiter().GetResult();
            Require(domains.Configuration==both&&domains.Snapshot is{CpuState:"Disabled",GpuState:"Disabled"},"Lifecycle release lost selected preferences or claimed active hardware.");
            domains.EnableAsync(true,true,PerformancePowerSourceKind.Battery,default).GetAwaiter().GetResult();
            Require(cpus.Count==3&&gpus.Count==3,"Display-On did not reacquire the latest selected domains.");
            cpus[^1].FailRelease=true;
            bool failed=false;
            try{domains.UpdateConfiguration(gpuOnly with{AcGpuMaximumMHz=1800},PerformancePowerSourceKind.Ac);}catch(AggregateException){failed=true;}
            Require(failed&&domains.Configuration.CpuEnabled&&domains.Configuration.AcGpuMaximumMHz==1800&&gpus[^1].Updates==1&&gpus[^1].Releases==0,
                "Partial failure fabricated CPU release, suppressed GPU update or reset its owner.");
            bool sourceRejected=false;
            try{domains.UpdateConfiguration(both,PerformancePowerSourceKind.Unknown);}catch(InvalidOperationException){sourceRejected=true;}
            Require(sourceRejected&&gpus[^1].Updates==1,"Unknown source authorized a domain update.");
            try{domains.ReleaseAsync(true,true,"TEST_EXIT",default).GetAwaiter().GetResult();}catch(AggregateException){}
            Require(gpus[^1].Releases==1,"Failed CPU cleanup suppressed independent GPU cleanup.");
            output.WriteLine("PASS selectable CPU/GPU domains: none/CPU/GPU/both, lazy hardware acquisition, independent off/on without touching the peer, source routing, fresh reacquisition, partial commits and independent failed cleanup; hardware IO=0.");
            return 0;
        }
        catch(Exception error){output.WriteLine(error);return 1;}
    }
    private sealed class Fake(bool cpu) : IGuardianDomainLifecycle,ICpuPowerSourceTransitionSink,IGpuClockSourceTransitionSink
    {
        internal int Enables,Releases,Updates,Sources,Disposals;
        internal bool FailRelease;
        private bool enabled;
        public GuardianDomainLifecycleSnapshot Snapshot=>new(Enables,Releases,cpu&&enabled,!cpu&&enabled,null,null,
            cpu?Enables+Releases:0,cpu?0:Enables+Releases,cpu&&enabled?"Active":"Disabled",!cpu&&enabled?"ActiveUnverified":"Disabled",null,null);
        public ValueTask EnableAsync(bool c,bool g,PerformancePowerSourceKind source,CancellationToken token)
        {if(c!=cpu||g==cpu)throw new InvalidOperationException("Wrong fake domain selection.");Enables++;enabled=true;return ValueTask.CompletedTask;}
        public ValueTask ReleaseAsync(bool c,bool g,string reason,CancellationToken token)
        {Releases++;if(FailRelease)throw new IOException("Synthetic CPU release failure.");enabled=false;return ValueTask.CompletedTask;}
        CpuPowerPresetTransitionResult ICpuPowerSourceTransitionSink.HandleConfirmedSourceChange(PerformancePowerSourceKind source)
        {Sources++;return new(CpuPowerPresetTransitionDisposition.EnabledPresetUnchanged,source,null,true,"FAKE_CPU");}
        GpuClockPresetTransitionResult IGpuClockSourceTransitionSink.HandleConfirmedSourceChange(PerformancePowerSourceKind source)
        {Sources++;return new(GpuClockPresetTransitionDisposition.EnabledPresetUnchanged,source,null,true,GpuClockSessionState.ActiveUnverified,"FAKE_GPU");}
    }
}
