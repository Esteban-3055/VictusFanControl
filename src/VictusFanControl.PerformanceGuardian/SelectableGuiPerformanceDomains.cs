using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal interface IGuardianSelectableDomains : IGuardianConfigurableDomains { }

internal sealed record GuiPerformanceDomain(IGuardianDomainLifecycle Lifecycle,
    Action<PerformanceGuiSessionConfiguration,PerformancePowerSourceKind> Update,
    ICpuPowerSourceTransitionSink? Cpu,IGpuClockSourceTransitionSink? Gpu,Action Dispose);

// Each domain is acquired lazily, released independently and recreated only after
// a confirmed release. Failed/uncertain owners remain available for explicit cleanup.
internal sealed class SelectableGuiPerformanceDomains : IGuardianDomainLifecycle,IGuardianSelectableDomains,
    ICpuPowerSourceTransitionSink,IGpuClockSourceTransitionSink,IDisposable
{
    private readonly PerformanceGuiSessionConfiguration initial;
    private readonly Func<PerformanceGuiSessionConfiguration,GuiPerformanceDomain> createCpu,createGpu;
    private GuiPerformanceDomain? cpu,gpu;
    private bool active,disposed;
    private int enables,releases,retiredCpuWrites,retiredGpuWrites;
    private PerformancePowerSourceKind? initialSource;
    private string? releaseReason;
    internal SelectableGuiPerformanceDomains(PerformanceGuiSessionConfiguration initial,
        Func<PerformanceGuiSessionConfiguration,GuiPerformanceDomain> createCpu,
        Func<PerformanceGuiSessionConfiguration,GuiPerformanceDomain> createGpu)
    {
        initial.Validate();this.initial=initial;this.createCpu=createCpu;this.createGpu=createGpu;
        Configuration=initial with{CpuEnabled=false,GpuEnabled=false};
    }
    public PerformanceGuiSessionConfiguration Configuration {get;private set;}
    public GuardianDomainLifecycleSnapshot Snapshot=>new(enables,releases,Configuration.CpuEnabled,Configuration.GpuEnabled,
        initialSource,releaseReason,retiredCpuWrites+(cpu?.Lifecycle.Snapshot.CpuHardwareWriteAttempts??0),
        retiredGpuWrites+(gpu?.Lifecycle.Snapshot.GpuHardwareWriteAttempts??0),cpu?.Lifecycle.Snapshot.CpuState??"Disabled",
        gpu?.Lifecycle.Snapshot.GpuState??"Disabled",cpu?.Lifecycle.Snapshot.CpuStatus,gpu?.Lifecycle.Snapshot.GpuStatus,
        gpu?.Lifecycle.Snapshot.RecoveryGpuSession);

    public ValueTask EnableAsync(bool cpuEnabled,bool gpuEnabled,PerformancePowerSourceKind source,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
        var selected=enables==0?initial:Configuration;
        if(active||cpuEnabled!=selected.CpuEnabled||gpuEnabled!=selected.GpuEnabled)
            throw new InvalidOperationException("GUI session selection differs from its validated launch configuration.");
        active=true;initialSource=source;enables++;
        Configuration=selected with{CpuEnabled=false,GpuEnabled=false};
        try{UpdateConfiguration(selected,source);}
        catch{Configuration=selected;throw;}
        return ValueTask.CompletedTask;
    }
    public void UpdateConfiguration(PerformanceGuiSessionConfiguration next,PerformancePowerSourceKind source)
    {
        ObjectDisposedException.ThrowIf(disposed,this);next.Validate();
        if(!active||source is not(PerformancePowerSourceKind.Ac or PerformancePowerSourceKind.Battery))
            throw new InvalidOperationException("Domain changes require a live session and confirmed source.");
        var failures=new List<Exception>();
        try{UpdateCpu(next,source);}catch(Exception error){failures.Add(new InvalidOperationException("CPU: "+error.Message,error));}
        try{UpdateGpu(next,source);}catch(Exception error){failures.Add(new InvalidOperationException("GPU: "+error.Message,error));}
        if(failures.Count!=0)throw new AggregateException("Domain update incomplete; actual committed selections are retained.",failures);
    }
    private void UpdateCpu(PerformanceGuiSessionConfiguration next,PerformancePowerSourceKind source)
    {
        var before=Configuration;
        if(!next.CpuEnabled)
        {
            if(cpu is not null)
            {
                cpu.Lifecycle.ReleaseAsync(true,false,"GUI_CPU_DISABLED",CancellationToken.None).GetAwaiter().GetResult();
                if(cpu.Lifecycle.Snapshot.CpuState!="Disabled")throw new InvalidOperationException("CPU release is not confirmed.");
                var writes=cpu.Lifecycle.Snapshot.CpuHardwareWriteAttempts;cpu.Dispose();retiredCpuWrites+=writes;cpu=null;
            }
        }
        else if(!before.CpuEnabled)
        {
            cpu??=createCpu(next);
            Configuration=WithCpu(Configuration,next);
            cpu.Lifecycle.EnableAsync(true,false,source,CancellationToken.None).GetAwaiter().GetResult();
            if(cpu.Lifecycle.Snapshot.CpuState!="Active")throw new InvalidOperationException("CPU enable is not confirmed.");
        }
        else if(cpu is null||cpu.Lifecycle.Snapshot.CpuState!="Active")throw new InvalidOperationException("CPU owner requires recovery.");
        else if(!SameCpu(before,next))cpu.Update(next,source);
        Configuration=WithCpu(Configuration,next);
    }
    private void UpdateGpu(PerformanceGuiSessionConfiguration next,PerformancePowerSourceKind source)
    {
        var before=Configuration;
        if(!next.GpuEnabled)
        {
            if(gpu is not null)
            {
                gpu.Lifecycle.ReleaseAsync(false,true,"GUI_GPU_DISABLED",CancellationToken.None).GetAwaiter().GetResult();
                if(gpu.Lifecycle.Snapshot.GpuState!="Disabled")throw new InvalidOperationException("GPU release is not confirmed.");
                var writes=gpu.Lifecycle.Snapshot.GpuHardwareWriteAttempts;gpu.Dispose();retiredGpuWrites+=writes;gpu=null;
            }
        }
        else if(!before.GpuEnabled)
        {
            gpu??=createGpu(next);
            Configuration=WithGpu(Configuration,next);
            gpu.Lifecycle.EnableAsync(false,true,source,CancellationToken.None).GetAwaiter().GetResult();
            if(gpu.Lifecycle.Snapshot.GpuState!="ActiveUnverified")throw new InvalidOperationException("GPU enable is not confirmed.");
        }
        else if(gpu is null||gpu.Lifecycle.Snapshot.GpuState!="ActiveUnverified")throw new InvalidOperationException("GPU owner requires recovery.");
        else if(!SameGpu(before,next))gpu.Update(next,source);
        Configuration=WithGpu(Configuration,next);
    }
    public async ValueTask ReleaseAsync(bool cpuEnabled,bool gpuEnabled,string reason,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();releaseReason=reason;releases++;
        var selected=Configuration;
        var failures=new List<Exception>();
        // Attempt all initialized owners, including an enable whose acknowledgement failed.
        try{UpdateCpu(Configuration with{CpuEnabled=false},initialSource??PerformancePowerSourceKind.Unknown);}
        catch(Exception error){failures.Add(error);}
        try{UpdateGpu(Configuration with{GpuEnabled=false},initialSource??PerformancePowerSourceKind.Unknown);}
        catch(Exception error){failures.Add(error);}
        // Lifecycle release keeps the selected mask for an authorized display-On
        // reacquisition. Actual hardware state is reported independently in Snapshot.
        Configuration=selected;
        if(failures.Count!=0)throw new AggregateException("Independent domain cleanup incomplete.",failures);
        active=false;await ValueTask.CompletedTask;
    }
    CpuPowerPresetTransitionResult ICpuPowerSourceTransitionSink.HandleConfirmedSourceChange(PerformancePowerSourceKind source)=>
        Configuration.CpuEnabled&&cpu?.Cpu is{} sink?sink.HandleConfirmedSourceChange(source):
            new(CpuPowerPresetTransitionDisposition.NoActiveOwnership,source,null,true,"CPU_LIMIT_DISABLED");
    GpuClockPresetTransitionResult IGpuClockSourceTransitionSink.HandleConfirmedSourceChange(PerformancePowerSourceKind source)=>
        Configuration.GpuEnabled&&gpu?.Gpu is{} sink?sink.HandleConfirmedSourceChange(source):
            new(GpuClockPresetTransitionDisposition.NoActiveSession,source,null,true,GpuClockSessionState.Disabled,"GPU_LIMIT_DISABLED");
    private static bool SameCpu(PerformanceGuiSessionConfiguration a,PerformanceGuiSessionConfiguration b)=>
        a.CpuEnabled==b.CpuEnabled&&a.AcPl1Watts==b.AcPl1Watts&&a.AcPl2Watts==b.AcPl2Watts&&a.BatteryPl1Watts==b.BatteryPl1Watts&&a.BatteryPl2Watts==b.BatteryPl2Watts;
    private static bool SameGpu(PerformanceGuiSessionConfiguration a,PerformanceGuiSessionConfiguration b)=>
        a.GpuEnabled==b.GpuEnabled&&a.AcGpuMaximumMHz==b.AcGpuMaximumMHz&&a.BatteryGpuMaximumMHz==b.BatteryGpuMaximumMHz;
    private static PerformanceGuiSessionConfiguration WithCpu(PerformanceGuiSessionConfiguration a,PerformanceGuiSessionConfiguration b)=>a with
        {CpuEnabled=b.CpuEnabled,AcPl1Watts=b.AcPl1Watts,AcPl2Watts=b.AcPl2Watts,BatteryPl1Watts=b.BatteryPl1Watts,BatteryPl2Watts=b.BatteryPl2Watts};
    private static PerformanceGuiSessionConfiguration WithGpu(PerformanceGuiSessionConfiguration a,PerformanceGuiSessionConfiguration b)=>a with
        {GpuEnabled=b.GpuEnabled,AcGpuMaximumMHz=b.AcGpuMaximumMHz,BatteryGpuMaximumMHz=b.BatteryGpuMaximumMHz};
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{cpu?.Dispose();}finally{gpu?.Dispose();}
        // No implicit restore/Reset: explicit release or exact-journal recovery owns cleanup.
    }
}
