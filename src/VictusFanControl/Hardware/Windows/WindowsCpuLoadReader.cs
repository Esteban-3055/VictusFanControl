using System.Runtime.InteropServices;

namespace VictusFanControl.Hardware.Windows;

internal sealed class WindowsCpuLoadReader
{
    private ulong? _lastIdle;
    private ulong? _lastKernel;
    private ulong? _lastUser;

    public double? ReadTotalLoadPercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return null;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();

        if (_lastIdle is null || _lastKernel is null || _lastUser is null)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            return null;
        }

        var idleDelta = idle - _lastIdle.Value;
        var kernelDelta = kernel - _lastKernel.Value;
        var userDelta = user - _lastUser.Value;
        var totalDelta = kernelDelta + userDelta;

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        if (totalDelta == 0)
        {
            return null;
        }

        var busyDelta = totalDelta >= idleDelta ? totalDelta - idleDelta : 0;
        return Math.Clamp((busyDelta * 100.0) / totalDelta, 0.0, 100.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime lpIdleTime,
        out FileTime lpKernelTime,
        out FileTime lpUserTime);
}
