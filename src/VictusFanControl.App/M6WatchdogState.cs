using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.App;

internal sealed record M6LeaseSnapshot(
    int SchemaVersion,
    string TargetProfileId,
    int ControllerProcessId,
    long ControllerStartUtcTicks,
    int Phase,
    long Generation,
    int? OwnedCpu,
    int? OwnedGpu);

internal sealed record M6WatchdogSnapshot(
    bool Ready,
    bool Blocked,
    int ProcessId,
    int SessionId,
    string AccountName,
    string TargetProfileId,
    string? RecoveryDisposition,
    string Detail,
    string JournalPath,
    bool JournalPresent,
    bool ProcessAlive,
    long ProcessStartUtcTicks);

internal static class M6WatchdogStateReader
{
    private static readonly string ServiceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VictusFanControl",
        "WatchdogM4");

    internal static readonly string StatusPath = Path.Combine(
        ServiceRoot,
        "state",
        "m4-8c40.status.json");

    internal static readonly string DefaultJournalPath = Path.Combine(
        ServiceRoot,
        "state",
        "lease.json");

    public static M6WatchdogSnapshot Read()
    {
        if (!File.Exists(StatusPath))
        {
            throw new InvalidOperationException(
                $"M6 watchdog status marker is missing: {StatusPath}");
        }

        M6WatchdogStatusDocument? status;
        try
        {
            status = JsonSerializer.Deserialize<M6WatchdogStatusDocument>(
                File.ReadAllText(StatusPath));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"M6 could not parse watchdog status marker '{StatusPath}'.",
                ex);
        }

        if (status is null)
        {
            throw new InvalidOperationException(
                $"M6 watchdog status marker '{StatusPath}' is empty or invalid.");
        }

        var journalPath = string.IsNullOrWhiteSpace(status.JournalPath)
            ? DefaultJournalPath
            : status.JournalPath;

        var processAlive = false;
        var observedSession = -1;
        var processStartUtcTicks = 0L;

        try
        {
            using var process = Process.GetProcessById(status.ProcessId);
            processAlive = !process.HasExited;

            if (processAlive)
            {
                observedSession = process.SessionId;
                processStartUtcTicks =
                    process.StartTime.ToUniversalTime().Ticks;
            }
        }
        catch (ArgumentException)
        {
            processAlive = false;
        }
        catch (InvalidOperationException)
        {
            processAlive = false;
        }

        if (processAlive &&
            observedSession != status.SessionId)
        {
            throw new InvalidOperationException(
                $"M6 watchdog status/process SessionId mismatch: status={status.SessionId}, process={observedSession}.");
        }

        return new M6WatchdogSnapshot(
            status.Ready,
            status.Blocked,
            status.ProcessId,
            status.SessionId,
            status.AccountName ?? string.Empty,
            status.TargetProfileId ?? string.Empty,
            status.RecoveryDisposition,
            status.Detail ?? string.Empty,
            journalPath,
            File.Exists(journalPath),
            processAlive,
            processStartUtcTicks);
    }

    public static void RequireReady(
        M6WatchdogSnapshot snapshot,
        int? expectedProcessId = null,
        long? expectedStartUtcTicks = null)
    {
        if (!snapshot.Ready ||
            snapshot.Blocked)
        {
            throw new InvalidOperationException(
                $"M6 requires watchdog Ready/unblocked; Ready={snapshot.Ready}, Blocked={snapshot.Blocked}, detail={snapshot.Detail}");
        }

        if (!string.Equals(
                snapshot.TargetProfileId,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"M6 requires watchdog target '{Hp8C40TargetProfile.Instance.Id}'; observed '{snapshot.TargetProfileId}'.");
        }

        if (snapshot.SessionId != 0)
        {
            throw new InvalidOperationException(
                $"M6 requires watchdog Session 0; observed {snapshot.SessionId}.");
        }

        if (!snapshot.AccountName.EndsWith(
                "SYSTEM",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"M6 requires LocalSystem watchdog status; account='{snapshot.AccountName}'.");
        }

        if (snapshot.ProcessId <= 0 ||
            !snapshot.ProcessAlive)
        {
            throw new InvalidOperationException(
                $"M6 watchdog status PID {snapshot.ProcessId} is not a live process.");
        }

        if (snapshot.ProcessStartUtcTicks <= 0)
        {
            throw new InvalidOperationException(
                "M6 watchdog process creation time is unavailable.");
        }

        if (expectedProcessId.HasValue &&
            snapshot.ProcessId != expectedProcessId.Value)
        {
            throw new InvalidOperationException(
                $"M6 requires the same watchdog process across normal Modern Standby; expected PID {expectedProcessId.Value}, observed {snapshot.ProcessId}.");
        }

        if (expectedStartUtcTicks.HasValue &&
            snapshot.ProcessStartUtcTicks != expectedStartUtcTicks.Value)
        {
            throw new InvalidOperationException(
                $"M6 requires the same watchdog creation time across Modern Standby; expected {expectedStartUtcTicks.Value}, observed {snapshot.ProcessStartUtcTicks}.");
        }
    }

    public static M6LeaseSnapshot ReadOwnedLease(
        M6WatchdogSnapshot watchdog)
    {
        if (!watchdog.JournalPresent)
        {
            throw new InvalidOperationException(
                $"M6 requires a durable OWNED journal at '{watchdog.JournalPath}'.");
        }

        using var document =
            JsonDocument.Parse(
                File.ReadAllText(watchdog.JournalPath));

        var root = document.RootElement;

        var schemaVersion =
            root.GetProperty("SchemaVersion").GetInt32();

        var targetProfileId =
            root.GetProperty("TargetProfileId").GetString() ??
            string.Empty;

        var controller =
            root.GetProperty("Controller");

        var phaseElement =
            root.GetProperty("Phase");

        var phase =
            phaseElement.ValueKind == JsonValueKind.Number
                ? phaseElement.GetInt32()
                : string.Equals(
                    phaseElement.GetString(),
                    "Owned",
                    StringComparison.Ordinal)
                    ? 2
                    : -1;

        var ownedCpu = (int?)null;
        var ownedGpu = (int?)null;

        if (root.TryGetProperty(
                "Owned",
                out var owned) &&
            owned.ValueKind != JsonValueKind.Null)
        {
            ownedCpu =
                owned.GetProperty("Cpu").GetInt32();
            ownedGpu =
                owned.GetProperty("Gpu").GetInt32();
        }

        return new M6LeaseSnapshot(
            schemaVersion,
            targetProfileId,
            controller.GetProperty("ProcessId").GetInt32(),
            controller.GetProperty("ProcessStartUtcTicks").GetInt64(),
            phase,
            root.GetProperty("Generation").GetInt64(),
            ownedCpu,
            ownedGpu);
    }

    public static void RequireOwned30(
        M6WatchdogSnapshot watchdog,
        int expectedControllerPid,
        long expectedControllerStartUtcTicks)
    {
        var lease =
            ReadOwnedLease(watchdog);

        if (lease.SchemaVersion != 2 ||
            !string.Equals(
                lease.TargetProfileId,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal) ||
            lease.ControllerProcessId != expectedControllerPid ||
            lease.ControllerStartUtcTicks != expectedControllerStartUtcTicks ||
            lease.Phase != 2 ||
            lease.Generation != 3 ||
            lease.OwnedCpu != 30 ||
            lease.OwnedGpu != 30)
        {
            throw new InvalidOperationException(
                $"M6 requires exact schema-v2 OWNED generation-3 30/30 journal for controller PID={expectedControllerPid}, startTicks={expectedControllerStartUtcTicks}; observed phase={lease.Phase}, generation={lease.Generation}, owned={lease.OwnedCpu?.ToString() ?? "null"}/{lease.OwnedGpu?.ToString() ?? "null"}.");
        }
    }

    public static void WriteDurableMarker(
        string path,
        string content)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                $"M6 marker path has no parent directory: {path}");

        Directory.CreateDirectory(directory);

        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);

        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            1024,
            leaveOpen: true);

        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private sealed record M6WatchdogStatusDocument(
        bool Ready,
        bool Blocked,
        int ProcessId,
        int SessionId,
        string? AccountName,
        string? TargetProfileId,
        string? RecoveryDisposition,
        string? Detail,
        string? JournalPath);
}
