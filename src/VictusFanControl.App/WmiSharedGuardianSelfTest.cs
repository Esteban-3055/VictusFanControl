using System.Text.Json;
using VictusFanControl.Runtime;
using VictusFanControl.Hardware.Hp;
using System.Diagnostics;

namespace VictusFanControl.App;

internal static partial class WmiSharedGuardianSelfTest
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            await RetainedLeaseInventoryAsync();
            var client=new WmiFanGuiGuardianClient(fixture:true);
            await client.StartAsync(default);
            try
            {
                await client.SetCtgpAsync(true,default);
                var first=await client.ReleaseFanDomainAsync(default);
                Require(first.FanDomainReleased&&!first.GuardianLeaseRetired&&client.Ctgp is{Active:true},
                    "cTGP-only session was closed by a no-op fan release.");
                for(var i=0;i<3;i++)
                {
                    client.BeginFanSession();client.PersistIntent(30+i);
                    var released=await client.ReleaseFanDomainAsync(default);
                    Require(released.FanDomainReleased&&released.ReleaseRequestAccepted&&released.LegacyDefaultRequestAccepted&&
                        !released.GuardianLeaseRetired&&client.Ctgp is{Active:true}&&!File.Exists(Path.Combine(client.SessionDirectory,"write-intent.json")),
                        "Scoped fan release lost the cTGP owner, retained a released intent or fabricated completion.");
                    client.EnsureAlive();
                }
                var last=await client.ReleaseFanDomainAsync(default);
                Require(!last.ReleaseRequestAccepted&&!last.LegacyDefaultRequestAccepted&&last.FanDomainReleased,
                    "Repeated no-op release replayed native fan commands.");
                var all=await client.ReleaseAsync(default);
                Require(all.GuardianLeaseRetired,"Global release did not retire the shared supervisor.");
                using var report=JsonDocument.Parse(File.ReadAllText(all.ReportPath));
                var commands=report.RootElement.GetProperty("FixtureRequests").EnumerateArray().Select(r=>r.GetProperty("CommandType").GetInt32()).ToArray();
                Require(commands.Count(c=>c==0x2E)==3&&commands.Count(c=>c==0x1A)==3&&commands.Count(c=>c==0x22)==2&&
                    report.RootElement.GetProperty("CtgpRestored").GetBoolean(),
                    "Scoped release repeated fan cleanup, toggled cTGP between fan sessions or failed its final restore.");
            }
            finally{await client.DisposeAsync();}
            await RejectedActivationAsync(false);await RejectedActivationAsync(true);
            Console.WriteLine("PASS: actual detached shared WMI guardian keeps cTGP active through three fan-domain releases; cTGP-only/no-op release sends zero fan requests; no replay; global cleanup restores cTGP once. Refused and unsent-busy cTGP activation preserve the shared guardian. Fixture native calls only.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static async Task RejectedActivationAsync(bool busy)
    {
        var folder=Path.Combine(Path.GetTempPath(),"Victus-shared-refusal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        using var owner=Process.GetCurrentProcess();var ownerStart=owner.StartTime.ToUniversalTime().Ticks;var clock=Stopwatch.StartNew();
        using var stop=new CancellationTokenSource();
        var pulse=Task.Run(async()=>
        {try{while(true){WmiFanExperiment.WriteJson(Path.Combine(folder,"heartbeat.json"),new{Pid=owner.Id,ElapsedMs=clock.ElapsedMilliseconds});await Task.Delay(50,stop.Token);}}catch(OperationCanceledException)when(stop.IsCancellationRequested){}});
        var admissions=0;
        var host=WmiFanGuiGuardianHost.RunAsync(["fixture","--session-dir",folder,"--owner-pid",owner.Id.ToString(),"--owner-start",ownerStart.ToString()],true,
            fixtureCtgpQualification:()=>{if(!busy)throw new InvalidOperationException("Fixture qualification refused without acquisition.");if(admissions++==0)throw new WmiNativeSlotBusyException();});
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        async Task Wait(Func<bool> condition){while(!condition()){if(host.IsCompleted)throw new IOException("Shared guardian stopped during a component refusal.");await Task.Delay(20,budget.Token);}}
        try
        {
            await Wait(()=>File.Exists(Path.Combine(folder,"ready.json")));
            var command=new CtgpCommand(Guid.NewGuid(),owner.Id,ownerStart,true);WmiFanExperiment.WriteJson(Path.Combine(folder,"ctgp-command.json"),command);
            CtgpReply? reply=null;
            await Wait(()=>{reply=CtgpPublication.TryRead<CtgpReply>(Path.Combine(folder,"ctgp-reply.json"));return reply?.Id==command.Id;});
            Require(reply!.Status is{Managed:false,Active:false}&&reply.Status.State==(busy?"Waiting":"RecoveryRequired")&&!host.IsCompleted,
                "Unowned cTGP activation failure closed the shared guardian or fabricated ownership.");
            if(busy)
            {
                command=command with{Id=Guid.NewGuid()};WmiFanExperiment.WriteJson(Path.Combine(folder,"ctgp-command.json"),command);
                await Wait(()=>{reply=CtgpPublication.TryRead<CtgpReply>(Path.Combine(folder,"ctgp-reply.json"));return reply?.Id==command.Id;});
                Require(reply!.Status is{Active:true,Managed:true},"An unsent activation could not be safely retried.");
            }
        }
        finally{WmiFanGuiGuardianHost.RequestStop(folder,"FIXTURE_COMPLETE");await host.WaitAsync(TimeSpan.FromSeconds(10));stop.Cancel();await pulse;}
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"guardian-report.json")));
        var requests=report.RootElement.GetProperty("FixtureRequests").EnumerateArray().Select(r=>r.GetProperty("CommandType").GetInt32()).ToArray();
        Require(!requests.Any(t=>t is 0x2E or 0x1A)&&requests.Count(t=>t==0x22)==(busy?2:0),
            "Activation refusal changed fan state or replayed a policy setter.");
    }
    private static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
}
