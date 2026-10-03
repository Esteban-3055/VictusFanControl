using System.Diagnostics;
using System.Text.Json;

namespace VictusFanControl.CpuProbe;

internal sealed record ProbeRequest(string Mode, string ModulePath, string Directory,
    int DurationSeconds, int ParentId, long ParentStartTicks, DateTimeOffset CreatedUtc);

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--self-test" })) return await RaplProbeSelfTest.RunAsync();
            if (args.Length == 2 && args[0] == "--worker") return Worker(args[1]);
            if (args.Length == 0 || args.SequenceEqual(new[] { "--help" }))
            {
                Console.WriteLine("CPU-only RAPL diagnostic. No fan, EC or NVIDIA control.");
                Console.WriteLine("--observe|--write-test --module <IntelMSR.bin> --output-dir <new-dir> --duration-seconds <10..120>");
                Console.WriteLine("Writes are a bounded downward-only 20% test with restore, never a persistent limiter.");
                return 0;
            }
            if (args.Length != 7 || args[0] is not ("--observe" or "--write-test") ||
                args[1] != "--module" || args[3] != "--output-dir" || args[5] != "--duration-seconds" ||
                !int.TryParse(args[6], out var seconds) || seconds is < 10 or > 120)
                throw new ArgumentException("Invalid arguments. Use --help. Maximum duration is 120 seconds.");
            var directory = Path.GetFullPath(args[4]);
            if (Directory.Exists(directory)) throw new IOException("Output directory must be new.");
            Directory.CreateDirectory(directory);
            using var parent = Process.GetCurrentProcess();
            var request = new ProbeRequest(args[0], Path.GetFullPath(args[2]), directory,
                seconds, parent.Id, parent.StartTime.ToUniversalTime().Ticks, DateTimeOffset.UtcNow);
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
        if (request.Mode is not ("--observe" or "--write-test") || request.DurationSeconds is < 10 or > 120 ||
            Path.GetFullPath(requestPath) != Path.Combine(request.Directory, "request.json") ||
            DateTimeOffset.UtcNow - request.CreatedUtc > TimeSpan.FromSeconds(30) ||
            request.CreatedUtc > DateTimeOffset.UtcNow.AddSeconds(2))
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
        using var guard = new Mutex(false, @"Global\VictusFanControl.IntelRaplCpuProbe.8C40");
        bool acquired;
        try { acquired = guard.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another CPU probe is running.");
        var journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VictusFanControl", "CpuProbe", "active-write.json");
        var writeTest = request.Mode == "--write-test";
        try
        {
            using var evidence = new ProbeEvidence(request.Directory, writeTest ? journal : null);
            try
            {
                if (writeTest && File.Exists(journal))
                    throw new InvalidOperationException("Previous write test has an unresolved journal. Read-only diagnosis only; preserve " + journal);
                using var hardware = new PhysicalRaplHardware(request.ModulePath, request.Directory, writeTest);
                evidence.Event("CPU-only experiment; firmware fans; no direct EC, HP WMI fan calls or GPU writes.");
                evidence.Event(writeTest ? "P1: baseline 10 s, reduce PL1/PL2 once, observe, restore, verify." : "P0/P0.5: read-only observation.");
                var timing = writeTest ? ProbeTiming.Physical(request.DurationSeconds)
                    : new ProbeTiming(request.DurationSeconds, 0, 0, 1000);
                // Keep the named mutex on its owning OS thread while the engine
                // performs sequential asynchronous sampling on pool threads.
                var result = RaplProbeEngine.RunAsync(hardware, evidence, writeTest, timing,
                    KeepRunning, CancellationToken.None).GetAwaiter().GetResult();
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
