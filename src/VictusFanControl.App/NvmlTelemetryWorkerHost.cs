using System.Diagnostics;
using System.Text;
using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.App;

internal static class NvmlTelemetryWorkerHost
{
    private static readonly HashSet<string> Fixtures = new(StringComparer.Ordinal)
    { "healthy", "exit", "fatal", "hang", "malformed", "oversize", "partial", "wrong-session", "wrong-sequence",
        "wrong-version", "stale", "future", "invalid-temperature", "invalid-power", "invalid-load", "error", "replay", "stderr-flood" };

    internal static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--nvml-telemetry-worker" or "--nvml-telemetry-fixture")) return 2;
        var fixture = args[0] == "--nvml-telemetry-fixture";
        if (args.Length != (fixture ? 4 : 3) || fixture && !Fixtures.Contains(args[3]) ||
            !int.TryParse(args[1], out var parentId) || parentId <= 0 ||
            !long.TryParse(args[2], out var parentStart) || parentStart <= 0) return 2;
        using var parent = OpenOwner(parentId,parentStart);
        if (parent is null) return 197;
        using var finished = new ManualResetEventSlim();
        var lastRequest = Environment.TickCount64;
        // This thread stays independent of blocked native calls and exits only this read-only helper.
        var monitor = new Thread(() =>
        {
            while (!finished.Wait(500))
            {
                if (!OwnerAlive(parent) || Environment.TickCount64 - Interlocked.Read(ref lastRequest) > 20000) Environment.Exit(197);
            }
        }) { IsBackground = true, Name = "NVML helper owner monitor" };
        monitor.Start();
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        INvmlTelemetryReader? reader = null;
        try
        {
            using var idle = new CancellationTokenSource(20000);
            var first = NvmlTelemetryProtocol.Parse<NvmlTelemetryProtocol.Request>(
                NvmlTelemetryProtocol.ReadLineAsync(Console.In, idle.Token).GetAwaiter().GetResult());
            if (first.Version != NvmlTelemetryProtocol.Version || first.Operation != "initialize" || first.Sequence != 1 ||
                !Guid.TryParseExact(first.Session, "N", out _) || first.PreferredDeviceName?.Length > 128) return 2;
            if (!fixture) reader = new NvmlClient(first.PreferredDeviceName, first.RequirePreferredDevice);
            var name = reader?.DeviceName ?? first.PreferredDeviceName ?? "fixture-GPU";
            Reply(new(first.Version, first.Session, 1, name));
            long sequence = 1;
            while (true)
            {
                using var deadline = new CancellationTokenSource(20000);
                var line = NvmlTelemetryProtocol.ReadLineAsync(Console.In, deadline.Token).GetAwaiter().GetResult();
                if (line is null) return 0;
                var request = NvmlTelemetryProtocol.Parse<NvmlTelemetryProtocol.Request>(line);
                if (request.Version != first.Version || request.Session != first.Session || request.Sequence != ++sequence ||
                    request.Operation != "sample") return 2;
                Interlocked.Exchange(ref lastRequest, Environment.TickCount64);
                var sampledAt = DateTimeOffset.UtcNow;
                var sample = reader?.ReadSample() ?? new NvmlClient.GpuSample(51, 18, 12);
                var response = new NvmlTelemetryProtocol.Reply(first.Version, first.Session, sequence, name, sampledAt, sample);
                if (fixture)
                {
                    switch (args[3])
                    {
                        case "exit": return 191;
                        case "fatal": Environment.FailFast("Synthetic fatal NVML helper fixture; no hardware IO."); break;
                        case "hang": Thread.Sleep(Timeout.Infinite); break;
                        case "malformed": Console.WriteLine("{"); continue;
                        case "oversize": Console.WriteLine(new string('x', 8192)); continue;
                        case "partial": Console.Write("{"); Console.Out.Flush(); return 192;
                        case "wrong-session": response = response with { Session = Guid.NewGuid().ToString("N") }; break;
                        case "wrong-sequence": response = response with { Sequence = sequence + 1 }; break;
                        case "wrong-version": response = response with { Version = 99 }; break;
                        case "stale": response = response with { SampledAtUtc = sampledAt.AddMinutes(-1) }; break;
                        case "future": response = response with { SampledAtUtc = sampledAt.AddMinutes(1) }; break;
                        case "invalid-temperature": response = response with { Sample = sample with { TemperatureC = 0 } }; break;
                        case "invalid-power": response = response with { Sample = sample with { PowerW = 300 } }; break;
                        case "invalid-load": response = response with { Sample = sample with { LoadPercent = 101 } }; break;
                        case "error": response = response with { Failed = true }; break;
                        case "replay" when sequence > 2: response = response with { Sequence = 2 }; break;
                        case "stderr-flood": for (var i = 0; i < 256; i++) Console.Error.Write(new string('x', 8192)); break;
                    }
                }
                Reply(response);
            }
        }
        catch (Exception ex) when (ex is not AccessViolationException)
        {
            // Ordinary API/protocol errors end the helper. Native corrupted-state exceptions are not recoverable.
            Console.Error.WriteLine("NVML read-only helper failed: " + ex.GetType().Name); return 193;
        }
        finally
        {
            reader?.Dispose(); finished.Set(); monitor.Join(1000);
        }
    }

    private static Process? OpenOwner(int id,long startedTicks)
    {
        Process? owner=null;
        try
        {
            owner=Process.GetProcessById(id);
            if (!owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == startedTicks) return owner;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        owner?.Dispose(); return null;
    }

    private static bool OwnerAlive(Process owner)
    {
        try { return !owner.HasExited; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }

    private static void Reply(NvmlTelemetryProtocol.Reply reply)
    {
        NvmlTelemetryProtocol.WriteAsync(Console.Out, reply, CancellationToken.None).GetAwaiter().GetResult();
        Console.Out.Flush();
    }
}
