using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal static class PerformanceRecoveryPreviewSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var directory=Path.Combine(Path.GetTempPath(),"VFC-Preview-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            require(!PerformanceRecoveryPreview.Read(directory).Pending,"Empty journals blocked a new session.");
            var cpuId=Guid.NewGuid();var gpuId=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
            var cpu=new JsonCpuPowerSessionJournal(Path.Combine(directory,"cpu-power-session.json"),CpuPowerProductDefaults.TargetProfileId);
            var gpu=new JsonGpuClockSessionJournal(Path.Combine(directory,"gpu-clock-session.json"),CpuPowerProductDefaults.TargetProfileId);
            cpu.Store(new(1,CpuPowerProductDefaults.TargetProfileId,cpuId,8,CpuPowerJournalPhase.Owned,
                new(18722037335818600,45,115,false),new(30,50),18719803952824560,null,
                new(CpuPowerConflictState.Inactive,0,5,false,null,null,0,0),null,now,now));
            var before=File.ReadAllBytes(cpu.Path);
            var preview=PerformanceRecoveryPreview.Read(directory);
            require(preview.Pending && preview.CpuSession==cpuId && preview.GpuSession is null && !preview.Instructions().Contains("-ExpectedGpuSession"),"Partial journals generated fabricated recovery identity.");
            gpu.Store(new(1,CpuPowerProductDefaults.TargetProfileId,gpuId,6,GpuClockJournalPhase.ActiveUnverified,new(210,1900),null,null,now,now));
            var gpuBefore=File.ReadAllBytes(gpu.Path);preview=PerformanceRecoveryPreview.Read(directory);
            require(preview.Pending && preview.CpuSession==cpuId && preview.GpuSession==gpuId && preview.Instructions().Contains("-ExpectedCpuSession '"+cpuId+"'") && preview.Instructions().Contains("-ExpectedGpuSession '"+gpuId+"'"),"Recovery preview lost exact journal IDs.");
            require(before.SequenceEqual(File.ReadAllBytes(cpu.Path)) && gpuBefore.SequenceEqual(File.ReadAllBytes(gpu.Path)),"Read-only preflight mutated journals.");
            File.WriteAllText(cpu.Path,"{ broken JSON }");var malformed=File.ReadAllBytes(cpu.Path);
            require(PerformanceRecoveryPreview.Read(directory).Pending && malformed.SequenceEqual(File.ReadAllBytes(cpu.Path)),"Malformed pending journal was ignored or changed.");
            File.Delete(cpu.Path);File.Delete(gpu.Path);
            require(!PerformanceRecoveryPreview.Read(directory).Pending,"Explicit external recovery remained blocked after journals were removed.");
        }
        finally {Directory.Delete(directory,true);}
    }
}
