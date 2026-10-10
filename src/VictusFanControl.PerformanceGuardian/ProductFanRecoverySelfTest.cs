using System.Text.Json;
using VictusFanControl.Recovery;

namespace VictusFanControl.PerformanceGuardian;

internal static class ProductFanRecoverySelfTest
{
    internal static int Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-fan-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Require(bool ok,string message){if(!ok)throw new IOException(message);}
        try
        {
            if(OperatingSystem.IsWindows())
            {
                // Exercise the real P/Invoke and read-only OS query on CI and on
                // the packaged helper; simulated clocks alone miss DLL bindings.
                var first=ProductFanRecovery.ReadKernelUptime100ns();
                var actualBoot=ProductFanRecovery.ReadBoot();
                var last=ProductFanRecovery.ReadKernelUptime100ns();
                Require(first>0&&last>=first,"Windows kernel uptime is invalid or regressed.");
                Require(actualBoot<DateTimeOffset.UtcNow,"Windows boot query returned a future boot.");
                Console.WriteLine("Product fan recovery Windows boot clock: PASS (real API-set QueryInterruptTime and Win32_OperatingSystem agree; no fan/CPU/GPU hardware IO).");
            }
            var boot=DateTimeOffset.UtcNow.AddHours(-1);
            Require(ProductFanRecovery.KernelBootAgrees(boot,boot.AddHours(1),(ulong)TimeSpan.FromHours(1).Ticks),"Matching kernel boot refused.");
            Require(!ProductFanRecovery.KernelBootAgrees(boot.AddMinutes(55),boot.AddHours(1),(ulong)TimeSpan.FromHours(1).Ticks),"New user boot over old kernel allowed.");
            Require(!ProductFanRecovery.PreviousBoot(DateTimeOffset.UtcNow,boot),"Same boot allowed.");
            Require(!ProductFanRecovery.PreviousBoot(boot.AddSeconds(-30),boot),"Ambiguous boot timestamp allowed.");
            Require(ProductFanRecovery.PreviousBoot(boot.AddHours(-1),boot),"Previous boot blocked.");
            foreach(var mode in new[]{"no-intent","release","rejected","same-boot","previous-boot","changed","late-change","uncertain-release"})
            {
                var common=Path.Combine(root,mode);var dir=Path.Combine(common,"session");var evidence=Path.Combine(common,"evidence");Directory.CreateDirectory(dir);Directory.CreateDirectory(evidence);
                var path=Path.Combine(common,"lease.json");
                File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(new{OwnerPid=123,OwnerStartUtcTicks=456L,GuardianPid=789,DirectEcProhibited=true,SessionDirectory=dir}));
                var original=File.ReadAllBytes(path);var entry=new ProductRecoveryRecord(path,"FanGui",ProductRecoveryInventory.Hash(original));
                if(mode!="no-intent")File.WriteAllText(Path.Combine(dir,"write-intent.json"),"{\"Level\":30}");
                if(mode is "same-boot" or "previous-boot")
                {
                    var marker=Path.Combine(dir,"native-inflight.json");File.WriteAllText(marker,"{\"Pid\":123}");
                    if(mode=="previous-boot")File.SetLastWriteTimeUtc(marker,boot.AddHours(-1).UtcDateTime);
                }
                if(mode=="changed")File.AppendAllText(path," ");
                var calls=new List<int>();var failed=false;
                try
                {
                    ProductFanRecovery.ExecuteWmi(entry,evidence,boot,()=>{},r=>
                    {
                        calls.Add((int)r.CommandType);
                        if(mode=="late-change")File.AppendAllText(path," ");
                        if(mode=="uncertain-release") { File.WriteAllText(Path.Combine(dir,"native-inflight.json"),"{}");throw new IOException("Uncertain native return"); }
                        return mode=="rejected"&&r.CommandType==0x2E?1:0;
                    },_=>{});
                }
                catch(Exception){failed=true;}
                var shouldFail=mode is "rejected" or "same-boot" or "changed" or "late-change" or "uncertain-release";
                Require(failed==shouldFail,"Wrong completion: "+mode);
                Require(File.Exists(path)==shouldFail,"Lease retention failed: "+mode);
                Require(calls.All(x=>x is 0x2E or 0x1A),"Normal target allowed.");
                if(mode is "no-intent" or "same-boot" or "changed")Require(calls.Count==0,"Hardware call sent before admission: "+mode);
                if(mode is "release" or "previous-boot")Require(calls.SequenceEqual(new[]{0x2E,0x1A}),"Release sequence changed.");
                if(!shouldFail)Require(Directory.GetFiles(common,"lease.json.recovered-*").Single() is var archived&&File.ReadAllBytes(archived).SequenceEqual(original),"Original lease archive lost.");
            }
            foreach(var mode in new[]{"orphan-same-boot","orphan-previous-boot","orphan-intent"})
            {
                var dir=Path.Combine(root,mode);var evidence=Path.Combine(dir,"evidence");Directory.CreateDirectory(evidence);
                var path=Path.Combine(dir,"native-inflight.json");File.WriteAllText(path,"{\"Pid\":123}");
                if(mode!="orphan-same-boot")File.SetLastWriteTimeUtc(path,boot.AddHours(-1).UtcDateTime);
                if(mode=="orphan-intent")File.WriteAllText(Path.Combine(dir,"write-intent.json"),"{}");
                var entry=new ProductRecoveryRecord(path,"OrphanWmi",ProductRecoveryInventory.Hash(File.ReadAllBytes(path)));var failed=false;
                try{ProductFanRecovery.ExecuteOrphan(entry,evidence,boot,()=>{});}catch(IOException){failed=true;}
                Require(failed==(mode!="orphan-previous-boot")&&File.Exists(path)==failed,"Orphan restart/ownership fence failed: "+mode);
            }
            Console.WriteLine("Product fan recovery: PASS (no-intent, release/rejection, current/previous kernel boot, changed lease, late change, uncertain return and orphan reads/intents; fake IO only).");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{Directory.Delete(root,true);}
    }
}
