using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VictusFanControl.Runtime;

/// <summary>
/// Holds a Windows ES_SYSTEM_REQUIRED request on one dedicated thread for the
/// lifetime of an active hardware test.
///
/// SetThreadExecutionState is thread-scoped, so the request must not be placed
/// on an async/thread-pool continuation that can move between threads. The
/// dedicated worker owns and clears the request deterministically.
///
/// This prevents automatic idle sleep/Modern Standby. It intentionally does
/// not request ES_DISPLAY_REQUIRED and does not attempt to block explicit user,
/// lid, battery-critical or administrator power transitions.
/// </summary>
internal sealed class SystemSleepInhibitor : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;

    private Exception? _startupFailure;
    private bool _disposed;

    private SystemSleepInhibitor(string reason)
    {
        _thread = new Thread(() => Worker(reason))
        {
            IsBackground = true,
            Name = "VictusFanControl-SystemSleepInhibitor"
        };

        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _stop.Set();
            throw new TimeoutException(
                "Timed out while acquiring the Windows system-sleep inhibition request.");
        }

        if (_startupFailure is not null)
        {
            _stop.Set();
            throw new InvalidOperationException(
                "Windows refused the system-sleep inhibition request.",
                _startupFailure);
        }
    }

    public static SystemSleepInhibitor Acquire(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "System sleep inhibition is available only on Windows.");
        }

        return new SystemSleepInhibitor(reason);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();

        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException(
                "Timed out while releasing the Windows system-sleep inhibition request.");
        }

        _ready.Dispose();
        _stop.Dispose();
    }

    private void Worker(string reason)
    {
        try
        {
            var previous = SetThreadExecutionState(
                EsContinuous | EsSystemRequired);

            if (previous == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetThreadExecutionState(ES_SYSTEM_REQUIRED) failed.");
            }

            _ready.Set();

            try
            {
                _stop.Wait();
            }
            finally
            {
                _ = SetThreadExecutionState(EsContinuous);
            }
        }
        catch (Exception ex)
        {
            _startupFailure = new InvalidOperationException(
                $"Could not keep the system awake for active hardware test '{reason}'.",
                ex);
            _ready.Set();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
