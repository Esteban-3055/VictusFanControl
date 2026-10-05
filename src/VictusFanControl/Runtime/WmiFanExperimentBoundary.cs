using VictusFanControl.Hardware.Hp;
using VictusFanControl.Telemetry;
using VictusFanControl.Safety;

namespace VictusFanControl.Runtime;

/// <summary>Process-local whitelist plus cross-process serialization for the WMI experiment and GUI.</summary>
internal static class WmiFanExperimentBoundary
{
    internal static string? SessionDirectory { get; private set; }
    internal static bool Control { get; private set; }
    private static int _recovering;
    private static bool _gui;
    private static readonly object SessionGate = new();
    internal static Action? EnsureGuiGuardianAlive { get; set; }
    private sealed record Admission(TelemetrySnapshot Snapshot, Action EnsureThermalAllowed);
    private static Admission? _admission;
    internal static bool Enabled => SessionDirectory is not null;
    internal static bool Recovering => Volatile.Read(ref _recovering) != 0;
    internal static string StopPath => Path.Combine(SessionDirectory!, "stop.signal");
    private static string InFlightPath => Path.Combine(SessionDirectory!, "native-inflight.json");
    private static string UncertainPath => Path.Combine(SessionDirectory!, "native-uncertain.signal");

    internal static void Enable(string directory, bool control, bool gui = false)
    {
        lock (SessionGate)
        {
            if (Enabled) throw new InvalidOperationException("Experiment boundary is already installed.");
            SessionDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(SessionDirectory);
            Control = control;
            _gui = gui;
            Volatile.Write(ref _admission, null);
            Interlocked.Exchange(ref _recovering, 0);
            // Reuse the existing hard prohibition before EC module loading / I/O.
            WmiOnlyInvestigationPolicy.Enable();
        }
    }

    internal static void BeginRecovery() => Interlocked.Exchange(ref _recovering, 1);

    internal static void RearmGuiAfterSuccessfulRelease(
        string releasedDirectory,
        string nextDirectory)
    {
        lock (SessionGate)
        {
            if (!_gui || !Recovering || SessionDirectory is null)
                throw new InvalidOperationException("WMI GUI boundary is not in a rearmable recovery state.");

            var released = Path.GetFullPath(releasedDirectory);
            var current = Path.GetFullPath(SessionDirectory);
            if (!string.Equals(released, current, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WMI GUI release directory does not match the active boundary.");

            if (File.Exists(Path.Combine(released, "native-inflight.json")) ||
                File.Exists(Path.Combine(released, "native-uncertain.signal")))
                throw new InvalidOperationException("WMI GUI boundary cannot rearm while native completion is uncertain.");

            var next = Path.GetFullPath(nextDirectory);
            Directory.CreateDirectory(next);
            Volatile.Write(ref _admission, null);
            SessionDirectory = next;
            Control = true;
            _gui = true;
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    internal static void SetAdmission(TelemetrySnapshot snapshot, Action ensureThermalAllowed) =>
        Volatile.Write(ref _admission, new(snapshot, ensureThermalAllowed));

    internal static bool IsAllowed(HpBiosRequest r, bool control, bool recovering, bool stopped, bool gui = false)
    {
        if (r.Command != Hp8C40BiosFanControl.DefaultCommand || r.Payload is not { Length: 4 }) return false;
        if (r.CommandType == 0x2D && r.OutputSize == 128 && r.Payload.All(b => b == 0)) return true;
        if (!control || r.OutputSize != 0) return false;
        if (recovering)
            return (r.CommandType == 0x2E && r.Payload.SequenceEqual(new byte[] { 255, 255, 0, 0 })) ||
                (r.CommandType == 0x1A && r.Payload.SequenceEqual(new byte[] { 255, 0, 0, 0 }));
        return !stopped && r.CommandType == 0x2E && r.Payload[0] >= (gui ? Hp8C40TargetProfile.MinimumValidatedFanLevel : 30) && r.Payload[0] <= 50 &&
            r.Payload[1] == r.Payload[0] && r.Payload[2] == 0 && r.Payload[3] == 0;
    }

    internal static void EnsureRequestAllowed(HpBiosRequest request)
    {
        if (_gui && Recovering && request.CommandType == Hp8C40BiosFanControl.GetFanLevelCommandType)
            throw new InvalidOperationException("WMI GUI recovery has closed new fan reads; drain existing native work before release.");
        if (!IsAllowed(request, Control, Recovering, File.Exists(StopPath), gui: _gui))
            throw new InvalidOperationException("Request is outside the WMI fan experiment lifecycle/whitelist.");
        if (request.CommandType == 0x2E && request.Payload[0] != 255)
        {
            if (_gui)
            {
                EnsureGuiGuardianAlive?.Invoke();
                VictusFanControl.Control.FanDispatchAdmissionScope.EnsureAllowed();
                return;
            }
            var admission = Volatile.Read(ref _admission);
            var snapshot = admission?.Snapshot;
            var now = DateTimeOffset.UtcNow;
            if (snapshot is null || now < snapshot.Timestamp || now - snapshot.Timestamp > SafetyGate.MaximumTelemetryAge ||
                !snapshot.IsFanTelemetryFreshAt(now))
                throw new InvalidOperationException("Telemetry admission expired before native WMI fan dispatch.");
            // Shared preview, without counting this epoch again. Invoked after
            // mutex admission and again immediately before the native method.
            admission!.EnsureThermalAllowed();
        }
    }

    internal static HpBiosResponse Serialize(HpBiosRequest request, Func<HpBiosResponse> send)
    {
        // Held on the same synchronous thread until the native call returns.
        // The timeout on WaitOne is admission timeout, not firmware cancellation.
        using var mutex = new Mutex(false, @"Global\VictusFanControl.WmiFanExperiment.Native");
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException)
            {
                owned = true;
                // Native firmware completion is unknown after a process dies in-call.
                // Do not dispatch a new call on an abandoned serialization boundary.
                File.WriteAllText(UncertainPath, "Abandoned WMI mutex");
                throw new InvalidOperationException("Abandoned WMI mutex; native completion is unknown. Preserve the lease.");
            }
            if (!owned) throw new TimeoutException("WMI experiment native slot is still occupied; no overlapping call sent.");
            if (File.Exists(InFlightPath) || File.Exists(UncertainPath))
                throw new InvalidOperationException("Previous native completion is unknown; no further WMI call admitted.");
            EnsureRequestAllowed(request); // Recheck stop/lifecycle after waiting.
            return send();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    internal static void MarkNativeStart(HpBiosRequest request)
    {
        if (!Enabled) return;
        WmiFanExperiment.WriteJson(InFlightPath, new { Pid = Environment.ProcessId,
            Utc = DateTimeOffset.UtcNow, request.CommandType });
        File.AppendAllText(Path.Combine(SessionDirectory!, "native-dispatch.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId,
                timestampUtc = DateTimeOffset.UtcNow, commandType = request.CommandType,
                payload = request.Payload.Select(b => (int)b).ToArray(), recovering = Recovering }) + Environment.NewLine);
    }

    internal static void MarkNativeReturned()
    {
        if (Enabled) File.Delete(InFlightPath);
    }
}
