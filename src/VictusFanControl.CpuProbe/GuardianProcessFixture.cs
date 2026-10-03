using System.Diagnostics;
using System.Text.Json;

namespace VictusFanControl.CpuProbe;

// Real Windows processes, simulated register only. This verifies that the
// no-console guardian outlives its launcher and releases its mutex correctly.
internal static class GuardianProcessFixture
{
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "VictusFanControl.CpuProbe.exe");
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native guardian fixture requires Windows.");
        var directory = Path.Combine(root, "guardian-parent-death");
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--fixture-supervisor");
        start.ArgumentList.Add(directory);
        using var supervisor = Process.Start(start) ?? throw new IOException("Fixture supervisor did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await supervisor.WaitForExitAsync(timeout.Token);
        if (supervisor.ExitCode != 0) throw new InvalidOperationException("Fixture supervisor failed before simulated apply.");
        var guardian = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(directory, "guardian.json")));
        var pid = guardian.GetProperty("Id").GetInt32();
        var ticks = guardian.GetProperty("StartTicks").GetInt64();
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks == ticks)
                await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { } // Guardian has already released and exited.
        var result = JsonSerializer.Deserialize<ProbeResult>(File.ReadAllText(Path.Combine(directory, "summary.json")));
        if (result is null || result.Result != "ABORTED" || result.RestoreResult != "BASELINE_VERIFIED")
            throw new InvalidOperationException("Real parent death did not produce verified simulated restoration.");
        using var mutex = new Mutex(false, @"Global\VictusFanControl.IntelRaplCpuProbe.Fixtures");
        if (!mutex.WaitOne(0)) throw new InvalidOperationException("Guardian did not release its single-probe mutex.");
        mutex.ReleaseMutex();
        Console.WriteLine("PASS native no-console guardian survives parent exit and restores simulated limit");
    }

    internal static int Supervisor(string directory)
    {
        using var parent = Process.GetCurrentProcess();
        var request = new ProbeRequest("--fixture", "NEVER_OPENED", directory, 10,
            parent.Id, parent.StartTime.ToUniversalTime().Ticks, null, null, DateTimeOffset.UtcNow);
        var requestPath = Path.Combine(directory, "request.json");
        ProbeEvidence.DurableJson(requestPath, request);
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--worker");
        start.ArgumentList.Add(requestPath);
        using var guardian = Process.Start(start) ?? throw new IOException("Fixture guardian did not start.");
        ProbeEvidence.DurableJson(Path.Combine(directory, "guardian.json"), new {
            guardian.Id, StartTicks = guardian.StartTime.ToUniversalTime().Ticks });
        var wait = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "fixture-applied.json")))
        {
            if (guardian.HasExited || wait.Elapsed > TimeSpan.FromSeconds(10)) return 2;
            Thread.Sleep(10);
        }
        // Abrupt owner exit: bypass managed cleanup. Only simulated MSR exists.
        Environment.Exit(0);
        return 0;
    }

    internal sealed class FixtureHardware(string directory) : IRaplProbeHardware
    {
        private ulong _raw = 360UL | (1UL << 15) | (480UL << 32) | (1UL << 47);
        private int _writes;
        public ulong UnitsRaw => 3UL | (14UL << 8) | (10UL << 16);
        public ulong PowerInfoRaw => 360UL | (80UL << 16) | (920UL << 32);
        public ulong ReadLimit() => _raw;
        public void WriteLimit(ulong value)
        {
            if (!File.Exists(Path.Combine(directory, "fixture-active.json")))
                throw new InvalidOperationException("Fixture write lacks journal.");
            _raw = value;
            if (++_writes == 1) ProbeEvidence.DurableJson(Path.Combine(directory, "fixture-applied.json"), new { Raw = $"0x{value:X16}" });
        }
        public CpuObservation Sample() => new(_raw, _writes == 1 ? 40 : 60, 50, 80, true);
        public void Dispose() { }
    }
}
