using System.Text.Json;

namespace VictusFanControl.Recovery;

internal static class ProductRecoveryInventorySelfTest
{
    internal static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-recovery-inventory-"+Guid.NewGuid().ToString("N"));
        var common=Path.Combine(root,"common");var local=Path.Combine(root,"local");Directory.CreateDirectory(common);
        void Require(bool ok,string message){if(!ok)throw new IOException(message);}
        void Write(string name,object value){var file=Path.Combine(common,name);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,JsonSerializer.SerializeToUtf8Bytes(value));}
        try
        {
            Require(ProductRecoveryInventory.ReadFans(common,local).Count==0,"Empty inventory failed.");
            Write("WmiFanGui/lease.json",new{OwnerPid=123,OwnerStartUtcTicks=1,GuardianPid=456,DirectEcProhibited=true,SessionDirectory=Path.Combine(root,"session")});
            Write("WmiFanExperiment/lease.json",new{Pid=789,Directory=Path.Combine(root,"experiment"),Utc=DateTimeOffset.UtcNow,Control=true,FirmwareRestorationVerified=false});
            Write("WatchdogM4/state/lease.json",new{SchemaVersion=2,TargetProfileId=ProductRecoveryInventory.Target,SessionId=Guid.NewGuid()});
            var inventory=ProductRecoveryInventory.ReadFans(common,local);
            Require(inventory.Count==3&&inventory.All(x=>x.Recoverable),"Known GUI/experiment/legacy leases were not inventoried.");
            Require(ProductRecoveryInventory.AllowsRemaining(inventory,inventory.Take(1)),"Already-resolved domains rejected.");
            Require(!ProductRecoveryInventory.AllowsRemaining(inventory,new[]{inventory[0] with{Sha256=new string('a',64)}}),"Changed lease allowed.");
            File.WriteAllText(Path.Combine(common,"WmiFanGui/lease.json"),"invalid");
            Require(ProductRecoveryInventory.ReadFans(common,local).Any(x=>x.Problem is not null),"Malformed lease treated as absent.");
            Write("unknown/lease.json",new{SchemaVersion=1});
            Require(ProductRecoveryInventory.ReadFans(common,local).Any(x=>x.Kind=="Unknown"),"Unknown old schema hidden.");
            var foreign=Path.Combine(local,"Performance","other-board");Directory.CreateDirectory(foreign);File.WriteAllText(Path.Combine(foreign,"gpu-clock-session.json"),"{}");
            Require(ProductRecoveryInventory.ReadFans(common,local).Any(x=>x.Problem?.Contains("otro equipo")==true),"Other-target journal hidden.");
            var orphan=Path.Combine(local,"FanWmi","gui","orphan");Directory.CreateDirectory(orphan);File.WriteAllText(Path.Combine(orphan,"native-inflight.json"),"{\"Pid\":123}");
            Require(ProductRecoveryInventory.ReadFans(common,local).Any(x=>x.Kind=="OrphanWmi"&&x.Recoverable),"Orphan native read hidden.");
            File.WriteAllText(Path.Combine(orphan,"write-intent.json"),"{}");
            Require(ProductRecoveryInventory.ReadFans(common,local).Any(x=>x.Kind=="OrphanWmi"&&!x.Recoverable),"Orphan write intent lost ownership refusal.");
            var selection=new ProductRecoverySelection(Guid.Empty,Guid.Empty,inventory);
            Require(selection.Pending&&!selection.PerformancePending,"Fan-only pending session missing.");
            var args=ProductRecoveryFansClient.BuildStart("guardian","modules","request",new string('b',64),"evidence",new(1,2,"sid"),2).ArgumentList;
            Require(args.Count==18&&args[15]=="--confirm-release-only"&&args[17]=="2","Fan recovery identity/explicit release contract changed.");
            var report=JsonSerializer.SerializeToElement(new{kind="VictusFanControl.ProductFanRecovery",target=ProductRecoveryInventory.Target,selected=inventory,succeeded=true,automaticStarted=false,normalFanTargets=false,failure=(string?)null});
            Require(ProductRecoveryFansClient.Evaluate(report,inventory,false,"evidence").Succeeded,"Valid recovery report refused.");
            Require(!ProductRecoveryFansClient.Evaluate(report,inventory,true,"evidence").Succeeded,"Remaining lease accepted.");
            Require(!ProductRecoveryFansClient.Evaluate(report,new[]{inventory[0]},false,"evidence").Succeeded,"Wrong selection accepted.");
            if(OperatingSystem.IsWindows())using(var me=System.Diagnostics.Process.GetCurrentProcess())
            {
                var owned=Path.Combine(local,"FanWmi","gui","current");Directory.CreateDirectory(owned);
                Write("WmiFanGui/lease.json",new{OwnerPid=me.Id,OwnerStartUtcTicks=me.StartTime.ToUniversalTime().Ticks,GuardianPid=456,DirectEcProhibited=true,SessionDirectory=owned});
                var marker=Path.Combine(owned,"native-inflight.json");File.WriteAllText(marker,JsonSerializer.Serialize(new{Pid=me.Id}));File.WriteAllText(Path.Combine(owned,"write-intent.json"),"{}");
                var active=ProductRecoveryInventory.ReadFans(common,local,ignoreCurrentOwner:true);
                Require(!active.Any(x=>x.Path==marker||x.Path==Path.GetFullPath(Path.Combine(common,"WmiFanGui/lease.json"))),"Own active native query misclassified as orphan/pending lease.");
                File.WriteAllText(Path.Combine(owned,"native-uncertain.signal"),"uncertain");
                Require(ProductRecoveryInventory.ReadFans(common,local,ignoreCurrentOwner:true).Any(x=>x.Kind=="FanGui"&&x.Path==Path.GetFullPath(Path.Combine(common,"WmiFanGui/lease.json"))),"Own uncertain lease hidden.");
            }
            Console.WriteLine("Recovery inventory: PASS (GUI/experiment/legacy, multiple/changed/malformed/foreign records, fan-only and exact report; zero hardware IO).");
        }
        finally{Directory.Delete(root,true);}
    }
}
