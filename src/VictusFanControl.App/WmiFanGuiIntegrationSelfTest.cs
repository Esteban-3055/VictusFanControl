using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

internal static class WmiFanGuiIntegrationSelfTest
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            await Hp8C40WmiFanControlBackendSelfTest.RunAsync(Console.Out);

            var boundaryRoot = Path.Combine(Path.GetTempPath(), "VFC-WmiBoundary-" + Guid.NewGuid().ToString("N"));
            var boundaryA = Path.Combine(boundaryRoot, "a");
            var boundaryB = Path.Combine(boundaryRoot, "b");
            WmiFanExperimentBoundary.Enable(boundaryA, true, gui: true);
            WmiFanExperimentBoundary.BeginRecovery();
            WmiFanExperimentBoundary.RearmGuiAfterSuccessfulRelease(boundaryA, boundaryB);
            if (WmiFanExperimentBoundary.Recovering ||
                !string.Equals(Path.GetFullPath(boundaryB), WmiFanExperimentBoundary.SessionDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WMI GUI boundary did not rearm after a verified release.");
            Console.WriteLine("PASS: WMI GUI boundary recovery closes then rearms a fresh session directory.");
            File.WriteAllText(WmiFanExperimentBoundary.StopPath, "fixture-stop");
            var paused = false;
            try { WmiFanExperimentBoundary.EnsureRequestAllowed(Hp8C40BiosFanControl.BuildGetFanLevelRequest()); }
            catch (WmiFanReadAdmissionPausedException) { paused = true; }
            if (!paused) throw new IOException("Guardian stop allowed a new GUI native read.");
            File.Delete(WmiFanExperimentBoundary.StopPath);

            var client = new WmiFanGuiGuardianClient(fixture: true);
            await client.StartAsync(default);
            client.EnsureAlive();
            foreach(var invalid in new[]{9,51})
            {
                var refused=false;try{client.PersistIntent(invalid);}catch(ArgumentOutOfRangeException){refused=true;}
                if(!refused||File.Exists(Path.Combine(client.SessionDirectory,"write-intent.json")))throw new InvalidOperationException("Invalid Manual level created guardian intent.");
            }
            client.PersistIntent(10); client.Heartbeat();
            using(var intent=JsonDocument.Parse(File.ReadAllText(Path.Combine(client.SessionDirectory,"write-intent.json"))))
                if(intent.RootElement.GetProperty("Level").GetInt32()!=10)throw new InvalidOperationException("Detached guardian lost Manual low endpoint intent.");
            var release = await client.ReleaseAsync(default);
            if (release is not { ReleaseRequestAccepted: true, LegacyDefaultRequestAccepted: true,
                GuardianLeaseRetired: true, IndependentFirmwareOwnershipVerified: false })
                throw new InvalidOperationException("Detached WMI release fixture failed.");
            await client.DisposeAsync();
            Console.WriteLine("PASS: real detached guardian READY/intent/release, owner binding, lease retirement; zero hardware IO.");

            var draining = new WmiFanGuiGuardianClient(fixture: true);
            await draining.StartAsync(default); draining.PersistIntent(30);
            using (var entered = new ManualResetEventSlim())
            using (var finish = new ManualResetEventSlim())
            {
                var marker = Path.Combine(draining.SessionDirectory, "native-inflight.json");
                var native = Task.Run(() =>
                {
                    using var mutex = new Mutex(false, @"Global\VictusFanControl.WmiFanExperiment.Native");
                    mutex.WaitOne();
                    try
                    {
                        File.WriteAllText(marker,"fixture-live-read"); entered.Set();
                        if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Drain fixture timed out.");
                        File.Delete(marker);
                    }
                    finally { mutex.ReleaseMutex(); }
                });
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Drain fixture did not enter.");
                var releasing = draining.ReleaseAsync(default);
                using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    while (!File.Exists(Path.Combine(draining.SessionDirectory,"stop.signal"))) await Task.Delay(20,drainDeadline.Token);
                    // The marker is still live when the guardian sees stop.
                    await Task.Delay(350,drainDeadline.Token);
                    if (File.Exists(draining.ReportPath)) throw new IOException("Guardian classified a live native call before draining it.");
                }
                finally { finish.Set(); }
                await native;
                var drained = await releasing;
                if (!drained.GuardianLeaseRetired || !drained.ReleaseRequestAccepted) throw new IOException("Completed live read retained its lease.");
            }
            await draining.DisposeAsync();
            Console.WriteLine("PASS: detached guardian drains a concurrent native read before release; zero hardware IO.");

            var unknown = new WmiFanGuiGuardianClient(fixture: true);
            await unknown.StartAsync(default);
            unknown.PersistIntent(30);
            File.WriteAllText(Path.Combine(unknown.SessionDirectory, "native-uncertain.signal"), "fixture");
            var rejected = false;
            try { await unknown.ReleaseAsync(default); } catch (IOException) { rejected = true; }
            if (!rejected || !File.Exists(Path.Combine(unknown.SessionDirectory, "fixture-lease.json")))
                throw new InvalidOperationException("Uncertain native completion retired lease.");
            using (var report = JsonDocument.Parse(File.ReadAllText(unknown.ReportPath)))
                if (report.RootElement.GetProperty("ReleaseRequestAccepted").GetBoolean() ||
                    report.RootElement.GetProperty("LegacyDefaultRequestAccepted").GetBoolean())
                    throw new InvalidOperationException("Uncertain native completion admitted release.");
            await unknown.DisposeAsync();
            Console.WriteLine("PASS: unknown native completion blocks all recovery calls and retains lease.");

            var directory = Path.Combine(Path.GetTempPath(), "VFC-WmiOwnerExit-" + Guid.NewGuid().ToString("N"));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(WmiFanGuiIntegrationSelfTest).Assembly.Location);
            start.ArgumentList.Add("--wmi-fan-fixture-owner"); start.ArgumentList.Add(directory);
            using var owner = Process.Start(start) ?? throw new IOException("Fixture owner did not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await owner.WaitForExitAsync(deadline.Token);
            if (owner.ExitCode != 0) throw new IOException("Fixture owner failed.");
            var reportPath = Path.Combine(directory, "guardian-report.json");
            while (!File.Exists(reportPath)) await Task.Delay(100, deadline.Token);
            using (var report = JsonDocument.Parse(File.ReadAllText(reportPath)))
            {
                var r = report.RootElement;
                if (r.GetProperty("OwnerPid").GetInt32() != owner.Id || r.GetProperty("ExitReason").GetString() != "OWNER_EXITED" ||
                    !r.GetProperty("ReleaseRequestAccepted").GetBoolean() || !r.GetProperty("LegacyDefaultRequestAccepted").GetBoolean() ||
                    !r.GetProperty("GuardianLeaseRetired").GetBoolean())
                    throw new InvalidOperationException("Owner death recovery fixture failed.");
            }
            Console.WriteLine("PASS: owner process exits without release; detached guardian recovers and retires fixture lease.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    internal static async Task<int> RunFixtureOwnerAsync(string directory)
    {
        var client = new WmiFanGuiGuardianClient(fixture: true, fixtureDirectory: Path.GetFullPath(directory));
        await client.StartAsync(default);
        client.PersistIntent(30);
        return 0; // Intentionally exit this zero-hardware fixture owner without release.
    }
}
