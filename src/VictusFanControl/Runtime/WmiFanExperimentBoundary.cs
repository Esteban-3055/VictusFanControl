using VictusFanControl.Hardware.Hp;
using VictusFanControl.Telemetry;
using VictusFanControl.Safety;
using System.Text.Json;

namespace VictusFanControl.Runtime;

/// <summary>A read was rejected before native dispatch; its planned-release cause is captured at that boundary.</summary>
internal sealed class WmiFanReadAdmissionPausedException : InvalidOperationException
{
    internal bool PlannedRelease { get; }
    internal WmiFanReadAdmissionPausedException(bool plannedRelease) : base("WMI GUI recovery has closed new fan reads; drain existing native work before release.") => PlannedRelease=plannedRelease;
}

/// <summary>The invocation is accounted for; only its tracking file still needs retirement.</summary>
internal sealed class WmiNativeTrackingCleanupPendingException(string outcome, Exception inner)
    : IOException($"Native operation {outcome}; marker cleanup pending. No further WMI call admitted until cleanup succeeds.", inner) { }

/// <summary>Process-local whitelist plus cross-process serialization for the WMI experiment and GUI.</summary>
internal static class WmiFanExperimentBoundary
{
    internal static string? SessionDirectory { get; private set; }
    internal static bool Control { get; private set; }
    private static int _recovering;
    private static bool _gui;
    private static bool _productUpperRange;
    private static readonly object SessionGate = new();
    internal static Func<bool>? PlannedGuiRelease { get; set; }
    internal static Action? EnsureGuiGuardianAlive { get; set; }
    private sealed record Admission(TelemetrySnapshot Snapshot, Action EnsureThermalAllowed);
    private static Admission? _admission;
    private sealed record NativeInvocation(string Directory, byte[] MarkerBytes);
    private sealed record ConfirmedCompletion(NativeInvocation Invocation, string Outcome);
    // Process-local evidence only. A file, elapsed time or a lost owner cannot manufacture this receipt.
    private static ConfirmedCompletion? _confirmedCompletion;
    internal static bool Enabled => SessionDirectory is not null;
    internal static bool Recovering => Volatile.Read(ref _recovering) != 0;
    internal static string StopPath => Path.Combine(SessionDirectory!, "stop.signal");
    private static string InFlightPath => Path.Combine(SessionDirectory!, "native-inflight.json");
    private static string UncertainPath => Path.Combine(SessionDirectory!, "native-uncertain.signal");

    internal static void Enable(string directory, bool control, bool gui = false, bool productUpperRange = false)
    {
        if(productUpperRange && !gui) throw new ArgumentException("Upper range is a GUI trial only.");
        lock (SessionGate)
        {
            if (Enabled) throw new InvalidOperationException("Experiment boundary is already installed.");
            SessionDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(SessionDirectory);
            Control = control;
            _gui = gui;
            _productUpperRange = productUpperRange;
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
                File.Exists(Path.Combine(released, "native-uncertain.signal")) || Volatile.Read(ref _confirmedCompletion) is not null)
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

    internal static bool IsAllowed(HpBiosRequest r, bool control, bool recovering, bool stopped, bool gui = false, bool productUpperRange = false)
    {
        if (r.Command != Hp8C40BiosFanControl.DefaultCommand || r.Payload is not { Length: 4 }) return false;
        if (r.CommandType == 0x2D && r.OutputSize == 128 && r.Payload.All(b => b == 0)) return true;
        if (!control || r.OutputSize != 0) return false;
        if (recovering)
            return (r.CommandType == 0x2E && r.Payload.SequenceEqual(new byte[] { 255, 255, 0, 0 })) ||
                (r.CommandType == 0x1A && r.Payload.SequenceEqual(new byte[] { 255, 0, 0, 0 }));
        if (gui && productUpperRange)
            return !stopped && r.CommandType == 0x2E && Hp8C40ProductFanTargets.IsPair(r.Payload[0],r.Payload[1]) && r.Payload[2] == 0 && r.Payload[3] == 0;
        return !stopped && r.CommandType == 0x2E && r.Payload[0] >= (gui ? Hp8C40TargetProfile.MinimumValidatedFanLevel : 30) && r.Payload[0] <= 50 &&
            r.Payload[1] == r.Payload[0] && r.Payload[2] == 0 && r.Payload[3] == 0;
    }

    internal static void EnsureRequestAllowed(HpBiosRequest request)
    {
        if (_gui && (Recovering || File.Exists(StopPath)) && request.CommandType == Hp8C40BiosFanControl.GetFanLevelCommandType)
            throw new WmiFanReadAdmissionPausedException(PlannedGuiRelease?.Invoke()==true);
        if (!IsAllowed(request, Control, Recovering, File.Exists(StopPath), gui: _gui, productUpperRange: _productUpperRange))
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
            RetireConfirmedCompletion(SessionDirectory!);
            if (File.Exists(InFlightPath) || File.Exists(UncertainPath))
                throw new InvalidOperationException("Previous native completion is unknown; no further WMI call admitted.");
            EnsureRequestAllowed(request); // Recheck stop/lifecycle after waiting.
            return send();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    internal static void MarkNativeStart(HpBiosRequest request) => PublishNativeStart(request);

    private static NativeInvocation? PublishNativeStart(HpBiosRequest request)
    {
        if (!Enabled) return null;
        if (File.Exists(InFlightPath) || File.Exists(UncertainPath) || Volatile.Read(ref _confirmedCompletion) is not null)
            throw new InvalidOperationException("Previous native tracking is pending; no new native invocation admitted.");
        using var owner = System.Diagnostics.Process.GetCurrentProcess();
        var invocationId = Guid.NewGuid();
        var marker = new { Pid = Environment.ProcessId,
            OwnerStartUtcTicks = owner.StartTime.ToUniversalTime().Ticks,
            Utc = DateTimeOffset.UtcNow, request.CommandType, InvocationId = invocationId };
        var invocation = new NativeInvocation(SessionDirectory!, JsonSerializer.SerializeToUtf8Bytes(marker));
        WmiFanExperiment.WriteJson(InFlightPath, marker);
        try
        {
            File.AppendAllText(Path.Combine(SessionDirectory!, "native-dispatch.jsonl"),
            JsonSerializer.Serialize(new { pid = Environment.ProcessId, invocationId,
                timestampUtc = DateTimeOffset.UtcNow, commandType = request.CommandType,
                payload = request.Payload.Select(b => (int)b).ToArray(), recovering = Recovering }) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The native delegate has not been entered. This marker is also safe
            // to retire, but the original dispatch-log failure still rejects the call.
            ConfirmCompletion(invocation, "was not dispatched");
            throw;
        }
        return invocation;
    }

    private static void ConfirmCompletion(NativeInvocation invocation, string outcome)
    {
        Volatile.Write(ref _confirmedCompletion, new(invocation, outcome));
        RetireConfirmedCompletion(invocation.Directory);
    }

    // Only callers already holding the native mutex may retire a confirmed
    // marker. Keep exact bytes including a unique invocation ID, never just PID/age.
    private static void RetireConfirmedCompletion(string directory)
    {
        var receipt = Volatile.Read(ref _confirmedCompletion);
        if (receipt is null || !string.Equals(Path.GetFullPath(directory), receipt.Invocation.Directory, StringComparison.OrdinalIgnoreCase)) return;
        var markerPath = Path.Combine(directory, "native-inflight.json");
        if (File.Exists(Path.Combine(directory, "native-uncertain.signal")))
        {
            Interlocked.CompareExchange(ref _confirmedCompletion, null, receipt);
            throw new IOException("Native uncertainty signal overrides the local completion receipt; marker retained.");
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                try
                {
                    using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    var expected = receipt.Invocation.MarkerBytes;
                    var actual = new byte[expected.Length];
                    if (marker.Length == expected.Length) marker.ReadExactly(actual);
                    if (marker.Length != expected.Length || !actual.AsSpan().SequenceEqual(expected))
                    {
                        Interlocked.CompareExchange(ref _confirmedCompletion, null, receipt);
                        throw new InvalidDataException("Native marker changed; local completion receipt rejected and marker retained.");
                    }
                }
                catch (FileNotFoundException) { } // Already retired after a confirmed completion.
                File.Delete(markerPath);
                Interlocked.CompareExchange(ref _confirmedCompletion, null, receipt);
                return;
            }
            catch (InvalidDataException) { throw; }
            catch (IOException ex) when (attempt < 3 && ((ex.HResult & 0xffff) is 32 or 33))
            {
                Thread.Sleep(25); // Bounded tracking cleanup, never cancellation of native execution.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new WmiNativeTrackingCleanupPendingException(receipt.Outcome, ex);
            }
        }
    }

    internal static T InvokeNative<T>(HpBiosRequest request, Func<T> invoke)
    {
        var invocation = PublishNativeStart(request);
        var result = invoke();
        // A returned null/invalid response is still a completed invocation. Keep
        // response validation outside this boundary; exceptions from Invoke keep
        // the marker because their native completion has not been established.
        if (invocation is not null)
        {
            try { ConfirmCompletion(invocation, "returned"); }
            catch
            {
                // The caller will reject this acquisition. Do not leak a native
                // response object that its using statement has not received yet.
                if (result is IDisposable disposable) disposable.Dispose();
                throw;
            }
        }
        return result;
    }

    // Wait for a live native call to return before inspecting its transient
    // marker. Hold the same slot throughout recovery; nested Send calls on
    // this synchronous thread may acquire the Windows mutex recursively.
    internal static IDisposable EnterRecoverySlot(string directory, TimeSpan timeout, string? mutexName = null, bool waitForConfirmedCleanup = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var mutex = new Mutex(false, mutexName ?? @"Global\VictusFanControl.WmiFanExperiment.Native");
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException)
            {
                owned = true;
                File.WriteAllText(Path.Combine(directory, "native-uncertain.signal"), "Abandoned WMI mutex during release");
                throw new IOException("Native completion is unknown after owner exit; lease retained.");
            }
            if (!owned) throw new TimeoutException("Native WMI call did not drain; no recovery call admitted and lease retained.");
            while (true)
            {
                try { RetireConfirmedCompletion(directory); break; }
                catch (WmiNativeTrackingCleanupPendingException ex) when (waitForConfirmedCleanup &&
                    ex.InnerException is IOException io && ((io.HResult & 0xffff) is 32 or 33) && clock.Elapsed < timeout)
                {
                    // Keep the mutex across retries so neither a caller nor the
                    // detached guardian can classify this known-return marker
                    // between attempts. This never retries native execution.
                    Thread.Sleep((int)Math.Min(100, Math.Max(1, (timeout - clock.Elapsed).TotalMilliseconds)));
                }
            }
            if (File.Exists(Path.Combine(directory, "native-inflight.json")) || File.Exists(Path.Combine(directory, "native-uncertain.signal")))
                throw new IOException("Native completion is unknown; no recovery call admitted and lease retained.");
            return new NativeSlot(mutex);
        }
        catch
        {
            if (owned) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }
    internal static void PrepareGuiReleaseStop(string directory, TimeSpan timeout)
    {
        if (!_gui || !string.Equals(Path.GetFullPath(directory), SessionDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WMI GUI release does not match the active boundary.");
        BeginRecovery(); // Close admission before waiting for the outstanding call.
        try
        {
            using var slot = EnterRecoverySlot(directory, timeout, waitForConfirmedCleanup: true);
            // Drain only proven returned tracking before the detached guardian
            // inspects this directory without the owner's in-memory evidence.
            WmiFanGuiGuardianHost.RequestStop(directory, "CLIENT_RELEASE");
        }
        catch
        {
            WmiFanGuiGuardianHost.RequestStop(directory, "CLIENT_RELEASE_FAILED");
            throw;
        }
    }
    private sealed class NativeSlot(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
