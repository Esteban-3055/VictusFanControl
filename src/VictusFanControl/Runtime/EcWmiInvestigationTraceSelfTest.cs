using System.Text.Json;

namespace VictusFanControl.Runtime;

internal static class EcWmiInvestigationTraceSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            var first = new InvestigationTraceRecord(DateTimeOffset.UnixEpoch, 12345, 67890,
                7, 42, "ec.read.failure", "register=0x34;status=0x01\nsecond line");
            var buffer = new InvestigationTraceBuffer(2);
            Check(buffer.TryAdd(first) && buffer.TryAdd(first with { Operation = 43 }), "initial records refused");
            Check(!buffer.TryAdd(first with { Operation = 44 }) && buffer.Dropped == 1, "full queue loss not visible");
            Check(buffer.TryTake(out var read) && read == first, "FIFO or original timestamps changed");
            Check(buffer.TryAdd(first with { Operation = 45 }), "drained capacity was not reusable");
            Check(buffer.TryTake(out read) && read?.Operation == 43, "overflow overwrote queued evidence");
            Check(buffer.TryTake(out read) && read?.Operation == 45 && !buffer.TryTake(out _), "queue did not drain");
            output.WriteLine("PASS: investigation queue preserves evidence and reports overflow without blocking.");

            var concurrent = new InvestigationTraceBuffer(17);
            Parallel.For(0, 2000, i => concurrent.TryAdd(first with { Operation = i }));
            var operations = new HashSet<long>();
            while (concurrent.TryTake(out read)) Check(operations.Add(read!.Operation), "duplicate concurrent record");
            Check(operations.Count == 17 && concurrent.Dropped == 1983, "concurrent bound/loss accounting failed");
            output.WriteLine("PASS: concurrent investigation producers remain bounded with exact loss accounting.");

            var json = JsonSerializer.Serialize(first);
            Check(!json.Contains('\n') && JsonSerializer.Deserialize<InvestigationTraceRecord>(json) == first,
                "JSONL escaped details or clock evidence changed");
            output.WriteLine("PASS: investigation JSONL retains UTC, uptime, QPC and escaped error detail.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine($"FAIL: investigation trace: {ex.Message}");
            return 1;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
