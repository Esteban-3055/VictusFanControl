using System.Runtime.CompilerServices;

namespace VictusFanControl.Telemetry;

/// <summary>
/// Process-wide native read coordination, shared across both reader lifetimes.
/// Admission and native completion survive reader recreation. Control callers
/// may share a queued acquisition only before its native worker starts.
/// Completed samples are never cached here; decoding/freshness stay in the adapter.
/// </summary>
internal sealed class HpWmiFanSampleBroker
{
    private static readonly ConditionalWeakTable<SemaphoreSlim, HpWmiFanSampleBroker> Brokers = new();
    internal static HpWmiFanSampleBroker Production { get; } = For(new SemaphoreSlim(1, 1));
    private readonly object _controlGate = new();
    private ControlBatch? _queuedControl;
    private long _controlRequests, _controlJoins;

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
    internal long ControlRequests => Interlocked.Read(ref _controlRequests);
    internal long CoalescedControlRequests => Interlocked.Read(ref _controlJoins);

    /// <summary>
    /// Join only an acquisition that has not begun. Each caller keeps its own
    /// cancellation/deadline; the batch is canceled when its last waiter leaves.
    /// The adapter still retains the native slot until synchronous WMI returns.
    /// </summary>
    internal async ValueTask<HpWmiFanProofSample> ReadControlAsync(
        Func<Action, CancellationToken, Task<HpWmiFanProofSample>> read,
        TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        ControlBatch batch;
        bool start;
        lock (_controlGate)
        {
            Interlocked.Increment(ref _controlRequests);
            start = _queuedControl is not { Accepting: true };
            if (start) _queuedControl = new ControlBatch();
            else Interlocked.Increment(ref _controlJoins);
            batch = _queuedControl!;
            batch.Waiters++;
        }
        if (start) _ = RunControlBatchAsync(batch, read);
        try
        {
            // The leader's adapter already bounds queue + native time. Avoid a
            // second competing timeout that could misclassify it as cancellation.
            if (start) return await batch.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var remaining = maximumWait - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("HP WMI control caller budget expired.");
            return await batch.Completion.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_controlGate)
            {
                batch.Waiters--;
                if (batch.Waiters == 0)
                {
                    CloseBatch(batch);
                    // Cancel under the same gate as disposal. Cancel may complete
                    // the adapter inline; Monitor reentrancy preserves this boundary.
                    if (!batch.Finished)
                    {
                        batch.Cancelling = true;
                        try { batch.Lifetime.Cancel(); }
                        finally { batch.Cancelling = false; DisposeBatch(batch); }
                    }
                    else DisposeBatch(batch);
                }
            }
        }
    }

    private async Task RunControlBatchAsync(ControlBatch batch,
        Func<Action, CancellationToken, Task<HpWmiFanProofSample>> read)
    {
        try
        {
            var sample = await read(() =>
            {
                lock (_controlGate) CloseBatch(batch);
            }, batch.Lifetime.Token).ConfigureAwait(false);
            batch.Completion.TrySetResult(sample);
        }
        catch (OperationCanceledException ex) { batch.Completion.TrySetCanceled(ex.CancellationToken); }
        catch (Exception ex) { batch.Completion.TrySetException(ex); }
        finally
        {
            lock (_controlGate)
            {
                CloseBatch(batch);
                batch.Finished = true;
                DisposeBatch(batch);
            }
        }
    }

    // Caller owns _controlGate. Never retain a completed/in-flight native sample as cache.
    private void CloseBatch(ControlBatch batch)
    {
        batch.Accepting = false;
        if (ReferenceEquals(_queuedControl, batch)) _queuedControl = null;
    }

    private static void DisposeBatch(ControlBatch batch)
    {
        if (batch.Finished && batch.Waiters == 0 && !batch.Cancelling && !batch.Disposed)
        {
            batch.Disposed = true;
            batch.Lifetime.Dispose();
        }
    }

    private sealed class ControlBatch
    {
        internal readonly TaskCompletionSource<HpWmiFanProofSample> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource Lifetime = new();
        internal int Waiters;
        internal bool Accepting = true, Finished, Cancelling, Disposed;
        internal ControlBatch()
        {
            // All waiters may leave before a native failure becomes observable.
            _ = Completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

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
