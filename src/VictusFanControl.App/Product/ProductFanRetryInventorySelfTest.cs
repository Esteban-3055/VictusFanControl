using VictusFanControl.Recovery;
using System.Text.Json.Nodes;

namespace VictusFanControl.App;

internal static partial class WmiSharedGuardianSelfTest
{
    private static async Task RetainedLeaseInventoryAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"Victus-retained-inventory-"+Guid.NewGuid().ToString("N"));
        var common=Path.Combine(root,"common");var local=Path.Combine(root,"local");
        var shadow=Path.Combine(common,"WmiFanGui","lease.json");Directory.CreateDirectory(Path.GetDirectoryName(shadow)!);
        var client=new WmiFanGuiGuardianClient(fixture:true);
        try
        {
            await client.StartAsync(default);
            using(var source=new FileStream(Path.Combine(client.SessionDirectory,"fixture-lease.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var copy=File.Create(shadow))source.CopyTo(copy);
            var original=File.ReadAllText(shadow);
            ProductRecoveryRecord? Proof()=>client.RetainedFanLease(shadow,common);
            void Clear()=>ProductFanRetryInventory.EnsureClear(Proof,common,local);
            void Refused(Action action,string reason)
            {bool refused=false;try{action();}catch(IOException){refused=true;}Require(refused,reason);}
            // Exercise the real inventory scanner that was previously skipped
            // by isolated Forms. All lease files belong to this temporary tree.
            Require(ProductRecoveryInventory.ReadFans(common,local).Count==1,"Fixture did not reproduce the retained lease.");
            Clear();
            Refused(()=>ProductFanRetryInventory.EnsureClear(()=>null,common,local),"Unproven live lease was ignored.");
            client.BeginFanSession();client.PersistIntent(30);
            Refused(Clear,"Active fan authority was admitted as a released retained owner.");
            await client.ReleaseFanDomainAsync(default);Clear();
            foreach(var field in new[]{"OwnerPid","OwnerStartUtcTicks","GuardianPid","SessionDirectory","DirectEcProhibited"})
            {
                var data=JsonNode.Parse(original)!;
                data[field]=field=="SessionDirectory"?JsonValue.Create(root):field=="DirectEcProhibited"?JsonValue.Create(false):JsonValue.Create(1);
                File.WriteAllText(shadow,data.ToJsonString());
                Refused(Clear,"Changed retained identity was admitted: "+field);File.WriteAllText(shadow,original);
            }
            foreach(var marker in new[]{"write-intent.json","native-uncertain.signal"})
            {
                var path=Path.Combine(client.SessionDirectory,marker);File.WriteAllText(path,"fixture");
                Refused(Clear,"Pending/uncertain authority was ignored: "+marker);File.Delete(path);
            }
            var other=Path.Combine(common,"another","lease.json");Directory.CreateDirectory(Path.GetDirectoryName(other)!);File.WriteAllText(other,"{}");
            Refused(Clear,"An unrelated recovery record was ignored.");File.Delete(other);
            var orphan=Path.Combine(local,"FanWmi","gui","orphan","native-uncertain.signal");Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);File.WriteAllText(orphan,"{}");
            Refused(Clear,"An orphan native marker was ignored.");File.Delete(orphan);
            var calls=0;
            Refused(()=>ProductFanRetryInventory.EnsureClear(()=>++calls==1?Proof():null,common,local),"A changed lease proof was reused.");
            Clear();
            await client.ReleaseAsync(default);
            Refused(Clear,"An exited retained guardian was admitted.");
            Console.WriteLine("PASS suspend/resume retained WMI inventory: exact live shared lease accepted only without fan authority; active, foreign, changed, uncertain, orphan and exited evidence blocked; real temporary inventory and detached fixture guardian, zero hardware IO.");
        }
        finally{await client.DisposeAsync();}
    }
}
