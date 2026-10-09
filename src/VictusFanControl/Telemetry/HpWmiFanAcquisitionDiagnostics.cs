using System.Runtime.CompilerServices;

namespace VictusFanControl.Telemetry;

internal enum HpWmiFanAcquisitionPurpose
{
    Periodic,
    Control
}

internal sealed record HpWmiFanAcquisitionStatus(
    long Sequence,
    HpWmiFanAcquisitionPurpose Purpose,
    string Outcome,
    long RequestedAtMilliseconds,
    long? AdmittedAtMilliseconds,
    long? NativeStartedAtMilliseconds,
    long? NativeCompletedAtMilliseconds,
    long ObservedAtMilliseconds,
    long? QueueWaitMilliseconds,
    long? NativeDurationMilliseconds,
    long? TotalDurationMilliseconds,
    string? Detail);

internal sealed record HpWmiFanAcquisitionCounters(
    long Requests,
    long PeriodicNativeStarts,
    long ControlNativeStarts,
    long Accepted,
    long PeriodicAdmissionBusy,
    long AdmissionTimeouts,
    long LogicalTimeouts,
    long WaiterTimeouts,
    long WaiterCancellations,
    long NativeFailures,
    long ResponseRejects,
    long Expired,
    long SlowNative);

/// <summary>
/// Passive instrumentation for the shared HP WMI 20008h/2Dh acquisition lane.
/// It never owns the semaphore, schedules work or changes read/timeout policy.
/// State is scoped to the native admission semaphore so periodic and control
/// readers can be correlated without coupling their command-proof semantics.
/// </summary>
internal sealed class HpWmiFanAcquisitionDiagnostics
{
    internal const int SlowNativeThresholdMilliseconds = 1000;
    private const int MaximumNotices = 32;
    private static readonly ConditionalWeakTable<SemaphoreSlim, HpWmiFanAcquisitionDiagnostics> Slots = new();

    private readonly object _gate = new();
    private readonly Queue<string> _notices = new();
    private long _sequence;
    private long _requests;
    private long _periodicNativeStarts;
    private long _controlNativeStarts;
    private long _accepted;
    private long _periodicAdmissionBusy;
    private long _admissionTimeouts;
    private long _logicalTimeouts;
    private long _waiterTimeouts;
    private long _waiterCancellations;
    private long _nativeFailures;
    private long _responseRejects;
    private long _expired;
    private long _slowNative;
    private bool _degraded;
    private HpWmiFanAcquisitionStatus? _last;

    internal static HpWmiFanAcquisitionDiagnostics For(SemaphoreSlim slot) =>
        Slots.GetValue(slot, _ => new HpWmiFanAcquisitionDiagnostics());

    internal Operation Begin(HpWmiFanAcquisitionPurpose purpose, long requestedAtMilliseconds)
    {
        lock (_gate)
        {
            _requests++;
            return new Operation(++_sequence, purpose, requestedAtMilliseconds);
        }
    }

    internal void MarkAdmitted(Operation operation, long admittedAtMilliseconds)
    {
        lock (_gate)
        {
            if (operation.IsCompleted) return;
            operation.AdmittedAtMilliseconds ??= admittedAtMilliseconds;
        }
    }

    internal void MarkNativeStarted(Operation operation, long startedAtMilliseconds)
    {
        lock (_gate)
        {
            if (operation.IsCompleted || operation.NativeStartedAtMilliseconds.HasValue) return;
            operation.NativeStartedAtMilliseconds = startedAtMilliseconds;
            if (operation.Purpose == HpWmiFanAcquisitionPurpose.Periodic) _periodicNativeStarts++;
            else _controlNativeStarts++;
        }
    }

    internal void MarkNativeCompleted(Operation operation, long completedAtMilliseconds)
    {
        lock (_gate)
        {
            if (operation.IsCompleted) return;
            operation.NativeCompletedAtMilliseconds ??= completedAtMilliseconds;
        }
    }

    internal void MarkNativeDecoded(Operation operation, long observedAtMilliseconds)
    {
        lock (_gate)
        {
            if (operation.IsCompleted) return;
            operation.NativeDecodedAtMilliseconds = observedAtMilliseconds;
            if (operation.WaiterOutcome is not null)
            {
                FinishLocked(
                    operation,
                    $"native-completed-after-{operation.WaiterOutcome}",
                    observedAtMilliseconds,
                    "Caller stopped waiting before the native WMI result became usable.");
            }
        }
    }

    internal void MarkLogicalTimeout(Operation operation, long observedAtMilliseconds)
    {
        lock (_gate)
        {
            if (operation.IsCompleted || operation.LogicalTimeoutObserved) return;
            operation.LogicalTimeoutObserved = true;
            _logicalTimeouts++;
            _degraded = true;
            _last = SnapshotLocked(
                operation,
                "logical-timeout",
                observedAtMilliseconds,
                "Periodic logical timeout elapsed while the synchronous native call still owned the slot.");
            EnqueueLocked("WARN", _last);
        }
    }

    internal void MarkWaiterTimeout(Operation operation, long observedAtMilliseconds, string detail)
    {
        lock (_gate)
        {
            if (operation.IsCompleted || operation.WaiterOutcome is not null) return;
            operation.WaiterOutcome = "waiter-timeout";
            _waiterTimeouts++;
            _degraded = true;
            _last = SnapshotLocked(operation, operation.WaiterOutcome, observedAtMilliseconds, detail);
            EnqueueLocked("WARN", _last);
            if (operation.NativeDecodedAtMilliseconds.HasValue)
            {
                FinishLocked(
                    operation,
                    "native-completed-after-waiter-timeout",
                    observedAtMilliseconds,
                    "Native WMI result completed, but the control caller had already timed out.");
            }
        }
    }

    internal void MarkWaiterCanceled(Operation operation, long observedAtMilliseconds, string detail)
    {
        lock (_gate)
        {
            if (operation.IsCompleted || operation.WaiterOutcome is not null) return;
            operation.WaiterOutcome = "waiter-canceled";
            _waiterCancellations++;
            _last = SnapshotLocked(operation, operation.WaiterOutcome, observedAtMilliseconds, detail);
            EnqueueLocked("STATE", _last);
            if (operation.NativeDecodedAtMilliseconds.HasValue)
            {
                FinishLocked(
                    operation,
                    "native-completed-after-waiter-canceled",
                    observedAtMilliseconds,
                    "Native WMI result completed after the control caller was canceled.");
            }
        }
    }

    internal void Complete(
        Operation operation,
        string outcome,
        long observedAtMilliseconds,
        string? detail = null)
    {
        lock (_gate)
        {
            if (operation.IsCompleted) return;
            FinishLocked(operation, outcome, observedAtMilliseconds, detail);
        }
    }

    internal string Describe()
    {
        lock (_gate)
        {
            var last = _last is null
                ? "last=none"
                : $"last=seq={_last.Sequence},purpose={_last.Purpose},outcome={_last.Outcome}," +
                  $"queueMs={FormatMilliseconds(_last.QueueWaitMilliseconds)}," +
                  $"nativeMs={FormatMilliseconds(_last.NativeDurationMilliseconds)}," +
                  $"totalMs={FormatMilliseconds(_last.TotalDurationMilliseconds)}";

            return $"{last}; requests={_requests}; native=P{_periodicNativeStarts}/C{_controlNativeStarts}; " +
                   $"accepted={_accepted}; busyP={_periodicAdmissionBusy}; admissionTimeouts={_admissionTimeouts}; " +
                   $"logicalTimeouts={_logicalTimeouts}; waiterTimeouts={_waiterTimeouts}; " +
                   $"waiterCanceled={_waiterCancellations}; nativeFailures={_nativeFailures}; " +
                   $"responseRejects={_responseRejects}; expired={_expired}; slowNative={_slowNative}";
        }
    }

    internal HpWmiFanAcquisitionStatus? LastStatus
    {
        get { lock (_gate) return _last; }
    }

    internal HpWmiFanAcquisitionCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new(
                    _requests,
                    _periodicNativeStarts,
                    _controlNativeStarts,
                    _accepted,
                    _periodicAdmissionBusy,
                    _admissionTimeouts,
                    _logicalTimeouts,
                    _waiterTimeouts,
                    _waiterCancellations,
                    _nativeFailures,
                    _responseRejects,
                    _expired,
                    _slowNative);
            }
        }
    }

    internal IReadOnlyList<string> DrainNotices()
    {
        lock (_gate)
        {
            var result = _notices.ToArray();
            _notices.Clear();
            return result;
        }
    }

    private void FinishLocked(
        Operation operation,
        string outcome,
        long observedAtMilliseconds,
        string? detail)
    {
        if (operation.IsCompleted) return;
        operation.IsCompleted = true;

        var nativeDuration = Duration(
            operation.NativeStartedAtMilliseconds,
            operation.NativeCompletedAtMilliseconds);
        var slow = nativeDuration.HasValue &&
                   nativeDuration.Value >= SlowNativeThresholdMilliseconds;

        if (slow && !operation.SlowCounted)
        {
            operation.SlowCounted = true;
            _slowNative++;
        }

        var problem = outcome switch
        {
            "accepted" => false,
            "admission-busy" => false,
            "admission-canceled" => false,
            "canceled-before-native" => false,
            "lifecycle-discarded" => false,
            "native-completed-after-waiter-canceled" => false,
            _ => true
        };

        switch (outcome)
        {
            case "accepted":
                _accepted++;
                break;
            case "admission-busy":
                _periodicAdmissionBusy++;
                break;
            case "admission-timeout":
                _admissionTimeouts++;
                break;
            case "admission-canceled":
            case "canceled-before-native":
                _waiterCancellations++;
                break;
            case "native-failed":
            case "publish-failed":
                _nativeFailures++;
                break;
            case "response-rejected":
                _responseRejects++;
                break;
            case "expired":
                _expired++;
                break;
        }

        _last = SnapshotLocked(operation, outcome, observedAtMilliseconds, detail);

        if (problem)
        {
            _degraded = true;
            EnqueueLocked("WARN", _last);
            return;
        }

        if (slow)
        {
            EnqueueLocked("WARN", _last with
            {
                Detail = string.IsNullOrWhiteSpace(_last.Detail)
                    ? $"Native WMI acquisition exceeded {SlowNativeThresholdMilliseconds} ms."
                    : _last.Detail
            });
        }

        if (outcome == "accepted" && _degraded)
        {
            _degraded = false;
            EnqueueLocked("RECOVERED", _last with
            {
                Detail = "A fresh HP WMI fan acquisition succeeded after a previously observed acquisition fault."
            });
        }
    }

    private HpWmiFanAcquisitionStatus SnapshotLocked(
        Operation operation,
        string outcome,
        long observedAtMilliseconds,
        string? detail) =>
        new(
            operation.Sequence,
            operation.Purpose,
            outcome,
            operation.RequestedAtMilliseconds,
            operation.AdmittedAtMilliseconds,
            operation.NativeStartedAtMilliseconds,
            operation.NativeCompletedAtMilliseconds,
            observedAtMilliseconds,
            Duration(operation.RequestedAtMilliseconds, operation.AdmittedAtMilliseconds),
            Duration(operation.NativeStartedAtMilliseconds, operation.NativeCompletedAtMilliseconds),
            Duration(operation.RequestedAtMilliseconds, observedAtMilliseconds),
            detail);

    private void EnqueueLocked(string severity, HpWmiFanAcquisitionStatus status)
    {
        while (_notices.Count >= MaximumNotices) _notices.Dequeue();
        _notices.Enqueue(
            $"HP WMI ACQUIRE {severity}: seq={status.Sequence};purpose={status.Purpose};outcome={status.Outcome};" +
            $"queueMs={FormatMilliseconds(status.QueueWaitMilliseconds)};" +
            $"nativeMs={FormatMilliseconds(status.NativeDurationMilliseconds)};" +
            $"totalMs={FormatMilliseconds(status.TotalDurationMilliseconds)}" +
            (string.IsNullOrWhiteSpace(status.Detail) ? string.Empty : $";detail={status.Detail}"));
    }

    private static long? Duration(long? start, long? end) =>
        start.HasValue && end.HasValue && end.Value >= start.Value
            ? end.Value - start.Value
            : null;

    private static string FormatMilliseconds(long? value) =>
        value.HasValue ? value.Value.ToString() : "-";

    internal sealed class Operation
    {
        internal Operation(
            long sequence,
            HpWmiFanAcquisitionPurpose purpose,
            long requestedAtMilliseconds)
        {
            Sequence = sequence;
            Purpose = purpose;
            RequestedAtMilliseconds = requestedAtMilliseconds;
        }

        internal long Sequence { get; }
        internal HpWmiFanAcquisitionPurpose Purpose { get; }
        internal long RequestedAtMilliseconds { get; }
        internal long? AdmittedAtMilliseconds { get; set; }
        internal long? NativeStartedAtMilliseconds { get; set; }
        internal long? NativeCompletedAtMilliseconds { get; set; }
        internal long? NativeDecodedAtMilliseconds { get; set; }
        internal string? WaiterOutcome { get; set; }
        internal bool LogicalTimeoutObserved { get; set; }
        internal bool SlowCounted { get; set; }
        internal bool IsCompleted { get; set; }
    }
}
