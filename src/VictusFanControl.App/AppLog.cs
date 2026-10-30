using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static class AppLog
{
    internal const int DefaultTailBytes = 2 * 1024 * 1024;
    internal const int ExtendedReviewTailBytes = 16 * 1024 * 1024;
    private static long _maxFileBytes = 5 * 1024 * 1024;
    private static int _exportTailBytes = DefaultTailBytes;
    private static readonly OptionalWorkQueue Writer = new();
    private const int MaximumEntryCharacters = 31*1024;
    internal static object WriterStatus => Writer.Status;
    internal static Task<bool> FlushAsync(TimeSpan budget) => Writer.FlushAsync(budget);
    private static bool _initialized;
    private static long _initializationRetryAt;
    internal static string? InitializationFailure { get; private set; }
    internal static bool QualificationCompatibilityLogEnabled { get; private set; }
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
        exportLogs = $"current-session-only;latest-{_exportTailBytes / (1024 * 1024)}-MiB-per-stream;rotation-keeps-one-previous-segment",
        rotationMaximumBytesPerSegment = _maxFileBytes
    };
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    // Explicit long review only. Bounded retention remains isolated to this process.
    internal static void EnableExtendedReviewDiagnostics()
    {
        Writer.TryEnqueue(() =>
        {
            _maxFileBytes = ExtendedReviewTailBytes / 2;
            _exportTailBytes = ExtendedReviewTailBytes;
            try
            {
                if (_initialized)
                    File.WriteAllText(Path.Combine(SessionDirectory, "session.json"), JsonSerializer.Serialize(SessionIdentity));
            }
            catch { /* Logging failures must not change control admission. */ }
        },65536);
    }

    public static void Initialize()
        => Writer.TryEnqueue(InitializeOnWriter,65536);
    private static void InitializeOnWriter()
    {
            if (_initialized || Environment.TickCount64 < _initializationRetryAt) return;
            InitializationFailure = TryInitializeSessionFiles(SessionDirectory, SessionIdentity);
            if (InitializationFailure is not null)
            {
                _initializationRetryAt = Environment.TickCount64 + 30000;
                return; // Optional logs cannot prevent the GUI from opening.
            }
            _initialized = true;
            Append(CurrentLogPath,$"{DateTimeOffset.Now:O}  VictusFanControl application session opened: {SessionId}{Environment.NewLine}");
    }

    internal static string? TryInitializeSessionFiles(string directory, object identity)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(identity));
            return null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        { return "El registro de diagnóstico no está disponible; revisa espacio y permisos. " + ex.Message; }
    }

    // Explicit legacy hardware harnesses retain their existing daily-log collector contract.
    // Product GUI never opts in; its exports always use the primary session path.
    internal static void EnableQualificationCompatibilityLog()
    {
        Writer.TryEnqueue(() =>
        {
            QualificationCompatibilityLogEnabled = true;
            WriteOnWriter("Qualification daily-log compatibility enabled for session: " + SessionId);
        },65536);
    }

    public static void Write(string message)
    {
        var captured=DateTimeOffset.Now;
        var bounded=message.Length>MaximumEntryCharacters?message[..MaximumEntryCharacters]+" [truncated]":message;
        Writer.TryEnqueue(()=>WriteOnWriter(bounded,captured),Math.Min(65536,bounded.Length*2+128));
    }

    private static void WriteOnWriter(string message,DateTimeOffset? captured=null)
    {
        InitializeOnWriter();if(!_initialized)return;
        var bounded=message.Length>MaximumEntryCharacters?message[..MaximumEntryCharacters]+" [truncated]":message;
        var line=$"{captured??DateTimeOffset.Now:O}  {bounded}{Environment.NewLine}";
        Append(CurrentLogPath,line);
        if(QualificationCompatibilityLogEnabled)Append(Path.Combine(LogDirectory,$"events-{DateTime.Now:yyyy-MM-dd}.log"),line);
    }

    internal static void WriteTelemetry(TelemetrySnapshot snapshot)
    {
        Writer.TryEnqueue(() =>
        {
                InitializeOnWriter();
                if (!_initialized) return;
                var text=JsonSerializer.Serialize(new { sessionId = SessionId, snapshot }, Json);
                // Do not write a truncated JSON row. Oversize optional payload is dropped.
                if(text.Length<=MaximumEntryCharacters)Append(TelemetryLogPath,text+Environment.NewLine);
                else Writer.RecordDrop();
        },65536);
    }

    internal static void WriteStructured(string eventName, object data)
    {
        var captured=DateTimeOffset.Now;
        Writer.TryEnqueue(()=>WriteOnWriter(eventName+": "+JsonSerializer.Serialize(data,Json),captured),65536);
    }

    private static void Append(string path, string text)
    {
        if (File.Exists(path) && new FileInfo(path).Length >= _maxFileBytes)
            File.Move(path, path + ".1", overwrite: true);
        File.AppendAllText(path, text);
    }

    // A consistent tail across rotation, never a fragment of a JSON row or UTF-8 code point.
    // No discovery of other sessions, recovery files, leases or journals.
    internal static string ReadTail(string path, int maximumBytes = DefaultTailBytes)
    {
        // Export only: allow a short best-effort flush, never an unlimited wait.
        Writer.FlushAsync(TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
        // Optional export has its own IO. Never take the writer's lock or stop acquisition.
            var segments = new[] { path + ".1", path }.Where(File.Exists).ToArray();
            var chunks = new List<byte[]>(); var remaining = maximumBytes; var truncated = false;
            foreach (var segment in Enumerable.Reverse(segments))
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
