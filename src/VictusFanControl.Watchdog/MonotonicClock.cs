using System.Runtime.InteropServices;

namespace VictusFanControl.Watchdog;

internal interface IMonotonicClock
{
    ulong Milliseconds { get; }
}

internal sealed class WindowsMonotonicClock : IMonotonicClock
{
    private const ulong HundredNanosecondsPerMillisecond = 10_000UL;

    public ulong Milliseconds
    {
        get
        {
            if (!QueryUnbiasedInterruptTime(out var unbiasedTime100ns))
            {
                throw new InvalidOperationException(
                    "QueryUnbiasedInterruptTime failed.");
            }

            // QueryUnbiasedInterruptTime advances only while Windows is in the
            // working state. Dividing before any arithmetic avoids overflow and
            // intentionally floors sub-millisecond remainder.
            return unbiasedTime100ns / HundredNanosecondsPerMillisecond;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(
        out ulong unbiasedTime100ns);
}
