using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VictusFanControl.Performance;

internal enum GpuClockJournalPhase
{
    ApplyWriteArmed,
    ActiveUnverified,
    PresetSwitchWriteArmed,
    ReleaseWriteArmed,
    RecoveryRequired
}

internal sealed record GpuClockSessionJournalRecord(
    int SchemaVersion,
    string TargetProfileId,
    Guid SessionId,
    long Generation,
    GpuClockJournalPhase Phase,
    GpuClockLimitRequest? CommittedRequest,
    GpuClockLimitRequest? PendingRequest,
    string? RecoveryReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    internal const int CurrentSchemaVersion = 1;
}

internal interface IGpuClockSessionJournal
{
    string Path { get; }

    string TargetProfileId { get; }

    GpuClockSessionJournalRecord? Load();

    void Store(GpuClockSessionJournalRecord record);

    void Delete();
}

/// <summary>
/// Durable GPU clock journal. It is intentionally independent from the CPU
/// RAPL journal because the two subsystems have different ownership semantics.
///
/// A persisted GPU record never authorizes automatic recovery writes. Any
/// record found after process restart is evidence only and puts the future
/// controller into RecoveryRequired.
/// </summary>
internal sealed class JsonGpuClockSessionJournal :
    IGpuClockSessionJournal
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    private readonly string _expectedTargetProfileId;

    internal JsonGpuClockSessionJournal(
        string path,
        string expectedTargetProfileId)
    {
        if (string.IsNullOrWhiteSpace(
                expectedTargetProfileId))
        {
            throw new ArgumentException(
                "GPU clock journal requires an exact target profile id.",
                nameof(expectedTargetProfileId));
        }

        Path =
            System.IO.Path.GetFullPath(
                path);

        _expectedTargetProfileId =
            expectedTargetProfileId;
    }

    public string Path { get; }

    public string TargetProfileId =>
        _expectedTargetProfileId;

    public GpuClockSessionJournalRecord? Load()
    {
        if (!File.Exists(Path))
            return null;

        try
        {
            using var stream =
                new FileStream(
                    Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                    FileShare.Delete,
                    4096,
                    FileOptions.SequentialScan);

            var record =
                JsonSerializer.Deserialize<GpuClockSessionJournalRecord>(
                    stream,
                    JsonOptions);

            if (record is null)
            {
                throw new InvalidDataException(
                    "GPU clock journal deserialized to null.");
            }

            Validate(record);
            return record;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "GPU clock journal contains malformed JSON.",
                ex);
        }
    }

    public void Store(
        GpuClockSessionJournalRecord record)
    {
        Validate(record);

        var directory =
            System.IO.Path.GetDirectoryName(
                Path) ??
            throw new InvalidOperationException(
                "GPU clock journal path has no parent directory.");

        Directory.CreateDirectory(
            directory);

        var temporary =
            Path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream =
                new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    record,
                    JsonOptions);

                stream.Flush(
                    flushToDisk: true);
            }

            DurableReplace(
                temporary,
                Path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public void Delete()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }

    private void Validate(
        GpuClockSessionJournalRecord record)
    {
        if (record.SchemaVersion !=
            GpuClockSessionJournalRecord.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported GPU clock journal schema {record.SchemaVersion}; expected {GpuClockSessionJournalRecord.CurrentSchemaVersion}.");
        }

        if (!string.Equals(
                record.TargetProfileId,
                _expectedTargetProfileId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"GPU clock journal target '{record.TargetProfileId}' does not match expected target '{_expectedTargetProfileId}'.");
        }

        if (record.SessionId ==
            Guid.Empty)
        {
            throw new InvalidDataException(
                "GPU clock journal has an empty session id.");
        }

        if (record.Generation <= 0)
        {
            throw new InvalidDataException(
                "GPU clock journal generation must be positive.");
        }

        if (record.UpdatedAtUtc <
            record.CreatedAtUtc)
        {
            throw new InvalidDataException(
                "GPU clock journal update time precedes creation time.");
        }

        if (record.CommittedRequest.HasValue)
            ValidateRequest(
                record.CommittedRequest.Value);

        if (record.PendingRequest.HasValue)
            ValidateRequest(
                record.PendingRequest.Value);

        var valid =
            record.Phase switch
            {
                GpuClockJournalPhase.ApplyWriteArmed =>
                    !record.CommittedRequest.HasValue &&
                    record.PendingRequest.HasValue &&
                    string.IsNullOrEmpty(
                        record.RecoveryReason),

                GpuClockJournalPhase.ActiveUnverified =>
                    record.CommittedRequest.HasValue &&
                    !record.PendingRequest.HasValue &&
                    string.IsNullOrEmpty(
                        record.RecoveryReason),

                GpuClockJournalPhase.PresetSwitchWriteArmed =>
                    record.CommittedRequest.HasValue &&
                    record.PendingRequest.HasValue &&
                    record.CommittedRequest.Value !=
                        record.PendingRequest.Value &&
                    string.IsNullOrEmpty(
                        record.RecoveryReason),

                GpuClockJournalPhase.ReleaseWriteArmed =>
                    record.CommittedRequest.HasValue &&
                    !record.PendingRequest.HasValue &&
                    string.IsNullOrEmpty(
                        record.RecoveryReason),

                GpuClockJournalPhase.RecoveryRequired =>
                    (record.CommittedRequest.HasValue ||
                     record.PendingRequest.HasValue) &&
                    !string.IsNullOrWhiteSpace(
                        record.RecoveryReason),

                _ => false
            };

        if (!valid)
        {
            throw new InvalidDataException(
                $"GPU clock journal invariant failed for phase {record.Phase}.");
        }
    }

    private static void ValidateRequest(
        GpuClockLimitRequest request)
    {
        if (request.MinGraphicsClockMHz == 0 ||
            request.MaxGraphicsClockMHz == 0 ||
            request.MaxGraphicsClockMHz <
                request.MinGraphicsClockMHz)
        {
            throw new InvalidDataException(
                "GPU clock journal contains an invalid clock request.");
        }
    }

    private static void DurableReplace(
        string source,
        string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(
                source,
                destination,
                overwrite: true);

            return;
        }

        if (!MoveFileEx(
                source,
                destination,
                MoveFileFlags.ReplaceExisting |
                MoveFileFlags.WriteThrough))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Durable GPU clock journal replace failed.");
        }
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x00000001,
        WriteThrough = 0x00000008
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        MoveFileFlags flags);
}
