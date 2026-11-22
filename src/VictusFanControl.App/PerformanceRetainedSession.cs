using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal sealed record PerformanceRetainedJournals(Guid? Cpu,Guid? Gpu)
{
    internal static PerformanceRetainedJournals Read()
    {
        var directory=PerformanceRecoveryPreview.DirectoryPath;
        return new(new JsonCpuPowerSessionJournal(Path.Combine(directory,"cpu-power-session.json"),CpuPowerProductDefaults.TargetProfileId).Load()?.SessionId,
            new JsonGpuClockSessionJournal(Path.Combine(directory,"gpu-clock-session.json"),CpuPowerProductDefaults.TargetProfileId).Load()?.SessionId);
    }
}

// Ephemeral confirmation only. A saved receipt never reacquires hardware.
internal sealed record PerformanceRetainedSession(int OwnerPid,long OwnerStart,int GuardianPid,long GuardianStart,
    Guid Nonce,PerformanceGuiSessionConfiguration? Configuration,Guid? Cpu,Guid? Gpu)
{
    internal static bool MatchesJournals(PerformanceGuardianResponse status,PerformanceRetainedJournals journals)=>
        (status.CpuEnabled?status.CpuSession is {} cpu&&cpu!=Guid.Empty&&cpu==journals.Cpu:status.CpuSession is null&&journals.Cpu is null)&&
        (status.GpuEnabled?status.GpuSession is {} gpu&&gpu!=Guid.Empty&&gpu==journals.Gpu:status.GpuSession is null&&journals.Gpu is null);
}
