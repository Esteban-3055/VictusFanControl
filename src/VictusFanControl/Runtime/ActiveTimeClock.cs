using System.Runtime.InteropServices;

namespace VictusFanControl.Runtime;

/// <summary>
/// Monotonic active-system clock used by operations that are allowed to be
/// interrupted by S3 and continue after wake. Unlike Stopwatch/QPC and the
/// .NET 8 timer queue on Windows, QueryUnbiasedInterruptTime excludes time the
/// machine spends asleep or hibernating.
/// </summary>
internal interface IActiveTimeClock
{
    ulong Milliseconds { get; }
}

internal sealed class WindowsActiveTimeClock : IActiveTimeClock
{
    private const ulong HundredNanosecondsPerMillisecond = 10_000UL;

    public ulong Milliseconds
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Windows active-time clock requires QueryUnbiasedInterruptTime.");
            }

            if (!QueryUnbiasedInterruptTime(out var unbiasedTime100ns))
            {
                throw new InvalidOperationException(
                    "QueryUnbiasedInterruptTime failed.");
            }

            return unbiasedTime100ns / HundredNanosecondsPerMillisecond;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(
        out ulong unbiasedTime100ns);
}

internal static class ActiveTimeClock
{
    public static ulong TimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        return checked((ulong)Math.Ceiling(timeout.TotalMilliseconds));
    }

    public static ulong ElapsedMilliseconds(
        IActiveTimeClock clock,
        ulong startedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return unchecked(clock.Milliseconds - startedMilliseconds);
    }

    public static bool HasElapsed(
        IActiveTimeClock clock,
        ulong startedMilliseconds,
        TimeSpan timeout) =>
        ElapsedMilliseconds(clock, startedMilliseconds) >=
        TimeoutMilliseconds(timeout);

    /// <summary>
    /// Cancels <paramref name="target"/> only after the requested amount of
    /// active (non-S3/non-hibernation) system time has elapsed. Task.Delay is
    /// used only as a wake-efficient poll; it is never the source of elapsed
    /// time, so an immediately-completed timer after resume cannot manufacture
    /// a timeout.
    /// </summary>
    public static async Task CancelAfterActiveTimeAsync(
        CancellationTokenSource target,
        IActiveTimeClock clock,
        TimeSpan timeout,
        CancellationToken stopMonitoring)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(clock);

        var timeoutMilliseconds = TimeoutMilliseconds(timeout);
        var started = clock.Milliseconds;

        while (!stopMonitoring.IsCancellationRequested)
        {
            var elapsed =
                ElapsedMilliseconds(clock, started);

            if (elapsed >= timeoutMilliseconds)
            {
                target.Cancel();
                return;
            }

            var remaining = timeoutMilliseconds - elapsed;
            var pollMilliseconds =
                (int)Math.Clamp(
                    remaining,
                    1UL,
                    50UL);

            await Task.Delay(
                    pollMilliseconds,
                    stopMonitoring)
                .ConfigureAwait(false);
        }
    }
}
