using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Runtime;

internal static class WmiFanGuiGuardianHost
{
    public static void RequestStop(string directory, string reason)
    {
        var path = Path.Combine(directory, "stop.signal");
        if (File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(reason); stream.Write(bytes); stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path)) { /* An existing durable fence is already a stop request. */ }
    }

    internal static string LeasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VictusFanControl", "WmiFanGui", "lease.json");
    internal static string LegacyLeasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VictusFanControl", "WatchdogM4", "state", "lease.json");

    internal static async Task<int> RunAsync(string[] args, bool fixture = false)
    {
        // Separate explicit fixture entry point never initializes hardware.
        if (args.Length != 7 || args[1] != "--session-dir" || args[3] != "--owner-pid" || args[5] != "--owner-start")
            throw new ArgumentException("Invalid WMI GUI guardian arguments.");
        var directory = Path.GetFullPath(args[2]);
        var ownerPid = int.Parse(args[4]);
        var ownerStart = long.Parse(args[6]);
        using var owner = Process.GetProcessById(ownerPid);
        if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != ownerStart)
            throw new InvalidOperationException("WMI guardian owner identity mismatch.");
        if (!fixture && HpHardwareTargetResolver.Resolve(HardwareIdentityReader.ReadCurrent(), out _) != Hp8C40TargetProfile.Instance)
            throw new InvalidOperationException("WMI guardian requires exact HP 8C40/F.18.");
        var leasePath = fixture ? Path.Combine(directory, "fixture-lease.json") : LeasePath;
        if (!fixture && (File.Exists(LegacyLeasePath) || File.Exists(WmiFanExperiment.LeasePath)))
            throw new InvalidOperationException("Pending legacy/experimental fan lease blocks WMI GUI control.");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        if (!fixture) { WmiFanExperimentBoundary.Enable(directory, true); WmiFanExperimentBoundary.BeginRecovery(); }
        FileStream? lease = null;
        var retired = false;
        var releaseAccepted = false;
        var legacyAccepted = false;
        string? failure = null;
        var reason = "STARTUP_FAILURE";
        var baseline = 0L;
        try
        {
            // Durable and exclusive, retained on uncertain native completion.
            lease = new FileStream(leasePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(lease, new { OwnerPid = ownerPid, OwnerStartUtcTicks = ownerStart,
                SessionDirectory = directory, GuardianPid = Environment.ProcessId, DirectEcProhibited = true });
            lease.Flush(true);
            if (!fixture) baseline = LatestSystemRecord();
            using var current = Process.GetCurrentProcess();
            WmiFanExperiment.WriteJson(Path.Combine(directory, "ready.json"), new
            {
                OwnerPid = ownerPid, OwnerStartUtcTicks = ownerStart,
                GuardianPid = current.Id, GuardianStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
                DirectEcProhibited = true, LeasePath = leasePath
            });
            var clock = Stopwatch.StartNew();
            var monitor = new WmiFanHeartbeatMonitor(ownerPid);
            var previous = clock.Elapsed;
            while (true)
            {
                var now = clock.Elapsed;
                if (File.Exists(Path.Combine(directory, "stop.signal"))) { reason = "CLIENT_RELEASE"; break; }
                owner.Refresh();
                if (owner.HasExited) { reason = "OWNER_EXITED"; break; }
                if (now - previous > TimeSpan.FromSeconds(3)) { reason = "LIFECYCLE_GAP"; break; }
                previous = now;
                monitor.Observe(Path.Combine(directory, "heartbeat.json"), now);
                if (monitor.TimedOut(now)) { reason = "HEARTBEAT_EXPIRED"; break; }
                if (!fixture && HasAcpiFault(baseline)) { reason = "ACPI_13_OR_15"; break; }
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            // Close the parent's whitelist before recovery. The shared named
            // mutex and durable in-flight marker prevent overlapping a live or
            // unknown native call; a timeout is never firmware cancellation.
            if (lease is not null)
            {
                try
                {
                    RequestStop(directory, reason);
                    using var nativeSlot = WmiFanExperimentBoundary.EnterRecoverySlot(directory, TimeSpan.FromSeconds(10));
                    if (File.Exists(Path.Combine(directory, "write-intent.json")))
                    {
                        var recovery = new WmiFanSession(r =>
                        {
                            int rc;
                            if (fixture) rc = 0;
                            else rc = new HpOmenBiosWmiClient().Send(r);
                            if (rc == 0 && r.CommandType == 0x2E) releaseAccepted = true;
                            if (rc == 0 && r.CommandType == 0x1A) legacyAccepted = true;
                            return rc;
                        }, _ => throw new InvalidOperationException("Guardian cannot send normal targets."));
                        recovery.Recover(); // Attempts LegacyDefault even after rejected FF/FF.
                    }
                    // Even without a setter, a pending RPM query cannot be
                    // mistaken for a completed native transaction.
                    if (File.Exists(Path.Combine(directory, "native-inflight.json")) ||
                        File.Exists(Path.Combine(directory, "native-uncertain.signal")))
                        throw new InvalidOperationException("Native completion is unknown; lease retained.");
                    lease.Dispose(); lease = null;
                    File.Delete(leasePath);
                    retired = true;
                }
                catch (Exception ex) { failure = failure is null ? ex.Message : failure + "; recovery: " + ex.Message; }
                finally { lease?.Dispose(); }
            }
            WmiFanExperiment.WriteJson(Path.Combine(directory, "guardian-report.json"), new
            {
                SchemaVersion = 1, TargetProfileId = Hp8C40TargetProfile.Instance.Id,
                OwnerPid = ownerPid, OwnerStartUtcTicks = ownerStart, ExitReason = reason,
                ReleaseRequestAccepted = releaseAccepted, LegacyDefaultRequestAccepted = legacyAccepted,
                GuardianLeaseRetired = retired, IndependentFirmwareOwnershipVerified = false,
                DirectEcProhibited = true, Failure = failure
            });
        }
        return retired && failure is null ? 0 : 1;
    }

    private static long LatestSystemRecord()
    {
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName) { ReverseDirection = true });
        using var latest = reader.ReadEvent();
        return latest?.RecordId ?? throw new InvalidOperationException("System event cursor unavailable.");
    }
    private static bool HasAcpiFault(long baseline)
    {
        if (LatestSystemRecord() < baseline) throw new InvalidOperationException("System event log reset.");
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName,
            $"*[System[Provider[@Name='ACPI'] and (EventID=13 or EventID=15) and EventRecordID > {baseline}]]"));
        using var item = reader.ReadEvent();
        return item is not null;
    }
}
