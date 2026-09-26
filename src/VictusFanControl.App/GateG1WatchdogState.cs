using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VictusFanControl.App;

internal sealed record GateG1WatchdogSnapshot(
    bool Ready,
    bool Blocked,
    int ProcessId,
    int SessionId,
    string AccountName,
    string? RecoveryDisposition,
    string Detail,
    string JournalPath,
    bool JournalPresent,
    bool ProcessAlive);

internal static class GateG1WatchdogStateReader
{
    private static readonly string ServiceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VictusFanControl",
        "WatchdogService");

    internal static readonly string StatusPath = Path.Combine(
        ServiceRoot,
        "state",
        "gate-d.status.json");

    internal static readonly string DefaultJournalPath = Path.Combine(
        ServiceRoot,
        "state",
        "lease.json");

    public static GateG1WatchdogSnapshot Read()
    {
        if (!File.Exists(StatusPath))
        {
            throw new InvalidOperationException(
                $"Gate G1 watchdog status marker is missing: {StatusPath}");
        }

        GateG1WatchdogStatusDocument? status;
        try
        {
            status = JsonSerializer.Deserialize<GateG1WatchdogStatusDocument>(
                File.ReadAllText(StatusPath));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Gate G1 could not parse watchdog status marker '{StatusPath}'.",
                ex);
        }

        if (status is null)
        {
            throw new InvalidOperationException(
                $"Gate G1 watchdog status marker '{StatusPath}' is empty or invalid.");
        }

        var journalPath = string.IsNullOrWhiteSpace(status.JournalPath)
            ? DefaultJournalPath
            : status.JournalPath;

        var processAlive = false;
        var observedSession = -1;

        try
        {
            using var process = Process.GetProcessById(status.ProcessId);
            processAlive = !process.HasExited;
            if (processAlive)
            {
                observedSession = process.SessionId;
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
                $"Gate G1 watchdog status/process SessionId mismatch: status={status.SessionId}, process={observedSession}.");
        }

        return new GateG1WatchdogSnapshot(
            status.Ready,
            status.Blocked,
            status.ProcessId,
            status.SessionId,
            status.AccountName ?? string.Empty,
            status.RecoveryDisposition,
            status.Detail ?? string.Empty,
            journalPath,
            File.Exists(journalPath),
            processAlive);
    }

    public static void RequireReady(
        GateG1WatchdogSnapshot snapshot,
        int? expectedProcessId = null)
    {
        if (!snapshot.Ready ||
            snapshot.Blocked)
        {
            throw new InvalidOperationException(
                $"Gate G1 requires watchdog Ready/unblocked; Ready={snapshot.Ready}, Blocked={snapshot.Blocked}, detail={snapshot.Detail}");
        }

        if (snapshot.SessionId != 0)
        {
            throw new InvalidOperationException(
                $"Gate G1 requires watchdog Session 0; observed {snapshot.SessionId}.");
        }

        if (!snapshot.AccountName.EndsWith(
                "SYSTEM",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Gate G1 requires LocalSystem watchdog status; account='{snapshot.AccountName}'.");
        }

        if (snapshot.ProcessId <= 0 ||
            !snapshot.ProcessAlive)
        {
            throw new InvalidOperationException(
                $"Gate G1 watchdog status PID {snapshot.ProcessId} is not a live process.");
        }

        if (expectedProcessId.HasValue &&
            snapshot.ProcessId != expectedProcessId.Value)
        {
            throw new InvalidOperationException(
                $"Gate G1 requires the same watchdog process across normal suspend/resume; expected PID {expectedProcessId.Value}, observed {snapshot.ProcessId}.");
        }
    }

    public static void WriteDurableMarker(
        string path,
        string content)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                $"Gate G1 marker path has no parent directory: {path}");

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

    private sealed record GateG1WatchdogStatusDocument(
        bool Ready,
        bool Blocked,
        int ProcessId,
        int SessionId,
        string? AccountName,
        string? RecoveryDisposition,
        string? Detail,
        string? JournalPath);
}
