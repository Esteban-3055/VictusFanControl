using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.CpuProbe;

internal sealed class PhysicalRaplHardware : IRaplProbeHardware
{
    private readonly IntelMsrReader _reader;
    private readonly PawnIoModuleSession? _writer;
    private readonly WindowsCpuLoadReader _load = new();

    internal PhysicalRaplHardware(string modulePath, string outputDirectory, bool allowWrite)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !X86Base.IsSupported)
            throw new InvalidOperationException("An elevated Windows x64 process is required.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Administrator privileges are required.");
        var target = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(target, out var reason))
            throw new InvalidOperationException("Exact 8C40/F.18 target mismatch: " + reason);
        using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var cpuName = cpuKey?.GetValue("ProcessorNameString")?.ToString() ?? "unknown";
        if (!cpuName.Contains("i7-13700H", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Exact i7-13700H target mismatch: " + cpuName);
        foreach (var name in new[] { "VictusFanControl", "VictusFanControl.App", "VictusFanControl.Watchdog" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                    throw new InvalidOperationException("Close the app, investigation and fan watchdog before a separate CPU experiment: " + name);
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        if (!AcOnline()) throw new InvalidOperationException("AC power must be connected and its status readable.");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modulePath)));
        _reader = new IntelMsrReader(modulePath);
        try
        {
            if (_reader.PawnIoVersion < new Version(2, 2, 0) || _reader.PhysicalCoreCount != 14)
                throw new InvalidOperationException("PawnIO 2.2+ and the expected 14 physical cores are required.");
            UnitsRaw = _reader.ReadMsr(0x606);
            PowerInfoRaw = _reader.ReadMsr(0x614);
            var cpuid = X86Base.CpuId(1, 0);
            ProbeEvidence.DurableJson(Path.Combine(outputDirectory, "hardware.json"), new {
                Hardware = target, CpuName = cpuName,
                CpuIdLeaf1 = new { cpuid.Eax, cpuid.Ebx, cpuid.Ecx, cpuid.Edx },
                PhysicalCores = _reader.PhysicalCoreCount, LogicalProcessors = Environment.ProcessorCount,
                PawnIoVersion = _reader.PawnIoVersion.ToString(), IntelModuleSha256 = hash,
                ProgramSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PhysicalRaplHardware).Assembly.Location))),
                WriteTestRequested = allowWrite, CpuProbeVersion = "P1-bounded-v1",
                OperatingSystem = Environment.OSVersion.ToString() });
            if (allowWrite) _writer = new PawnIoModuleSession(modulePath);
        }
        catch { _reader.Dispose(); throw; }
    }

    public ulong UnitsRaw { get; }
    public ulong PowerInfoRaw { get; }
    public ulong ReadLimit() => _reader.ReadMsr(0x610);
    public void WriteLimit(ulong value)
    {
        if (_writer is null) throw new InvalidOperationException("Read-only session cannot write.");
        // Dedicated physical write surface: no caller can choose an MSR address.
        _writer.Execute("ioctl_write_msr", new ulong[] { 0x610, value }, 0);
    }
    public CpuObservation Sample() => new(ReadLimit(), _reader.ReadPackagePowerW(),
        _reader.ReadPackageTemperatureC(), _load.ReadTotalLoadPercent(), AcOnline());
    public void Dispose() { _writer?.Dispose(); _reader.Dispose(); }

    private static bool AcOnline()
    {
        if (!GetSystemPowerStatus(out var status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemPowerStatus failed.");
        return status.AcLineStatus == 1; // 0=battery, 255=unknown; both fail closed.
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
