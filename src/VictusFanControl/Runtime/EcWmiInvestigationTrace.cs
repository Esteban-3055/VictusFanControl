using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VictusFanControl.Runtime;

/// <summary>
/// Opt-in chronology of existing I/O. Producers only enqueue; the background
/// writer owns disk I/O. This never reads hardware, changes admission or retries.
/// UTC is for ETW correlation; TickCount64/QPC preserve ordering independently.
/// </summary>
internal static class EcWmiInvestigationTrace
{
    internal const string EnvironmentVariable = "VFC_EC_WMI_DIAGNOSTICS";
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly bool Requested = Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";
    private static readonly InvestigationTraceBuffer Buffer = new(4096);
    private static int _started;
    private static int _stopped;
    private static long _operation;

    internal static bool Enabled => Requested && Volatile.Read(ref _stopped) == 0;

    internal static void Initialize()
    {
        if (!Enabled || Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        try { new Thread(WriteLoop) { IsBackground = true, Name = "VFC investigation log" }.Start(); }
        catch { Volatile.Write(ref _stopped, 1); }
    }

    internal static long Begin(string stage, string detail)
    {
        if (!Enabled) return 0;
        var operation = Interlocked.Increment(ref _operation);
        Record(operation, stage, detail);
        return operation;
    }

    internal static void Record(long operation, string stage, string detail)
    {
        if (!Enabled) return;
        // Logging failures must never affect a command, cancellation or restore.
        try
        {
            Buffer.TryAdd(new InvestigationTraceRecord(DateTimeOffset.UtcNow,
                Environment.TickCount64, Stopwatch.GetTimestamp(),
                Environment.CurrentManagedThreadId, operation, stage, detail));
        }
        catch { }
    }

    private static void WriteLoop()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VictusFanControl", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"ec-wmi-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.log");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(new { schema = 1, kind = "session", pid = Environment.ProcessId,
                utc = DateTimeOffset.UtcNow, tickMs = Environment.TickCount64, qpc = Stopwatch.GetTimestamp(),
                qpcFrequency = Stopwatch.Frequency, executable = Environment.ProcessPath,
                coreMvid = typeof(EcWmiInvestigationTrace).Assembly.ManifestModule.ModuleVersionId,
                hardwareReadsAdded = false, nativeCancellation = false,
                maximumBytes = MaximumFileBytes, queueCapacity = Buffer.Capacity }));
            writer.Flush();
            var writtenBytes = file.Position;
            long observedDrops = 0;
            while (Enabled)
            {
                for (var batch = 0; batch < 256 && Buffer.TryTake(out var record); batch++)
                {
                    var line = JsonSerializer.Serialize(record);
                    var bytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                    if (writtenBytes + bytes > MaximumFileBytes - 1024)
                    {
                        writer.WriteLine("{\"kind\":\"capture-limit-reached\",\"complete\":false}");
                        Volatile.Write(ref _stopped, 1);
                        break;
                    }
                    writer.WriteLine(line);
                    writtenBytes += bytes;
                }
                var drops = Buffer.Dropped;
                if (drops != observedDrops)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { kind = "records-dropped", total = drops }));
                    writtenBytes += 128; // Conservative allowance for loss metadata.
                    observedDrops = drops;
                }
                writer.Flush();
                Thread.Sleep(200);
            }
        }
        catch { /* Unavailable/full disk is a diagnostic failure only. */ }
        finally { Volatile.Write(ref _stopped, 1); }
    }
}

internal sealed record InvestigationTraceRecord(DateTimeOffset Utc, long TickMs, long Qpc,
    int ManagedThreadId, long Operation, string Stage, string Detail);

internal sealed class InvestigationTraceBuffer(int capacity)
{
    private readonly ConcurrentQueue<InvestigationTraceRecord> _records = new();
    private int _pending;
    private long _dropped;
    internal int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    internal long Dropped => Interlocked.Read(ref _dropped);

    internal bool TryAdd(InvestigationTraceRecord record)
    {
        if (Interlocked.Increment(ref _pending) > Capacity)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return false;
        }
        _records.Enqueue(record);
        return true;
    }

    internal bool TryTake(out InvestigationTraceRecord? record)
    {
        if (!_records.TryDequeue(out record)) return false;
        Interlocked.Decrement(ref _pending);
        return true;
    }
}
