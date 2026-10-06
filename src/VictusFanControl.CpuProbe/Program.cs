using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VictusFanControl.CpuProbe;

internal sealed record ProbeRequest(string Mode, string ModulePath, string Directory,
    int DurationSeconds, int ParentId, long ParentStartTicks,
    double? Pl1Watts, double? Pl2Watts, DateTimeOffset CreatedUtc);

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--self-test" })) return await RaplProbeSelfTest.RunAsync();
            if (args.Length > 0 && args[0] == "--frequency-readonly")
                return FrequencyReadOnlyProbe.Run(args.Skip(1).ToArray());
            if (args.Length == 2 && args[0] == "--fixture-supervisor")
                return GuardianProcessFixture.Supervisor(args[1]);
            if (args.Length == 2 && args[0] == "--worker") return Worker(args[1]);
            if (args.Length == 0 || args.SequenceEqual(new[] { "--help" }))
            {
                Console.WriteLine("CPU-only RAPL diagnostic. No fan, EC or NVIDIA control.");
                Console.WriteLine("--observe|--write-test --module <IntelMSR.bin> --output-dir <new-dir> --duration-seconds <10..120> [--pl1-watts <W> --pl2-watts <W>]");
                Console.WriteLine("--frequency-readonly --module <IntelMSR.bin> --output-dir <new-dir> --duration-seconds <10..120>");
                Console.WriteLine("Default writes reduce both limits 20%. Explicit limits remain downward-only, validated, restored and non-persistent.");
                return 0;
            }
            if (args.Length is not (7 or 11) || args[0] is not ("--observe" or "--write-test") ||
                args[1] != "--module" || args[3] != "--output-dir" || args[5] != "--duration-seconds" ||
                !int.TryParse(args[6], out var seconds) || seconds is < 10 or > 120)
                throw new ArgumentException("Invalid arguments. Use --help. Maximum duration is 120 seconds.");
            double? pl1Watts = null;
            double? pl2Watts = null;
            if (args.Length == 11)
            {
                if (args[0] != "--write-test" || args[7] != "--pl1-watts" || args[9] != "--pl2-watts" ||
                    !double.TryParse(args[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var pl1) ||
                    !double.TryParse(args[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var pl2) ||
                    !double.IsFinite(pl1) || !double.IsFinite(pl2) || pl1 < 10 || pl1 > 200 ||
                    pl2 < pl1 || pl2 > 250)
                    throw new ArgumentException("Explicit limits require --write-test, PL1 10..200 W and PL2 >= PL1 and <=250 W.");
                pl1Watts = pl1;
                pl2Watts = pl2;
            }
            var directory = Path.GetFullPath(args[4]);
            if (Directory.Exists(directory)) throw new IOException("Output directory must be new.");
            Directory.CreateDirectory(directory);
            using var parent = Process.GetCurrentProcess();
            var request = new ProbeRequest(args[0], Path.GetFullPath(args[2]), directory,
                seconds, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
                pl1Watts, pl2Watts, DateTimeOffset.UtcNow);
            var requestPath = Path.Combine(directory, "request.json");
            ProbeEvidence.DurableJson(requestPath, request);
            // Start the apphost executable directly. The PowerShell launcher does
            // not use dotnet run, so the supervisor process identity stays stable.
            var executable = Path.Combine(AppContext.BaseDirectory, "VictusFanControl.CpuProbe.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--worker");
            start.ArgumentList.Add(requestPath);
            using var worker = Process.Start(start) ?? throw new IOException("CPU guardian failed to start.");
            var output = worker.StandardOutput.ReadToEndAsync();
            var errors = worker.StandardError.ReadToEndAsync();
            ProbeEvidence.DurableJson(Path.Combine(directory, "guardian.json"), new {
                worker.Id, StartTicks = worker.StartTime.ToUniversalTime().Ticks, Request = request });
            var stopPath = Path.Combine(directory, "STOP");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; File.WriteAllText(stopPath, "stop"); };
            Console.WriteLine($"CPU guardian PID {worker.Id}; evidence: {directory}");
            Console.WriteLine("Ctrl+C requests restoration. Do not terminate the guardian or start a second experiment.");
            var printed = 0;
            var watchdog = Stopwatch.StartNew();
            while (!worker.HasExited)
            {
                PrintNewEvents(directory, ref printed);
                if (watchdog.Elapsed.TotalSeconds > seconds + 90)
                {
                    File.WriteAllText(stopPath, "supervisor-timeout");
                    Console.Error.WriteLine("Guardian did not finish in time. Restoration is UNCONFIRMED; guardian was not killed.");
                    return 4;
                }
                await Task.Delay(500);
            }
            PrintNewEvents(directory, ref printed);
            var stdout = await output;
            var stderr = await errors;
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
            return worker.ExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine("CPU probe refused/failed: " + ex.Message); return 2; }
    }

    private static void PrintNewEvents(string directory, ref int printed)
    {
        var path = Path.Combine(directory, "events.txt");
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (; printed < lines.Length; printed++) Console.WriteLine(lines[printed].TrimEnd('\r'));
    }

    private static int Worker(string requestPath)
    {
        var request = JsonSerializer.Deserialize<ProbeRequest>(File.ReadAllText(requestPath))
            ?? throw new InvalidDataException("Missing request.");
        var hasExplicitPl1 = request.Pl1Watts.HasValue;
        var hasExplicitPl2 = request.Pl2Watts.HasValue;
        if (request.Mode is not ("--observe" or "--write-test" or "--fixture") || request.DurationSeconds is < 10 or > 120 ||
            Path.GetFullPath(requestPath) != Path.Combine(request.Directory, "request.json") ||
            DateTimeOffset.UtcNow - request.CreatedUtc > TimeSpan.FromSeconds(30) ||
            request.CreatedUtc > DateTimeOffset.UtcNow.AddSeconds(2) ||
            hasExplicitPl1 != hasExplicitPl2 ||
            (hasExplicitPl1 && (request.Mode != "--write-test" ||
                !double.IsFinite(request.Pl1Watts!.Value) || !double.IsFinite(request.Pl2Watts!.Value) ||
                request.Pl1Watts.Value < 10 || request.Pl1Watts.Value > 200 ||
                request.Pl2Watts.Value < request.Pl1Watts.Value || request.Pl2Watts.Value > 250)))
            throw new InvalidDataException("Invalid or stale guardian request.");
        var stopPath = Path.Combine(request.Directory, "STOP");
        bool KeepRunning()
        {
            if (File.Exists(stopPath)) return false;
            try
            {
                using var parent = Process.GetProcessById(request.ParentId);
                return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == request.ParentStartTicks &&
                    string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        if (!KeepRunning()) throw new InvalidDataException("Request owner is not alive or does not match this executable.");
        var fixture = request.Mode == "--fixture";
        using var guard = new Mutex(false, fixture
            ? @"Global\VictusFanControl.IntelRaplCpuProbe.Fixtures"
            : @"Global\VictusFanControl.IntelRaplCpuProbe.8C40");
        bool acquired;
        try { acquired = guard.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another CPU probe is running.");
        var journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VictusFanControl", "CpuProbe", "active-write.json");
        if (fixture) journal = Path.Combine(request.Directory, "fixture-active.json");
        var writeTest = request.Mode != "--observe";
        try
        {
            using var evidence = new ProbeEvidence(request.Directory, writeTest ? journal : null);
            try
            {
                if (writeTest && File.Exists(journal))
                    throw new InvalidOperationException("Previous write test has an unresolved journal. Read-only diagnosis only; preserve " + journal);
                using IRaplProbeHardware hardware = fixture
                    ? new GuardianProcessFixture.FixtureHardware(request.Directory)
                    : new PhysicalRaplHardware(request.ModulePath, request.Directory, writeTest);
                evidence.Event("CPU-only experiment; firmware fans; no direct EC, HP WMI fan calls or GPU writes.");
                evidence.Event(writeTest
                    ? request.Pl1Watts.HasValue
                        ? $"P1: baseline 10 s, apply explicit downward limits PL1={request.Pl1Watts.Value:0.###} W / PL2={request.Pl2Watts!.Value:0.###} W once, observe, restore, verify."
                        : "P1: baseline 10 s, reduce PL1/PL2 by 20% once, observe, restore, verify."
                    : "P0/P0.5: read-only observation.");
                var timing = fixture ? new ProbeTiming(2, 500, 1, 10)
                    : writeTest ? ProbeTiming.Physical(request.DurationSeconds)
                    : new ProbeTiming(request.DurationSeconds, 0, 0, 1000);
                // Keep the named mutex on its owning OS thread while the engine
                // performs sequential asynchronous sampling on pool threads.
                var explicitLimits = request.Pl1Watts.HasValue
                    ? new RequestedPowerLimits(request.Pl1Watts.Value, request.Pl2Watts!.Value)
                    : (RequestedPowerLimits?)null;
                var result = RaplProbeEngine.RunAsync(hardware, evidence, writeTest, timing,
                    KeepRunning, CancellationToken.None, explicitLimits).GetAwaiter().GetResult();
                if (result.WriteAttempted && result.RestoreResult != "BASELINE_VERIFIED") return 3;
                return result.Error is null ? 0 : 2;
            }
            catch (Exception ex)
            {
                evidence.Event("PREFLIGHT_REFUSED: " + ex.Message);
                ProbeEvidence.DurableJson(Path.Combine(request.Directory, "preflight-failure.json"), new {
                    Result = File.Exists(Path.Combine(request.Directory, "write-journal.json"))
                        ? "PROBE_EXCEPTION__CONSULT_WRITE_JOURNAL_AND_SUMMARY"
                        : "NO_WRITE_CURRENT_PROBE_PREFLIGHT_REFUSED", Error = ex.Message });
                return 2;
            }
        }
        finally { guard.ReleaseMutex(); }
    }
}
