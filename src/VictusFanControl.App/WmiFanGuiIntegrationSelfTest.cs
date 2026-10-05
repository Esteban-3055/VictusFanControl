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
            var client = new WmiFanGuiGuardianClient(fixture: true);
            await client.StartAsync(default);
            client.EnsureAlive(); client.PersistIntent(30); client.Heartbeat();
            var release = await client.ReleaseAsync(default);
            if (release is not { ReleaseRequestAccepted: true, LegacyDefaultRequestAccepted: true,
                GuardianLeaseRetired: true, IndependentFirmwareOwnershipVerified: false })
                throw new InvalidOperationException("Detached WMI release fixture failed.");
            await client.DisposeAsync();
            Console.WriteLine("PASS: real detached guardian READY/intent/release, owner binding, lease retirement; zero hardware IO.");

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
