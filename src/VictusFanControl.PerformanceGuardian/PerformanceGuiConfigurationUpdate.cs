using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal interface IGuardianConfigurationRuntime
{
    void ExecuteConfigurationUpdate(Action<PerformancePowerSourceKind> update);
}

internal interface IGuardianConfigurableDomains
{
    PerformanceGuiSessionConfiguration Configuration { get; }
    void UpdateConfiguration(PerformanceGuiSessionConfiguration configuration, PerformancePowerSourceKind source);
}

/// <summary>Retains independent committed presets; no release/re-enable or new baseline.</summary>
internal sealed class ConfiguredGuiPerformanceDomains(IGuardianDomainLifecycle inner,
    PerformanceGuiSessionConfiguration initial, Action<PerformanceGuiSessionConfiguration,PerformancePowerSourceKind>? updateCpu,
    Action<PerformanceGuiSessionConfiguration,PerformancePowerSourceKind>? updateGpu) : IGuardianDomainLifecycle, IGuardianConfigurableDomains
{
    public PerformanceGuiSessionConfiguration Configuration { get; private set; } = initial;
    public GuardianDomainLifecycleSnapshot Snapshot => inner.Snapshot;
    public ValueTask EnableAsync(bool cpuEnabled,bool gpuEnabled,PerformancePowerSourceKind source,CancellationToken token)
    {
        if(cpuEnabled!=Configuration.CpuEnabled||gpuEnabled!=Configuration.GpuEnabled)
            throw new InvalidOperationException("GUI domain selection differs from the launch configuration.");
        return inner.EnableAsync(cpuEnabled,gpuEnabled,source,token);
    }
    public ValueTask ReleaseAsync(bool cpuEnabled,bool gpuEnabled,string reason,CancellationToken token)=>inner.ReleaseAsync(cpuEnabled,gpuEnabled,reason,token);
    public void UpdateConfiguration(PerformanceGuiSessionConfiguration next,PerformancePowerSourceKind source)
    {
        next.Validate();
        if(source is not (PerformancePowerSourceKind.Ac or PerformancePowerSourceKind.Battery)||
            next.CpuEnabled!=Configuration.CpuEnabled||next.GpuEnabled!=Configuration.GpuEnabled||
            Configuration.CpuEnabled&&Snapshot.CpuState!="Active"||Configuration.GpuEnabled&&Snapshot.GpuState!="ActiveUnverified")
            throw new InvalidOperationException("Update requires unchanged domains, live ownership and a known source.");
        var failures=new List<Exception>();
        if(!Configuration.CpuEnabled) Configuration=Configuration with{AcPl1Watts=next.AcPl1Watts,AcPl2Watts=next.AcPl2Watts,
            BatteryPl1Watts=next.BatteryPl1Watts,BatteryPl2Watts=next.BatteryPl2Watts};
        if(!Configuration.GpuEnabled) Configuration=Configuration with{AcGpuMaximumMHz=next.AcGpuMaximumMHz,BatteryGpuMaximumMHz=next.BatteryGpuMaximumMHz};
        if(Configuration.CpuEnabled)
        {
            try
            {
                if(updateCpu is null)throw new InvalidOperationException("CPU update unsupported.");
                updateCpu(next,source);
                Configuration=Configuration with{AcPl1Watts=next.AcPl1Watts,AcPl2Watts=next.AcPl2Watts,
                    BatteryPl1Watts=next.BatteryPl1Watts,BatteryPl2Watts=next.BatteryPl2Watts};
            }
            catch(Exception ex){failures.Add(new InvalidOperationException("CPU: "+ex.Message,ex));}
        }
        if(Configuration.GpuEnabled)
        {
            try
            {
                if(updateGpu is null)throw new InvalidOperationException("GPU update unsupported.");
                updateGpu(next,source);
                Configuration=Configuration with{AcGpuMaximumMHz=next.AcGpuMaximumMHz,BatteryGpuMaximumMHz=next.BatteryGpuMaximumMHz};
            }
            catch(Exception ex){failures.Add(new InvalidOperationException("GPU: "+ex.Message,ex));}
        }
        if(failures.Count>0)throw new AggregateException("Preset update incomplete; committed values are reported per domain.",failures);
    }
}
