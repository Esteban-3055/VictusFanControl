using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal static class PerformanceGuiIntegrationSelfTest
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
            static PerformanceRecoveryPreview NoRecovery() => new(false,"Isolated IPC fixture inventory.");
            new PerformanceGuiSessionConfiguration().Validate();
            foreach (var invalid in new[]
            {
                new PerformanceGuiSessionConfiguration { AcPl1Watts = 115, AcPl2Watts = 115 },
                new PerformanceGuiSessionConfiguration { BatteryPl1Watts = 20, BatteryPl2Watts = 15 },
                new PerformanceGuiSessionConfiguration { TargetProfileId = "88F8" }
            })
            {
                var rejected = false;
                try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Invalid presets accepted.");
            }
            var request = new PerformanceGuardianRequest(1, Guid.NewGuid(), CpuPowerProductDefaults.TargetProfileId, Guid.NewGuid(), "STATUS");
            var response = new PerformanceGuardianResponse(1, request.RequestId, request.TargetProfileId, true, "OK", "", "Enabled", true, true, true,
                "Active", "ActiveUnverified", PowerSource: "Ac");
            foreach (var invalid in new[] { response with { RequestId = Guid.NewGuid() }, response with { ProtocolVersion = 99 }, response with { TargetProfileId = "88F8" } })
            {
                var rejected = false;
                try { PerformanceGuardianClient.ValidateResponse(request, invalid); } catch (IOException) { rejected = true; }
                Require(rejected, "Invalid response identity accepted.");
            }
            var xml = WindowsStartupRegistration.BuildXml("S-1-5-21-123", @"C:\Program Files\Victus & Control\app.exe", @"C:\Modules & Tools");
            var document = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            Require(WindowsStartupRegistration.HasCurrentContract(document,"S-1-5-21-123",@"C:\Program Files\Victus & Control\app.exe",@"C:\Modules & Tools"),"Current startup contract rejected.");
            foreach(var triggerAsAccount in new[]{false,true})
            foreach(var principalAsAccount in new[]{false,true})
            {
                var normalized=new XDocument(document);
                if(triggerAsAccount)normalized.Descendants(ns+"LogonTrigger").Single().Element(ns+"UserId")!.Value=@"fixture-pc\tester";
                if(principalAsAccount)normalized.Descendants(ns+"Principal").Single().Element(ns+"UserId")!.Value=@"FIXTURE-PC\Tester";
                Require(WindowsStartupRegistration.HasCurrentContract(normalized,"S-1-5-21-123",@"C:\Program Files\Victus & Control\app.exe",@"C:\Modules & Tools",@"FIXTURE-PC\Tester"),
                    "Scheduler SID/account normalization rejected the same OS identity.");
                foreach(var foreign in new[]{@"OTHER-PC\Tester",@"FIXTURE-PC\Other","Tester","S-1-5-21-456",""})
                foreach(var element in new[]{"LogonTrigger","Principal"})
                {
                    var altered=new XDocument(normalized);altered.Descendants(ns+element).Single().Element(ns+"UserId")!.Value=foreign;
                    Require(!WindowsStartupRegistration.HasCurrentContract(altered,"S-1-5-21-123",@"C:\Program Files\Victus & Control\app.exe",@"C:\Modules & Tools",@"FIXTURE-PC\Tester"),
                        "Foreign/ambiguous startup identity accepted: "+element);
                }
            }
            Console.WriteLine("PASS startup task roundtrip: SID/current-account normalization for trigger and principal; case-insensitive canonical identity; other domains/users/SIDs and bare names refused; no task registration.");
            foreach(var mutation in new[]{("DisallowStartIfOnBatteries","true"),("StopIfGoingOnBatteries","true"),("ExecutionTimeLimit","PT1H"),
                ("AllowHardTerminate","true"),("Arguments","--modules-dir wrong"),("RunLevel","LeastPrivilege"),("Command",@"C:\old.exe"),
                ("Interval","PT1S"),("Count","4")})
            {
                var altered=new XDocument(document);altered.Descendants(ns+mutation.Item1).Single().Value=mutation.Item2;
                Require(!WindowsStartupRegistration.HasCurrentContract(altered,"S-1-5-21-123",@"C:\Program Files\Victus & Control\app.exe",@"C:\Modules & Tools"),"Broken startup contract was shown enabled: "+mutation.Item1);
            }
            Require(document.Descendants(ns + "Command").Single().Value == @"C:\Program Files\Victus & Control\app.exe", "Startup escaping.");
            Require(document.Descendants(ns + "UserId").Count() == 2 &&
                document.Descendants(ns + "LogonType").Single().Value == "InteractiveToken" &&
                document.Descendants(ns + "RunLevel").Single().Value == "HighestAvailable" &&
                document.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S" &&
                document.Descendants(ns + "Arguments").Single().Value.StartsWith("--start-minimized --modules-dir"), "Startup semantic contract.");
            Console.WriteLine("PASS: CPU/GPU configuration rejection, reply identity, startup XML escaping/session contract.");
            var receipt=new PerformanceRecoveryHandoffReceipt(1,CpuPowerProductDefaults.TargetProfileId,Guid.NewGuid(),100,200,300,400,Guid.NewGuid(),true,true,true);
            bool Matches(PerformanceRecoveryHandoffReceipt r)=>r.Matches(receipt.TargetProfileId,receipt.SessionNonce,100,200,300,400);
            Require(Matches(receipt),"Valid retirement receipt rejected.");
            foreach(var invalid in new[]{receipt with{SchemaVersion=2},receipt with{TargetProfileId="other"},receipt with{SessionNonce=Guid.NewGuid()},
                receipt with{OwnerPid=101},receipt with{OwnerStartUtcTicks=201},receipt with{GuardianPid=301},receipt with{GuardianStartUtcTicks=401},
                receipt with{GpuSession=Guid.Empty},receipt with{SourceStopped=false},receipt with{CpuDisabled=false},receipt with{GpuRecoveryRequired=false}})
                Require(!Matches(invalid),"Altered retirement receipt authorized handoff.");

            var fixtureMode = "--gui-fixture-session";
            Process? launched = null;
            using var observers=new FixtureMutexObservers();
            string? reportPath = null;
            var client = new PerformanceGuardianClient(Path.GetTempPath(), start =>
            {
                start.ArgumentList[0] = fixtureMode;
                reportPath = start.ArgumentList[start.ArgumentList.IndexOf("--report") + 1];
                var nonce=Guid.Parse(start.ArgumentList[start.ArgumentList.IndexOf("--nonce")+1]);
                observers.Retain("Local\\VFC.GuiFixture."+nonce.ToString("N"));
                launched = Process.Start(start);
                return launched;
            },recoveryPreview:NoRecovery);
            var configuration = new PerformanceGuiSessionConfiguration { AcPl1Watts = 20, AcPl2Watts = 40 };
            try
            {
                var idle=configuration with{CpuEnabled=false,GpuEnabled=false};
                Require((await client.EnableAsync(idle)).Ok&&client.LimitsActive&&client.LastStatus is{CpuState:"Disabled",GpuState:"Disabled"},
                    "Initial no-limit GUI session was rejected or claimed active hardware.");
                var enabled = await client.UpdateAsync(configuration);
                Require(enabled.Ok && client.LimitsActive && client.AppliedConfiguration == configuration, "GUI enable state.");
                Require((await client.StatusAsync())?.CpuState == "Active", "GUI status round trip.");
                var rejected = false;
                try { await client.EnableAsync(configuration); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Concurrent replacement session accepted.");
                var guardianPid=launched!.Id;
                var expanded=configuration with{AcPl1Watts=80,AcPl2Watts=80};
                Require((await client.UpdateAsync(expanded)).Ok&&client.AppliedConfiguration==expanded,
                    "GUI/Guardian IPC rejected or clipped a PL1 request above 44 W.");
                var updated=configuration with{AcPl2Watts=38,AcGpuMaximumMHz=1800};
                Require((await client.UpdateAsync(updated)).Ok&&client.AppliedConfiguration==updated&&client.LimitsActive&&launched.Id==guardianPid,
                    "Live IPC update replaced owner or failed to confirm presets.");
                Require((await client.StatusAsync())?.Configuration==updated,"STATUS lost live committed presets.");
                foreach(var selection in new[]{(false,true),(true,false),(false,false),(true,true)})
                {
                    var selected=updated with{CpuEnabled=selection.Item1,GpuEnabled=selection.Item2};
                    Require((await client.UpdateAsync(selected)).Ok&&client.AppliedConfiguration==selected&&launched.Id==guardianPid,
                        "Independent domain selection replaced the Guardian owner or was rejected.");
                    var selectedStatus=await client.StatusAsync();
                    Require(selectedStatus?.CpuState==(selection.Item1?"Active":"Disabled")&&selectedStatus.GpuState==(selection.Item2?"ActiveUnverified":"Disabled"),
                        "Independent selection reported false active/disabled state.");
                }
                Require((await client.UpdateAsync(updated)).Ok&&client.HasProcess,"Repeated unchanged update lost the owner.");
                var ipcGate=(SemaphoreSlim)typeof(PerformanceGuardianClient).GetField("_gate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
                await ipcGate.WaitAsync();
                var admissions=0;
                var queuedConfiguration=updated with{AcPl2Watts=37};
                var queued=client.UpdateAsync(queuedConfiguration,ct=>{ct.ThrowIfCancellationRequested();admissions++;return Task.CompletedTask;});
                Require(!queued.IsCompleted&&admissions==0&&client.LimitsActive&&client.AppliedConfiguration==updated,
                    "Waiting on IPC began an update or invalidated the previous active limits.");
                ipcGate.Release();
                Require((await queued).Ok&&admissions==1&&client.AppliedConfiguration==queuedConfiguration,
                    "Queued update did not admit once after the transport became available.");
                var cancelledBeforeWrite=false;
                try{await client.UpdateAsync(updated,_=>throw new OperationCanceledException("Synthetic cancelled admission before dispatch."));}
                catch(OperationCanceledException){cancelledBeforeWrite=true;}
                Require(cancelledBeforeWrite&&client.LimitsActive&&client.AppliedConfiguration==queuedConfiguration,
                    "Cancelled pre-dispatch update changed committed limits or lost the healthy owner.");
                Console.WriteLine("PASS: IPC queue waiting does not pause control; cancelled pre-dispatch update preserves active limits; no hardware IO.");
                Console.WriteLine("PASS: real GUI IPC live presets, same process, STATUS readback and domain-selection fence; zero hardware writes.");
                await client.CloseAsync();
                Require(!client.HasProcess && !client.LimitsActive, "GUI shutdown leaked owner.");
                using var report = JsonDocument.Parse(File.ReadAllText(reportPath!));
                Require(report.RootElement.GetProperty("CpuHardwareWriteAttempts").GetInt32() == 0 &&
                    report.RootElement.GetProperty("GpuHardwareWriteAttempts").GetInt32() == 0 &&
                    report.RootElement.GetProperty("ReleaseCalls").GetInt32() == 1 &&
                    report.RootElement.GetProperty("FinalPhase").GetString() == "Stopped", "Fixture touched hardware / failed release.");
                Console.WriteLine("PASS: real GUI IPC HELLO/ENABLE/STATUS/SHUTDOWN; existing unowned mutex admitted; immutable preferences; cleanup; zero hardware writes.");
                // Close arriving while launch/enable is pending waits for the operation and releases once.
                var enabling = client.EnableAsync(configuration);
                var closing = client.CloseAsync();
                await Task.WhenAll(enabling, closing);
                Require(!client.HasProcess, "Close while starting leaked Guardian.");
                Console.WriteLine("PASS: close while GUI session starts serializes and releases.");
                fixtureMode = "--gui-fixture-update-failure";
                await client.EnableAsync(configuration);
                rejected=false;
                try{await client.UpdateAsync(configuration with{AcPl2Watts=38});}catch(InvalidOperationException){rejected=true;}
                Require(rejected&&client.HasProcess&&client.AppliedConfiguration==configuration&&!client.LimitsActive,
                    "Failed IPC update fabricated new presets, retired owner or success.");
                Require((await client.StatusAsync())?.Configuration==configuration&&client.LimitsActive,
                    "STATUS did not recover prior committed fixture presets after rejected update.");
                await client.CloseAsync();
                Console.WriteLine("PASS: rejected live IPC update retains committed configuration and owner; STATUS and normal cleanup remain available.");
                fixtureMode = "--gui-fixture-enable-failure";
                rejected = false;
                try { await client.EnableAsync(configuration); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected && !client.LimitsActive, "Rejected domain enable created active status.");
                await client.CloseAsync();
                Require(!client.HasProcess, "Rejected enable leaked Guardian.");
                Console.WriteLine("PASS: domain enable rejection remains inactive; reconnect/SHUTDOWN releases the transport owner.");
                fixtureMode="--gui-fixture-unknown-release";
                await client.EnableAsync(configuration);
                rejected=false;try{await client.CloseAsync();}catch(IOException){rejected=true;}
                Require(rejected&&client.HasProcess&&client.DrainedGpuSession is null,"Unknown release error retired the owner or fabricated handoff.");
                await client.CloseAsync(); // The no-hardware fixture succeeds on its second explicit request.
                Console.WriteLine("PASS: unknown release failure retains owner and cannot hand off recovery.");
                fixtureMode="--gui-fixture-release-failure";
                await client.EnableAsync(configuration);
                rejected=false;try{await client.CloseAsync();}catch(IOException){rejected=true;}
                Require(rejected&&!client.HasProcess&&client.DrainedGpuSession is not null&&!client.LimitsActive,
                    "Returned failure did not wait for exit or was called released/active.");
                using var drainReport=JsonDocument.Parse(File.ReadAllText(reportPath!));
                Require(drainReport.RootElement.GetProperty("ExitReason").GetString()=="CLIENT_RECOVERY_DRAINED_JOURNALS_RETAINED"&&
                    drainReport.RootElement.GetProperty("ReleaseCalls").GetInt32()==1&&
                    drainReport.RootElement.GetProperty("FinalPhase").GetString()!="Stopped",
                    "Failed release was retried or reported as Stopped.");
                rejected=false;try{await client.EnableAsync(configuration);}catch(InvalidOperationException){rejected=true;}
                Require(rejected&&!client.HasProcess,"Drained but unrecovered client enabled a new session.");
                Console.WriteLine("PASS: authenticated failed-release reply plus child exit permits exact-session handoff; no reset retry or false release; zero hardware IO.");
            }
            finally { if (client.HasProcess) await client.CloseAsync(); }
            var lostClient=new PerformanceGuardianClient(Path.GetTempPath(),start=>
            {start.ArgumentList[0]="--gui-fixture-lost-release-reply";return Process.Start(start);},recoveryPreview:NoRecovery);
            await lostClient.EnableAsync(configuration);
            var lostRejected=false;try { await lostClient.CloseAsync(); } catch(IOException) { lostRejected=true; }
            Require(lostRejected&&!lostClient.HasProcess&&lostClient.DrainedGpuSession is not null&&!lostClient.LimitsActive,
                "Lost release ACK remained stuck or fabricated confirmed GPU release.");
            Console.WriteLine("PASS lost release ACK: real IPC/host durable receipt plus actual child exit; exact nonce/target/parent/child/start identity; eleven altered receipts rejected; no reset retry or false release.");
            // A started process exiting with an error must remain unresolved, rather than look Disabled.
            Process? failedOwner=null;
            var failedClient=new PerformanceGuardianClient(Path.GetTempPath(), _=>
            {
                var start=new ProcessStartInfo("cmd.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
                start.ArgumentList.Add("/c");start.ArgumentList.Add("exit 23");failedOwner=Process.Start(start)!;
                if(!failedOwner.WaitForExit(5000))throw new TimeoutException("Fatal-start fixture did not exit.");return failedOwner;
            },recoveryPreview:NoRecovery);
            try
            {
                bool refused=false;try{await failedClient.EnableAsync(configuration);}catch(IOException){refused=true;}
                Require(refused&&failedClient.HasProcess&&!failedClient.LastStatusFresh&&!failedClient.LimitsActive,"Failed start fabricated a retired/active Guardian session.");
                refused=false;try{await failedClient.CloseAsync();}catch(IOException){refused=true;}
                Require(refused&&failedClient.HasProcess,"Nonzero owner exit was presented as successful release.");
                Console.WriteLine("PASS: failed startup owner retained; nonzero exit never implies a confirmed reset.");
            }
            finally{failedOwner?.Dispose();} // Already-exited fixture only; no live Guardian is terminated.
            Process? startupFailureOwner = null;
            var startupFailureClient = new PerformanceGuardianClient(Path.GetTempPath(), start =>
            {
                start.ArgumentList[0] = "--gui-fixture-startup-failure";
                startupFailureOwner = Process.Start(start);
                return startupFailureOwner;
            },recoveryPreview:NoRecovery);
            try
            {
                var timer = Stopwatch.StartNew();
                IOException? failure = null;
                try { await startupFailureClient.EnableAsync(configuration); } catch(IOException ex) { failure = ex; }
                Require(failure?.Message.Contains("Synthetic pending journal", StringComparison.Ordinal) == true &&
                    timer.Elapsed < TimeSpan.FromSeconds(15) && startupFailureClient.HasProcess && !startupFailureClient.LimitsActive,
                    "Early Guardian exit lost its root cause, waited for pipe timeout, or retired unresolved owner.");
                Console.WriteLine("PASS: startup failure interrupts pipe wait promptly and preserves explicit cause/unresolved owner.");
            }
            finally { startupFailureOwner?.Dispose(); } // Fixture has already exited; no hardware process is killed.
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("GUI integration self-test failed: " + ex); return 1; }
    }
    private sealed class FixtureMutexObservers : IDisposable
    {
        private readonly List<Mutex> _handles=new();
        internal void Retain(string name)=>_handles.Add(new Mutex(false,name));
        public void Dispose(){foreach(var handle in _handles)handle.Dispose();}
    }
}
