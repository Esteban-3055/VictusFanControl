using System.Text.Json;

namespace VictusFanControl.Runtime;

/// <summary>Observes atomic worker publications without extending liveness on failed reads.</summary>
internal sealed class WmiFanHeartbeatMonitor(int workerPid)
{
    private long lastElapsed = -1;
    private TimeSpan lastHeartbeat;

    internal bool Observe(string path, TimeSpan now)
    {
        try
        {
            // The worker replaces the pathname, never modifies an existing file.
            // Allow deletion/replacement while parsing this complete old publication.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var heartbeat = JsonDocument.Parse(file);
            var root = heartbeat.RootElement;
            if (root.GetProperty("Pid").GetInt32() != workerPid)
                throw new InvalidOperationException("Wrong heartbeat PID.");
            var elapsed = root.GetProperty("ElapsedMs").GetInt64();
            if (elapsed < 0) throw new InvalidOperationException("Invalid heartbeat elapsed time.");
            if (elapsed <= lastElapsed) return false;
            lastElapsed = elapsed;
            lastHeartbeat = now;
            return true;
        }
        catch (FileNotFoundException) { return false; } // Not published yet, or pathname disappeared.
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // Retry on the next supervisor tick, with the ORIGINAL watchdog deadline.
            // Other I/O failures and malformed/wrong-PID data still reject the session.
            return false;
        }
    }

    internal bool TimedOut(TimeSpan now) =>
        now - lastHeartbeat > TimeSpan.FromSeconds(lastElapsed < 0 ? 30 : 8);

    internal static bool IsSharingViolation(IOException exception) =>
        exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
}
