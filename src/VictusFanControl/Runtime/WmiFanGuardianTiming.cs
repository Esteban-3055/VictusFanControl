using System.Runtime.InteropServices;

namespace VictusFanControl.Runtime;

// Bracket the sleep-inclusive clock with active-time reads. A thread preemption
// between API calls widens the interval rather than inventing elapsed sleep.
internal readonly record struct WmiFanGuardianClockSample(ulong TotalMs, ulong ActiveBeforeMs, ulong ActiveAfterMs)
{
    internal ulong MinimumSleepMs => TotalMs > ActiveAfterMs ? TotalMs - ActiveAfterMs : 0;
    internal ulong MaximumSleepMs => TotalMs - ActiveBeforeMs;

    internal static WmiFanGuardianClockSample Read()
    {
        var active = new WindowsActiveTimeClock();
        var before = active.Milliseconds;
        QueryInterruptTime(out var total);
        return new(total / 10_000, before, active.Milliseconds);
    }

    // Same documented API contract used by ProductFanRecovery's boot clock.
    [DllImport("api-ms-win-core-realtime-l1-1-1.dll", ExactSpelling = true)]
    private static extern void QueryInterruptTime(out ulong interruptTime100ns);
}

internal sealed record WmiFanGuardianTimingStatus(double WorkMs, double IdleMs, ulong SleepLowerBoundMs, string? StopReason);

/// <summary>Measure idle gaps separately from synchronous supervisor work.</summary>
internal sealed class WmiFanGuardianTiming
{
    private WmiFanGuardianClockSample? _initial, _last;
    private TimeSpan? _workStarted, _workCompleted, _lastElapsed;
    internal WmiFanGuardianTimingStatus Status { get; private set; } = new(0, 0, 0, null);

    internal void Check(TimeSpan elapsed, WmiFanGuardianClockSample sample)
    {
        if (elapsed < TimeSpan.Zero || _lastElapsed is { } lastElapsed && elapsed < lastElapsed ||
            sample.ActiveAfterMs < sample.ActiveBeforeMs || sample.TotalMs < sample.ActiveBeforeMs ||
            _last is { } last && (sample.TotalMs < last.TotalMs || sample.ActiveBeforeMs < last.ActiveAfterMs))
            throw new InvalidOperationException("No se confirmó el reloj de supervisión WMI.");
        _initial ??= sample;
        _last = sample; _lastElapsed = elapsed;
        var sleep = sample.MinimumSleepMs > _initial.Value.MaximumSleepMs
            ? sample.MinimumSleepMs - _initial.Value.MaximumSleepMs : 0;
        Status = Status with { SleepLowerBoundMs = sleep };
        if (sleep > 3000) Status = Status with { StopReason = "SYSTEM_SLEEP_GAP" };
    }

    internal void BeginWork(TimeSpan elapsed, WmiFanGuardianClockSample sample)
    {
        Check(elapsed, sample);
        var idle = _workCompleted is { } completed ? elapsed - completed : TimeSpan.Zero;
        Status = Status with { IdleMs = idle.TotalMilliseconds };
        if (idle > TimeSpan.FromSeconds(3) && Status.StopReason is null)
            Status = Status with { StopReason = "SUPERVISOR_IDLE_GAP" };
        _workStarted = elapsed;
    }

    internal void CompleteWork(TimeSpan elapsed, WmiFanGuardianClockSample sample)
    {
        Check(elapsed, sample);
        if (_workStarted is not { } started) throw new InvalidOperationException("WMI work was not started.");
        Status = Status with { WorkMs = (elapsed - started).TotalMilliseconds };
        _workCompleted = elapsed;
    }
}
