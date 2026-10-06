using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static class AppLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static bool _initialized;
    internal static DateTimeOffset SessionStartedUtc { get; } = DateTimeOffset.UtcNow;
    internal static string SessionId { get; } = SessionStartedUtc.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "logs");
    internal static string SessionDirectory => Path.Combine(LogDirectory, "sessions", SessionId);
    public static string CurrentLogPath => Path.Combine(SessionDirectory, "events.log");
    internal static string TelemetryLogPath => Path.Combine(SessionDirectory, "telemetry.jsonl");
    internal static object SessionIdentity => new
    {
        schemaVersion = 1, sessionId = SessionId, startedUtc = SessionStartedUtc, processId = Environment.ProcessId,
        applicationVersion = typeof(AppLog).Assembly.GetName().Version?.ToString(),
        applicationMvid = typeof(AppLog).Assembly.ManifestModule.ModuleVersionId,
        coreMvid = typeof(TelemetrySnapshot).Assembly.ManifestModule.ModuleVersionId,
        scope = "one-application-process;automatic-activations-have-separate-ids",
        exportLogs = "current-session-only;latest-2-MiB-per-stream;rotation-keeps-one-previous-segment"
    };
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            Directory.CreateDirectory(SessionDirectory);
            File.WriteAllText(Path.Combine(SessionDirectory, "session.json"), JsonSerializer.Serialize(SessionIdentity));
            _initialized = true;
            Write("VictusFanControl application session opened: " + SessionId);
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Initialize();
                Append(CurrentLogPath, $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
            }
        }
        catch { /* Diagnostics must never crash monitoring. */ }
    }

    internal static void WriteTelemetry(TelemetrySnapshot snapshot)
    {
        try
        {
            lock (Gate)
            {
                Initialize();
                Append(TelemetryLogPath, JsonSerializer.Serialize(new { sessionId = SessionId, snapshot }, Json) + Environment.NewLine);
            }
        }
        catch { /* Preserve control even if the diagnostic disk is unavailable. */ }
    }

    private static void Append(string path, string text)
    {
        if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
            File.Move(path, path + ".1", overwrite: true);
        File.AppendAllText(path, text);
    }

    // A consistent tail across rotation, never a fragment of a JSON row or UTF-8 code point.
    // No discovery of other sessions, recovery files, leases or journals.
    internal static string ReadTail(string path, int maximumBytes = 2 * 1024 * 1024)
    {
        lock (Gate)
        {
            var segments = new[] { path + ".1", path }.Where(File.Exists).ToArray();
            var chunks = new List<byte[]>(); var remaining = maximumBytes; var truncated = false;
            foreach (var segment in segments.Reverse())
            {
                using var stream = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var size = (int)Math.Min(stream.Length, remaining);
                truncated = stream.Length > size;
                stream.Seek(-size, SeekOrigin.End); var bytes = new byte[size]; var read = 0;
                while (read < size) { var n = stream.Read(bytes, read, size - read); if (n == 0) break; read += n; }
                if (read != size) Array.Resize(ref bytes, read);
                chunks.Insert(0, bytes); remaining -= read;
                if (remaining == 0) break;
            }
            var merged = chunks.SelectMany(x => x).ToArray();
            var start = truncated ? Array.IndexOf(merged, (byte)'\n') + 1 : 0;
            if (truncated && start == 0) return "";
            var end = Array.LastIndexOf(merged, (byte)'\n') + 1;
            return end > start ? Encoding.UTF8.GetString(merged, start, end - start) : "";
        }
    }
}
