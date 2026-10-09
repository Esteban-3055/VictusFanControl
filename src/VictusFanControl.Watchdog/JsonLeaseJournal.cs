using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VictusFanControl.Watchdog;

internal interface ILeaseJournal
{
    string Path { get; }

    WatchdogTargetPolicy TargetPolicy { get; }

    ValueTask<WatchdogLeaseRecord?> LoadAsync(
        CancellationToken cancellationToken);

    ValueTask StoreAsync(
        WatchdogLeaseRecord record,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        CancellationToken cancellationToken);
}

internal sealed class JsonLeaseJournal : ILeaseJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public JsonLeaseJournal(
        string path,
        WatchdogTargetPolicy targetPolicy)
    {
        Path = System.IO.Path.GetFullPath(path);
        TargetPolicy =
            targetPolicy ??
            throw new ArgumentNullException(nameof(targetPolicy));
    }

    public string Path { get; }

    public WatchdogTargetPolicy TargetPolicy { get; }

    public async ValueTask<WatchdogLeaseRecord?> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);

            var record =
                await JsonSerializer.DeserializeAsync<WatchdogLeaseRecord>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

            if (record is null)
            {
                throw new InvalidDataException(
                    "Lease journal deserialized to null.");
            }

            return NormalizeLoadedRecord(record);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Lease journal contains malformed JSON.",
                ex);
        }
    }

    public async ValueTask StoreAsync(
        WatchdogLeaseRecord record,
        CancellationToken cancellationToken)
    {
        ValidateCurrentRecord(record);

        var directory =
            System.IO.Path.GetDirectoryName(Path) ??
            throw new InvalidOperationException(
                "Lease journal path has no parent directory.");

        Directory.CreateDirectory(directory);

        var temp =
            Path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    record,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);

                stream.Flush(flushToDisk: true);
            }

            if (!MoveFileEx(
                    temp,
                    Path,
                    MoveFileFlags.ReplaceExisting |
                    MoveFileFlags.WriteThrough))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Durable lease journal replace failed.");
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public ValueTask DeleteAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        return ValueTask.CompletedTask;
    }

    private WatchdogLeaseRecord NormalizeLoadedRecord(
        WatchdogLeaseRecord record)
    {
        if (record.SchemaVersion ==
            WatchdogLeaseRecord.LegacySchemaVersion)
        {
            return NormalizeLegacyV1(record);
        }

        ValidateCurrentRecord(record);
        return record;
    }

    private WatchdogLeaseRecord NormalizeLegacyV1(
        WatchdogLeaseRecord record)
    {
        if (!TargetPolicy.LegacySchemaV1Compatible)
        {
            throw new InvalidDataException(
                $"Legacy lease journal schema 1 has no target identity and cannot be interpreted for '{TargetPolicy.TargetProfileId}'.");
        }

        if (!string.IsNullOrWhiteSpace(record.TargetProfileId))
        {
            throw new InvalidDataException(
                "Legacy lease journal schema 1 unexpectedly contains target identity; refusing ambiguous migration.");
        }

        ValidateCommonRecord(
            record,
            static setpoint =>
                setpoint.Cpu is >= 14 and <= 50 &&
                setpoint.Gpu is >= 14 and <= 50,
            "legacy HP 88F8 independent 14-50");

        var normalized = record with
        {
            SchemaVersion =
                WatchdogLeaseRecord.CurrentSchemaVersion,
            TargetProfileId =
                TargetPolicy.TargetProfileId
        };

        ValidateCurrentRecord(normalized);
        return normalized;
    }

    private void ValidateCurrentRecord(
        WatchdogLeaseRecord record)
    {
        if (record.SchemaVersion !=
            WatchdogLeaseRecord.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported lease journal schema {record.SchemaVersion}; expected {WatchdogLeaseRecord.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(record.TargetProfileId))
        {
            throw new InvalidDataException(
                "Lease journal schema 2 is missing target profile identity.");
        }

        if (!string.Equals(
                record.TargetProfileId,
                TargetPolicy.TargetProfileId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Lease journal target '{record.TargetProfileId}' does not match active watchdog target '{TargetPolicy.TargetProfileId}'. No ownership inference or restore is permitted.");
        }

        ValidateCommonRecord(
            record,
            TargetPolicy.IsValidatedCustom,
            TargetPolicy.DescribeCustomEnvelope());
    }

    private static void ValidateCommonRecord(
        WatchdogLeaseRecord record,
        Func<FanSetpoint, bool> setpointValidator,
        string envelopeDescription)
    {
        if (record.SessionId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Lease journal has an empty session id.");
        }

        if (record.Controller is null ||
            record.Controller.ProcessId <= 0 ||
            record.Controller.ProcessStartUtcTicks <= 0)
        {
            throw new InvalidDataException(
                "Lease journal has an invalid controller identity.");
        }

        if (record.Generation <= 0)
        {
            throw new InvalidDataException(
                "Lease journal generation must be positive.");
        }

        foreach (var setpoint in new[]
                 {
                     record.PreviousOwned,
                     record.Pending,
                     record.Owned
                 })
        {
            if (setpoint.HasValue &&
                !setpointValidator(setpoint.Value))
            {
                throw new InvalidDataException(
                    $"Lease journal contains custom setpoint {setpoint.Value} outside the target policy ({envelopeDescription}).");
            }
        }

        var valid = record.Phase switch
        {
            WatchdogLeasePhase.Prepared =>
                record.PreviousOwned is null &&
                record.Pending is null &&
                record.Owned is null,

            WatchdogLeasePhase.WriteArmed =>
                record.Pending.HasValue &&
                record.Owned is null,

            WatchdogLeasePhase.Owned =>
                record.PreviousOwned is null &&
                record.Pending is null &&
                record.Owned.HasValue,

            WatchdogLeasePhase.Restoring =>
                record.PreviousOwned.HasValue ||
                record.Pending.HasValue ||
                record.Owned.HasValue,

            _ => false
        };

        if (!valid)
        {
            throw new InvalidDataException(
                $"Lease journal invariant failed for phase {record.Phase}.");
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
