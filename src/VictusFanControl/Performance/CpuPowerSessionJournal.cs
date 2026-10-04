using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VictusFanControl.Performance;

internal enum CpuPowerJournalPhase
{
    WriteArmed,
    Owned,
    Contested,
    ReacquireWriteArmed,
    PresetSwitchWriteArmed,
    Stability,
    Yielded,
    Restoring,
    Unresolved
}

internal sealed record CpuPowerSessionJournalRecord(
    int SchemaVersion,
    string TargetProfileId,
    Guid SessionId,
    long Generation,
    CpuPowerJournalPhase Phase,
    CpuPowerLimitSnapshot OriginalBaseline,
    CpuPowerLimitRequest Request,
    ulong AppliedRaw,
    CpuPowerLimitSnapshot? ExternalHandoff,
    CpuPowerConflictSnapshot Conflict,
    ulong? PendingRaw,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    internal const int CurrentSchemaVersion = 1;
}

internal interface ICpuPowerSessionJournal
{
    string Path { get; }

    string TargetProfileId { get; }

    CpuPowerSessionJournalRecord? Load();

    void Store(CpuPowerSessionJournalRecord record);

    void Delete();
}

internal sealed class JsonCpuPowerSessionJournal : ICpuPowerSessionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _expectedTargetProfileId;

    internal JsonCpuPowerSessionJournal(
        string path,
        string expectedTargetProfileId)
    {
        if (string.IsNullOrWhiteSpace(expectedTargetProfileId))
            throw new ArgumentException(
                "CPU power journal requires an exact target profile id.",
                nameof(expectedTargetProfileId));

        Path = System.IO.Path.GetFullPath(path);
        _expectedTargetProfileId = expectedTargetProfileId;
    }

    public string Path { get; }

    public string TargetProfileId => _expectedTargetProfileId;

    public CpuPowerSessionJournalRecord? Load()
    {
        if (!File.Exists(Path))
            return null;

        try
        {
            using var stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite |
                FileShare.Delete,
                4096,
                FileOptions.SequentialScan);

            var record =
                JsonSerializer.Deserialize<CpuPowerSessionJournalRecord>(
                    stream,
                    JsonOptions);

            if (record is null)
                throw new InvalidDataException(
                    "CPU power journal deserialized to null.");

            Validate(record);
            return record;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "CPU power journal contains malformed JSON.",
                ex);
        }
    }

    public void Store(CpuPowerSessionJournalRecord record)
    {
        Validate(record);

        var directory =
            System.IO.Path.GetDirectoryName(Path) ??
            throw new InvalidOperationException(
                "CPU power journal path has no parent directory.");

        Directory.CreateDirectory(directory);

        var temporary =
            Path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream = new FileStream(
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

                stream.Flush(flushToDisk: true);
            }

            DurableReplace(temporary, Path);
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

    private void Validate(CpuPowerSessionJournalRecord record)
    {
        if (record.SchemaVersion !=
            CpuPowerSessionJournalRecord.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported CPU power journal schema {record.SchemaVersion}; expected {CpuPowerSessionJournalRecord.CurrentSchemaVersion}.");
        }

        if (!string.Equals(
                record.TargetProfileId,
                _expectedTargetProfileId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CPU power journal target '{record.TargetProfileId}' does not match expected target '{_expectedTargetProfileId}'.");
        }

        if (record.SessionId == Guid.Empty)
            throw new InvalidDataException(
                "CPU power journal has an empty session id.");

        if (record.Generation <= 0)
            throw new InvalidDataException(
                "CPU power journal generation must be positive.");

        if (record.UpdatedAtUtc < record.CreatedAtUtc)
            throw new InvalidDataException(
                "CPU power journal update time precedes creation time.");

        if (record.OriginalBaseline.Locked)
            throw new InvalidDataException(
                "CPU power journal baseline must not be locked.");

        if (!ValidRequest(record.Request))
            throw new InvalidDataException(
                "CPU power journal contains an invalid power request.");

        if (record.AppliedRaw == record.OriginalBaseline.Raw)
            throw new InvalidDataException(
                "CPU power journal applied raw must differ from the original baseline.");

        ValidateConflict(record.Conflict);

        var valid = record.Phase switch
        {
            CpuPowerJournalPhase.WriteArmed =>
                record.ExternalHandoff is null &&
                record.PendingRaw == record.AppliedRaw &&
                record.Conflict.State == CpuPowerConflictState.Inactive &&
                !record.Conflict.AttemptInFlight,

            CpuPowerJournalPhase.Owned =>
                record.PendingRaw is null &&
                record.Conflict.State == CpuPowerConflictState.Inactive &&
                !record.Conflict.AttemptInFlight,

            CpuPowerJournalPhase.Contested =>
                record.ExternalHandoff.HasValue &&
                record.PendingRaw is null &&
                record.Conflict.State == CpuPowerConflictState.Contested &&
                !record.Conflict.AttemptInFlight,

            CpuPowerJournalPhase.ReacquireWriteArmed =>
                record.ExternalHandoff.HasValue &&
                record.PendingRaw.HasValue &&
                record.Conflict.State == CpuPowerConflictState.Contested &&
                record.Conflict.AttemptInFlight &&
                record.Conflict.AttemptsUsed is >= 1 and <=
                    CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,

            CpuPowerJournalPhase.PresetSwitchWriteArmed =>
                record.PendingRaw.HasValue &&
                record.PendingRaw.Value != record.AppliedRaw &&
                record.Conflict.State == CpuPowerConflictState.Inactive &&
                !record.Conflict.AttemptInFlight,

            CpuPowerJournalPhase.Stability =>
                record.ExternalHandoff.HasValue &&
                record.PendingRaw is null &&
                record.Conflict.State ==
                    CpuPowerConflictState.ReacquiredPendingStability &&
                !record.Conflict.AttemptInFlight,

            CpuPowerJournalPhase.Yielded =>
                record.ExternalHandoff.HasValue &&
                record.PendingRaw is null &&
                record.Conflict.State == CpuPowerConflictState.Yielded &&
                !record.Conflict.AttemptInFlight,

            CpuPowerJournalPhase.Restoring =>
                record.PendingRaw.HasValue &&
                !record.Conflict.AttemptInFlight,

            CpuPowerJournalPhase.Unresolved =>
                !record.Conflict.AttemptInFlight,

            _ => false
        };

        if (!valid)
        {
            throw new InvalidDataException(
                $"CPU power journal invariant failed for phase {record.Phase}.");
        }
    }

    private static void ValidateConflict(
        CpuPowerConflictSnapshot conflict)
    {
        if (conflict.MaxAttempts !=
            CpuPowerConflictPolicy.DefaultMaxReacquireAttempts)
        {
            throw new InvalidDataException(
                $"CPU power journal conflict budget must be exactly {CpuPowerConflictPolicy.DefaultMaxReacquireAttempts} attempts.");
        }

        if (conflict.AttemptsUsed < 0 ||
            conflict.AttemptsUsed > conflict.MaxAttempts)
        {
            throw new InvalidDataException(
                "CPU power journal conflict attempt count is outside its budget.");
        }

        if (conflict.State == CpuPowerConflictState.Inactive &&
            (conflict.AttemptsUsed != 0 ||
             conflict.AttemptInFlight ||
             conflict.FirstDetectedActiveMilliseconds.HasValue ||
             conflict.LastDetectedActiveMilliseconds.HasValue))
        {
            throw new InvalidDataException(
                "Inactive CPU power conflict snapshot contains active conflict state.");
        }

        if (conflict.AttemptInFlight &&
            (conflict.State != CpuPowerConflictState.Contested ||
             conflict.AttemptsUsed == 0))
        {
            throw new InvalidDataException(
                "CPU power journal has an impossible in-flight conflict attempt.");
        }
    }

    private static bool ValidRequest(
        CpuPowerLimitRequest request) =>
        double.IsFinite(request.Pl1Watts) &&
        double.IsFinite(request.Pl2Watts) &&
        request.Pl1Watts >= CpuPowerProductDefaults.MinimumPl1Watts &&
        request.Pl2Watts >= request.Pl1Watts;

    private static void DurableReplace(
        string source,
        string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        const int ErrorAccessDenied = 5;
        const int ErrorSharingViolation = 32;
        const int ErrorLockViolation = 33;
        const int MaxReplaceAttempts = 8;

        for (var attempt = 1;
             attempt <= MaxReplaceAttempts;
             attempt++)
        {
            if (MoveFileEx(
                    source,
                    destination,
                    MoveFileFlags.ReplaceExisting |
                    MoveFileFlags.WriteThrough))
            {
                return;
            }

            var error =
                Marshal.GetLastWin32Error();

            var transientShareFailure =
                error == ErrorAccessDenied ||
                error == ErrorSharingViolation ||
                error == ErrorLockViolation;

            if (!transientShareFailure ||
                attempt == MaxReplaceAttempts)
            {
                throw new Win32Exception(
                    error,
                    "Durable CPU power journal replace failed (Win32 " + error + ").");
            }

            Thread.Sleep(
                attempt * 10);
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
