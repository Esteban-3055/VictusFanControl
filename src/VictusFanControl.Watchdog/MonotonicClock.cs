using System.Runtime.InteropServices;

namespace VictusFanControl.Watchdog;

internal interface IMonotonicClock
{
    ulong Milliseconds { get; }
}

internal sealed class WindowsMonotonicClock : IMonotonicClock
{
    public ulong Milliseconds => GetTickCount64();

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
