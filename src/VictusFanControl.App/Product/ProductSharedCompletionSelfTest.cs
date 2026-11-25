using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

internal static partial class WmiSharedGuardianSelfTest
{
    private static async Task SharedCompletionAfterExitAsync()
    {
        var raw=new WmiFanGuiGuardianClient(fixture:true);
        var source=new ProductCompletionSource();
        var shared=new ProductWmiLifetime(raw,source);
        var sends=0;var backend=new Hp8C40WmiFanControlBackend(shared.FanLease,_=>{sends++;return 0;},sharedGuardian:true);
        using var entered=new ManualResetEventSlim();using var finish=new ManualResetEventSlim();Task? held=null;
        try
        {
            await backend.EnterCustomModeAsync(default);await shared.SetCtgpAsync(true,true,default);
            await backend.ApplyAsync(new FanCommand(30,30,"fixture scoped completion"),default);
            var directory=raw.SessionDirectory;
            using var ready=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"ready.json")));
            using var child=Process.GetProcessById(ready.RootElement.GetProperty("GuardianPid").GetInt32());
            held=Task.Run(()=>
            {
                using var mutex=new Mutex(false,WmiFanExperimentBoundary.FixtureNativeMutexName(directory));mutex.WaitOne();
                try{entered.Set();if(!finish.Wait(TimeSpan.FromSeconds(10)))throw new IOException("Fixture mutex did not drain.");}
                finally{mutex.ReleaseMutex();}
            });
            Require(entered.Wait(TimeSpan.FromSeconds(5)),"Fixture failed to reserve its own native mutex.");
            WmiFanGuiGuardianHost.RequestStop(directory,"FIXTURE_COMPLETED_SHARED_EXIT");
            var before=sends;bool pending=false;
            try{await backend.RestoreFirmwareAutoAsync(default);}catch(IOException){pending=true;}
            Require(pending&&!child.HasExited&&sends==before,"Unreturned shared cleanup replayed native writes or invented completion.");
            finish.Set();await held;
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(15));await child.WaitForExitAsync(budget.Token);
            await backend.RestoreFirmwareAutoAsync(default);
            Require(backend.ReleaseEvidence is{GuardianLeaseRetired:true,ReleaseRequestAccepted:true,LegacyDefaultRequestAccepted:true}&&sends==before,
                "Completed shared cleanup stayed latched or repeated a native release.");
            using var report=JsonDocument.Parse(File.ReadAllText(backend.ReleaseEvidence!.ReportPath));
            var types=report.RootElement.GetProperty("FixtureRequests").EnumerateArray().Select(x=>x.GetProperty("CommandType").GetInt32()).ToArray();
            Require(types.Count(x=>x==0x2E)==1&&types.Count(x=>x==0x1A)==1,"Completed observation replayed guardian cleanup.");
            Require(await raw.ConfirmCompletedReleaseAsync(default)==backend.ReleaseEvidence&&
                await raw.ReleaseAsync(default)==backend.ReleaseEvidence,"Repeated completion observers lost the exact cached receipt.");
            var uncertainty=Path.Combine(directory,"native-uncertain.signal");File.WriteAllText(uncertainty,"fixture changed evidence");
            try
            {
                bool rejected=false;try{await raw.ConfirmCompletedReleaseAsync(default);}catch(IOException){rejected=true;}
                Require(rejected,"Cached completion ignored new native uncertainty.");
                rejected=false;try{await raw.ReleaseAsync(default);}catch(IOException){rejected=true;}
                Require(rejected,"Release reused cached completion despite changed native evidence.");
            }
            finally{File.Delete(uncertainty);}
            Console.WriteLine("PASS exact shared cleanup after an early failed wait: native work drains, valid completed receipt accepted, zero replay; detached fixture only.");
        }
        finally{finish.Set();if(held is not null)await held;try{await backend.DisposeAsync();}finally{await shared.DisposeAsync();}}
    }
    private sealed class ProductCompletionSource : VictusFanControl.Performance.IPerformancePowerSourceReader
    {
        public VictusFanControl.Performance.PerformancePowerSourceObservation Read()=>new(true,VictusFanControl.Performance.PerformancePowerSourceKind.Ac,1,80,0,"FIXTURE");
    }
}
