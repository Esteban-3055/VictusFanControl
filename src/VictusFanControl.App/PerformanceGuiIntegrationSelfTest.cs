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
            new PerformanceGuiSessionConfiguration().Validate();
            foreach (var invalid in new[]
            {
                new PerformanceGuiSessionConfiguration { CpuEnabled = false, GpuEnabled = false },
                new PerformanceGuiSessionConfiguration { AcPl1Watts = 45 },
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
            Require(document.Descendants(ns + "Command").Single().Value == @"C:\Program Files\Victus & Control\app.exe", "Startup escaping.");
            Require(document.Descendants(ns + "UserId").Count() == 2 &&
                document.Descendants(ns + "LogonType").Single().Value == "InteractiveToken" &&
                document.Descendants(ns + "RunLevel").Single().Value == "HighestAvailable" &&
                document.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S" &&
                document.Descendants(ns + "Arguments").Single().Value.StartsWith("--start-minimized --modules-dir"), "Startup semantic contract.");
            Console.WriteLine("PASS: CPU/GPU configuration rejection, reply identity, startup XML escaping/session contract.");

            var fixtureMode = "--gui-fixture-session";
            Process? launched = null;
            string? reportPath = null;
            var client = new PerformanceGuardianClient(Path.GetTempPath(), start =>
            {
                start.ArgumentList[0] = fixtureMode;
                reportPath = start.ArgumentList[start.ArgumentList.IndexOf("--report") + 1];
                launched = Process.Start(start);
                return launched;
            });
            var configuration = new PerformanceGuiSessionConfiguration { AcPl1Watts = 20, AcPl2Watts = 40 };
            try
            {
                var enabled = await client.EnableAsync(configuration);
                Require(enabled.Ok && client.LimitsActive && client.AppliedConfiguration == configuration, "GUI enable state.");
                Require((await client.StatusAsync())?.CpuState == "Active", "GUI status round trip.");
                var rejected = false;
                try { await client.EnableAsync(configuration); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Concurrent replacement session accepted.");
                await client.CloseAsync();
                Require(!client.HasProcess && !client.LimitsActive, "GUI shutdown leaked owner.");
                using var report = JsonDocument.Parse(File.ReadAllText(reportPath!));
                Require(report.RootElement.GetProperty("CpuHardwareWriteAttempts").GetInt32() == 0 &&
                    report.RootElement.GetProperty("GpuHardwareWriteAttempts").GetInt32() == 0 &&
                    report.RootElement.GetProperty("ReleaseCalls").GetInt32() == 1 &&
                    report.RootElement.GetProperty("FinalPhase").GetString() == "Stopped", "Fixture touched hardware / failed release.");
                Console.WriteLine("PASS: real GUI IPC HELLO/ENABLE/STATUS/SHUTDOWN; immutable preferences; cleanup; zero hardware writes.");
                // Close arriving while launch/enable is pending waits for the operation and releases once.
                var enabling = client.EnableAsync(configuration);
                var closing = client.CloseAsync();
                await Task.WhenAll(enabling, closing);
                Require(!client.HasProcess, "Close while starting leaked Guardian.");
                Console.WriteLine("PASS: close while GUI session starts serializes and releases.");
                fixtureMode = "--gui-fixture-enable-failure";
                rejected = false;
                try { await client.EnableAsync(configuration); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected && !client.LimitsActive, "Rejected domain enable created active status.");
                await client.CloseAsync();
                Require(!client.HasProcess, "Rejected enable leaked Guardian.");
                Console.WriteLine("PASS: domain enable rejection remains inactive; reconnect/SHUTDOWN releases the transport owner.");
            }
            finally { if (client.HasProcess) await client.CloseAsync(); }
            // A started process exiting with an error must remain unresolved, rather than look Disabled.
            Process? failedOwner=null;
            var failedClient=new PerformanceGuardianClient(Path.GetTempPath(), _=>
            {
                var start=new ProcessStartInfo("cmd.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
                start.ArgumentList.Add("/c");start.ArgumentList.Add("exit 23");failedOwner=Process.Start(start)!;
                if(!failedOwner.WaitForExit(5000))throw new TimeoutException("Fatal-start fixture did not exit.");return failedOwner;
            });
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
            });
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
}
