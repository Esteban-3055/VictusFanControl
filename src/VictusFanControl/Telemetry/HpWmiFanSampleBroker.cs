using System.Runtime.CompilerServices;

namespace VictusFanControl.Telemetry;

/// <summary>
/// Process-wide native read coordination, shared across both reader lifetimes.
/// This first stage owns admission and native completion only: decoding,
/// freshness, publication and command proof remain in the reader adapters.
/// It stores no samples and does not change SemaphoreSlim scheduling priority.
/// </summary>
internal sealed class HpWmiFanSampleBroker
{
    private static readonly ConditionalWeakTable<SemaphoreSlim, HpWmiFanSampleBroker> Brokers = new();
    internal static HpWmiFanSampleBroker Production { get; } = For(new SemaphoreSlim(1, 1));

    private HpWmiFanSampleBroker(SemaphoreSlim admission)
    {
        Admission = admission;
        Diagnostics = HpWmiFanAcquisitionDiagnostics.For(admission);
    }

    internal static HpWmiFanSampleBroker For(SemaphoreSlim admission) =>
        Brokers.GetValue(admission, static slot => new HpWmiFanSampleBroker(slot));

    // Retain the existing identity used by publication and passive diagnostics.
    internal SemaphoreSlim Admission { get; }
    internal HpWmiFanAcquisitionDiagnostics Diagnostics { get; }

    internal NativeReadLease? TryAcquirePeriodic() =>
        Admission.Wait(0) ? new NativeReadLease(this) : null;

    internal async ValueTask<NativeReadLease?> AcquireControlAsync(
        TimeSpan maximumWait, CancellationToken cancellationToken) =>
        await Admission.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false)
            ? new NativeReadLease(this)
            : null;

    internal Task RunNative(NativeReadLease lease, Action query) =>
        RunNative(lease, () => { query(); return true; });

    internal Task<T> RunNative<T>(NativeReadLease lease, Func<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lease.TransferToNative(this);
        try
        {
            // Never pass a caller cancellation token to this task. Once queued,
            // only its finally can release admission, including late completion.
            return Task.Run(() =>
            {
                try { return query(); }
                finally { lease.ReleaseAfterNative(); }
            });
        }
        catch
        {
            // Task scheduling failed before any native worker could own the lease.
            lease.ReleaseAfterNative();
            throw;
        }
    }

    internal async Task WaitForQuiescenceAsync(CancellationToken cancellationToken)
    {
        await Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        Admission.Release();
    }

    /// <summary>
    /// One reservation, transferred exactly once: reserved -> native -> released.
    /// Abandoning a reservation cannot release a queued/running native worker.
    /// Reader Dispose/Pause and logical timeouts never dispose the broker/slot.
    /// </summary>
    internal sealed class NativeReadLease
    {
        private readonly HpWmiFanSampleBroker _owner;
        private int _state; // 0: reserved; 1: native owns; 2: released

        internal NativeReadLease(HpWmiFanSampleBroker owner) => _owner = owner;

        internal void TransferToNative(HpWmiFanSampleBroker owner)
        {
            if (!ReferenceEquals(owner, _owner) ||
                Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                throw new InvalidOperationException("HP WMI native lease belongs to another broker or was already consumed.");
        }

        internal void CancelBeforeNative()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                _owner.Admission.Release();
        }

        internal void ReleaseAfterNative()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
                _owner.Admission.Release();
        }
    }
}
