using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VictusFanControl.Runtime;

internal sealed record SystemPowerStatusSample(
    bool AcOnline,
    byte BatteryPercent,
    byte BatteryFlags)
{
    // GetSystemPowerStatus documents 0=offline, 1=online, 255=unknown.
    // Preserve the raw byte so performance control never misclassifies an
    // unknown AC state as Battery merely because AcOnline is false.
    public byte RawAcLineStatus { get; init; } = 0xFF;

    public bool BatteryPresent => (BatteryFlags & 0x80) == 0;

    public override string ToString() =>
        $"AC={(AcOnline ? "online" : "offline")} battery={BatteryPercent}% " +
        $"flags=0x{BatteryFlags:X2}";
}

internal static class SystemPowerStatusReader
{
    public static SystemPowerStatusSample Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows power status is available only on Windows.");
        }

        if (!GetSystemPowerStatus(out var native))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "GetSystemPowerStatus failed.");
        }

        return new SystemPowerStatusSample(
            AcOnline: native.ACLineStatus == 1,
            BatteryPercent: native.BatteryLifePercent,
            BatteryFlags: native.BatteryFlag)
        {
            RawAcLineStatus = native.ACLineStatus
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(
        out NativeSystemPowerStatus systemPowerStatus);
}
