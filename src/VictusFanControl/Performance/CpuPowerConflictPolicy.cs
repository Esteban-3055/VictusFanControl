using VictusFanControl.Runtime;

namespace VictusFanControl.Performance;

internal enum CpuPowerConflictState
{
    Inactive,
    Contested,
    ReacquiredPendingStability,
    Yielded
}

/// <summary>
/// Pure, hardware-free policy for bounded reacquisition after an external
/// writer changes an owned CPU package power limit.
///
/// The policy intentionally owns no timer and performs no writes. A guardian
/// supplies the active-system clock and must explicitly begin/complete each
/// reacquisition attempt.
/// </summary>
internal sealed class CpuPowerConflictPolicy
{
    internal const int DefaultMaxReacquireAttempts = 5;
    internal static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultStabilityWindow = TimeSpan.FromSeconds(60);

    private readonly IActiveTimeClock _clock;
    private readonly TimeSpan _retryInterval;
    private readonly TimeSpan _stabilityWindow;
    private ulong _retryStartedMilliseconds;
    private ulong _stabilityStartedMilliseconds;
    private bool _attemptInFlight;

    internal CpuPowerConflictPolicy(
        IActiveTimeClock clock,
        int maxReacquireAttempts = DefaultMaxReacquireAttempts,
        TimeSpan? retryInterval = null,
        TimeSpan? stabilityWindow = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        MaxReacquireAttempts = maxReacquireAttempts;
        _retryInterval = retryInterval ?? DefaultRetryInterval;
        _stabilityWindow = stabilityWindow ?? DefaultStabilityWindow;

        if (MaxReacquireAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxReacquireAttempts));
        if (_retryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryInterval));
        if (_stabilityWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stabilityWindow));
    }

    internal CpuPowerConflictState State { get; private set; } =
        CpuPowerConflictState.Inactive;

    internal int MaxReacquireAttempts { get; }
    internal int AttemptsUsed { get; private set; }
    internal bool AttemptInFlight => _attemptInFlight;
    internal ulong? FirstDetectedActiveMilliseconds { get; private set; }
    internal ulong? LastDetectedActiveMilliseconds { get; private set; }

    /// <summary>
    /// Records an observed external change. Repeated observations while already
    /// Contested never postpone the current retry deadline. If all five attempts
    /// have already been consumed and a previously reacquired value is changed
    /// again before the stability window closes, authority is yielded immediately.
    /// </summary>
    internal void ObserveExternalChange()
    {
        if (_attemptInFlight)
            throw new InvalidOperationException(
                "Cannot record an external change while a reacquisition attempt is in flight.");

        var now = _clock.Milliseconds;
        LastDetectedActiveMilliseconds = now;

        switch (State)
        {
            case CpuPowerConflictState.Inactive:
                FirstDetectedActiveMilliseconds = now;
                AttemptsUsed = 0;
                _retryStartedMilliseconds = now;
                State = CpuPowerConflictState.Contested;
                break;

            case CpuPowerConflictState.Contested:
                // Keep the original retry start. Continuous observations from an
                // external writer must not defer the first bounded reacquisition.
                break;

            case CpuPowerConflictState.ReacquiredPendingStability:
                if (AttemptsUsed >= MaxReacquireAttempts)
                {
                    State = CpuPowerConflictState.Yielded;
                }
                else
                {
                    _retryStartedMilliseconds = now;
                    State = CpuPowerConflictState.Contested;
                }
                break;

            case CpuPowerConflictState.Yielded:
                // Read-only observation is allowed after yield. No new automatic
                // authority is granted without explicit user action.
                break;

            default:
                throw new InvalidOperationException("Unknown CPU conflict state.");
        }
    }

    /// <summary>
    /// Atomically consumes one reacquisition attempt when the 30-second active
    /// time interval has elapsed. The caller must immediately follow a true
    /// result with CompleteReacquire().
    /// </summary>
    internal bool TryBeginReacquire()
    {
        if (_attemptInFlight || State != CpuPowerConflictState.Contested)
            return false;

        if (AttemptsUsed >= MaxReacquireAttempts)
        {
            State = CpuPowerConflictState.Yielded;
            return false;
        }

        if (!ActiveTimeClock.HasElapsed(
                _clock,
                _retryStartedMilliseconds,
                _retryInterval))
        {
            return false;
        }

        AttemptsUsed++;
        _attemptInFlight = true;
        return true;
    }

    /// <summary>
    /// Completes the attempt after the guardian performs at most one write and
    /// exact readback. Success starts the 60-second stability window. Failure
    /// schedules the next bounded retry or yields after the fifth attempt.
    /// </summary>
    internal void CompleteReacquire(bool exactReadback)
    {
        if (!_attemptInFlight)
            throw new InvalidOperationException(
                "No CPU power reacquisition attempt is in flight.");

        _attemptInFlight = false;
        var now = _clock.Milliseconds;

        if (exactReadback)
        {
            _stabilityStartedMilliseconds = now;
            State = CpuPowerConflictState.ReacquiredPendingStability;
            return;
        }

        if (AttemptsUsed >= MaxReacquireAttempts)
        {
            State = CpuPowerConflictState.Yielded;
            return;
        }

        _retryStartedMilliseconds = now;
        State = CpuPowerConflictState.Contested;
    }

    /// <summary>
    /// Closes the conflict episode only after the reacquired setting survives
    /// the full active-time stability window. At that point a future conflict
    /// receives a fresh five-attempt budget.
    /// </summary>
    internal bool TryCompleteStableWindow()
    {
        if (_attemptInFlight ||
            State != CpuPowerConflictState.ReacquiredPendingStability)
        {
            return false;
        }

        if (!ActiveTimeClock.HasElapsed(
                _clock,
                _stabilityStartedMilliseconds,
                _stabilityWindow))
        {
            return false;
        }

        ResetCore();
        return true;
    }

    /// <summary>
    /// Used when target/safety/lifecycle validation says reacquisition is no
    /// longer allowed. This never grants another automatic attempt.
    /// </summary>
    internal void ForceYield()
    {
        _attemptInFlight = false;
        State = CpuPowerConflictState.Yielded;
    }

    /// <summary>
    /// Explicit user action may start a new authority episode after Yielded.
    /// Calling this method performs no hardware I/O.
    /// </summary>
    internal void ResetByUser()
    {
        ResetCore();
    }

    private void ResetCore()
    {
        State = CpuPowerConflictState.Inactive;
        AttemptsUsed = 0;
        _attemptInFlight = false;
        FirstDetectedActiveMilliseconds = null;
        LastDetectedActiveMilliseconds = null;
        _retryStartedMilliseconds = 0;
        _stabilityStartedMilliseconds = 0;
    }
}
