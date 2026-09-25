using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VictusFanControl.Watchdog;

internal interface ILeaseJournal
{
    string Path { get; }

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

    public JsonLeaseJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

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
                // Readers must not block the same-directory atomic
                // MoveFileEx replacement used by StoreAsync. Sharing DELETE
                // lets a reader finish against the old file object while the
                // live path is replaced with the next durable generation.
                FileShare.Read | FileShare.Delete,
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

            ValidateRecord(record);
            return record;
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
        ValidateRecord(record);

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

                // WRITE_ARMED may be acknowledged only after the durable state
                // is flushed through the OS storage stack.
                stream.Flush(flushToDisk: true);
            }

            // Same-directory MoveFileEx keeps the critical transition on one
            // volume. WRITE_THROUGH also waits for the rename/replace metadata
            // to reach disk before StoreAsync can acknowledge WRITE_ARMED.
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

    internal static void ValidateRecord(
        WatchdogLeaseRecord record)
    {
        if (record.SchemaVersion !=
            WatchdogLeaseRecord.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported lease journal schema {record.SchemaVersion}.");
        }

        if (record.SessionId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Lease journal has an empty session id.");
        }

        if (record.Controller.ProcessId <= 0 ||
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
                !setpoint.Value.IsValidatedCustom)
            {
                throw new InvalidDataException(
                    $"Lease journal contains an out-of-range custom setpoint {setpoint.Value}.");
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
}
