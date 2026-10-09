using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class PerformanceGuiConfigurationUpdateSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            static void Require(bool valid,string message){if(!valid)throw new InvalidOperationException(message);}
            var initial=new PerformanceGuiSessionConfiguration();var next=initial with{AcPl2Watts=38,AcGpuMaximumMHz=1801};
            var domains=new RecordingGuardianDomainLifecycle();int cpu=0,gpu=0;bool failCpu=false,failGpu=false;
            var configured=new ConfiguredGuiPerformanceDomains(new ActiveDomains(domains),initial,
                (_,_)=>{cpu++;if(failCpu)throw new IOException("synthetic CPU failure");},
                (_,_)=>{gpu++;if(failGpu)throw new IOException("synthetic GPU failure");});
            configured.UpdateConfiguration(next,PerformancePowerSourceKind.Ac);
            Require(configured.Configuration==next&&cpu==1&&gpu==1,"successful update lost a domain");
            foreach(var bad in new[]{next with{AcPl2Watts=1},next with{CpuEnabled=false}})
            {try{configured.UpdateConfiguration(bad,PerformancePowerSourceKind.Ac);throw new Exception("invalid update accepted");}
                catch(ArgumentException){}catch(InvalidOperationException){}}
            Require(cpu==1&&gpu==1,"invalid update reached hardware sinks");
            failCpu=true;var partial=next with{AcPl2Watts=39,AcGpuMaximumMHz=1802};
            try{configured.UpdateConfiguration(partial,PerformancePowerSourceKind.Ac);throw new Exception("CPU failure hidden");}catch(AggregateException){}
            Require(configured.Configuration.AcPl2Watts==38&&configured.Configuration.AcGpuMaximumMHz==1802&&gpu==2,
                "CPU failure suppressed GPU or fabricated CPU commit");
            failCpu=false;failGpu=true;
            try{configured.UpdateConfiguration(partial with{AcGpuMaximumMHz=1803},PerformancePowerSourceKind.Ac);throw new Exception("GPU failure hidden");}catch(AggregateException){}
            Require(configured.Configuration.AcPl2Watts==39&&configured.Configuration.AcGpuMaximumMHz==1802,"GPU failure fabricated combined success");
            output.WriteLine("PASS live configuration validation and independent partial commits; no fabricated rollback");

            var reader=new SourceReader();var sourceListener=new SourceListener();var standbyListener=new StandbyListener();
            using var source=new GuardianPerformancePowerSourceRuntime(reader,new RecordingGuardianCpuSourceTransitionSink(),new RecordingGuardianGpuSourceTransitionSink(),sourceListener);
            using var standby=new GuardianModernStandbyLifecycleRuntime(source,domains,standbyListener);
            var prime=standby.Prime(true,true);domains.EnableAsync(true,true,prime.Observation.Source,CancellationToken.None).GetAwaiter().GetResult();
            standby.ActivateListener();int updates=0;
            standby.ExecuteConfigurationUpdate(s=>{Require(s==PerformancePowerSourceKind.Ac,"wrong source");updates++;});
            reader.Source=PerformancePowerSourceKind.Battery;
            try{standby.ExecuteConfigurationUpdate(_=>updates++);throw new Exception("unconfirmed source accepted");}catch(InvalidOperationException){}
            Require(updates==1,"unconfirmed source wrote presets");
            reader.Source=PerformancePowerSourceKind.Ac;standbyListener.Emit(GuardianModernStandbySignalKind.SessionDisplayOff);
            try{standby.ExecuteConfigurationUpdate(_=>updates++);throw new Exception("display Off accepted");}catch(InvalidOperationException){}
            Require(updates==1,"display/lifecycle fence wrote presets");
            output.WriteLine("PASS live update source and display/standby fences; fake IO only");
            return 0;
        }
        catch(Exception ex){output.WriteLine("Live performance update self-test: FAIL "+ex);return 1;}
    }
    private sealed class ActiveDomains(RecordingGuardianDomainLifecycle inner):IGuardianDomainLifecycle
    {
        public GuardianDomainLifecycleSnapshot Snapshot=>inner.Snapshot with{CpuState="Active",GpuState="ActiveUnverified"};
        public ValueTask EnableAsync(bool cpu,bool gpu,PerformancePowerSourceKind source,CancellationToken token)=>inner.EnableAsync(cpu,gpu,source,token);
        public ValueTask ReleaseAsync(bool cpu,bool gpu,string reason,CancellationToken token)=>inner.ReleaseAsync(cpu,gpu,reason,token);
    }
    private sealed class SourceReader:IPerformancePowerSourceReader
    {
        internal PerformancePowerSourceKind Source=PerformancePowerSourceKind.Ac;
        public PerformancePowerSourceObservation Read()=>new(true,Source,1,80,0,"FAKE");
    }
    private sealed class Lease:IDisposable{public void Dispose(){}}
    private sealed class SourceListener:IGuardianPowerSourceNotificationListenerFactory
    {public IDisposable Create(Action callback)=>new Lease();}
    private sealed class StandbyListener:IGuardianModernStandbyNotificationListenerFactory
    {
        private Action<GuardianModernStandbySignal>? _callback;
        public IDisposable Create(Action<GuardianModernStandbySignal> callback){_callback=callback;return new Lease();}
        internal void Emit(GuardianModernStandbySignalKind kind)=>_callback?.Invoke(new(kind,DateTimeOffset.UtcNow));
    }
}
