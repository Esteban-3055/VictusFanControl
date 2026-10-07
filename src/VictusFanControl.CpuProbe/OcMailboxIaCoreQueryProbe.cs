using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.CpuProbe;

/// <summary>
/// Bounded query-only characterization of the IA-core OC mailbox domain.
///
/// Queries only:
///   0x01 GET_OC_CAPABILITIES, domain 0 (IA core)
///   0x10 GET_VOLTAGE_FREQUENCY, domain 0 (IA core)
///   0x07 GET_FUSED_P0_RATIO_VOLTAGE, domain 0 (IA core)
///
/// Intel OC mailbox queries are submitted by writing a fixed query command to
/// MSR 0x150 and reading the response. This class exposes no arbitrary command,
/// data payload, domain, or MSR address.
///
/// Guard registers 0x1AD and 0x610 must remain bit-identical.
/// </summary>
internal static class OcMailboxIaCoreQueryProbe
{
    private const uint MsrOcMailbox = 0x150;
    private const uint MsrTurboRatioLimit = 0x1AD;
    private const uint MsrPkgPowerLimit = 0x610;

    private const byte DomainIaCore = 0x00;

    private const byte CmdGetOcCapabilities = 0x01;
    private const byte CmdGetFusedP0RatioVoltage = 0x07;
    private const byte CmdGetVoltageFrequency = 0x10;

    private const int MaxMailboxReads = 8;

    internal static int Run(string[] args)
    {
        try
        {
            var options = Parse(args);
            ValidateEnvironment(options.ModulePath);

            Directory.CreateDirectory(options.OutputDirectory);
            using var session = new PawnIoModuleSession(options.ModulePath);

            var baselineTurbo = ReadMsr(session, MsrTurboRatioLimit);
            var baselinePower = ReadMsr(session, MsrPkgPowerLimit);

            var capabilities = Query(session, CmdGetOcCapabilities, DomainIaCore, 0);
            var vf = capabilities.Success
                ? Query(session, CmdGetVoltageFrequency, DomainIaCore, 0)
                : MailboxQueryResult.NotAttempted(
                    CmdGetVoltageFrequency,
                    "SKIPPED_AFTER_CAPABILITIES_FAILURE");
            var fusedP0 = capabilities.Success && vf.Success
                ? Query(session, CmdGetFusedP0RatioVoltage, DomainIaCore, 0)
                : MailboxQueryResult.NotAttempted(
                    CmdGetFusedP0RatioVoltage,
                    "SKIPPED_AFTER_EARLIER_QUERY_FAILURE");

            var afterTurbo = ReadMsr(session, MsrTurboRatioLimit);
            var afterPower = ReadMsr(session, MsrPkgPowerLimit);

            var turboUnchanged = baselineTurbo == afterTurbo;
            var powerUnchanged = baselinePower == afterPower;

            var capDecoded = DecodeCapabilities(capabilities);
            var vfDecoded = DecodeVoltageFrequency(vf);
            var p0Decoded = DecodeFusedP0(fusedP0);

            string result;
            if (!turboUnchanged || !powerUnchanged)
                result = "UNEXPECTED_LIMIT_REGISTER_CHANGE";
            else if (!capabilities.Success)
                result = "CAPABILITIES_QUERY_NOT_USABLE";
            else if (!vf.Success)
                result = "VF_QUERY_NOT_USABLE";
            else if (!fusedP0.Success)
                result = "P0_QUERY_NOT_USABLE";
            else
                result = "IA_CORE_QUERY_COMPLETE";

            var evidence = new
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                Result = result,
                Target = HardwareIdentityReader.ReadCurrent(),
                CpuName = ReadCpuName(),
                LogicalProcessors = Environment.ProcessorCount,
                PawnIoVersion = session.DriverVersion.ToString(),
                IntelModuleSha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(options.ModulePath))),

                Semantics = new
                {
                    PhysicalReadOnly = false,
                    QueryOnly = true,
                    ConfigurationMutationRequested = false,
                    ArbitraryMsrWriteSurface = false,
                    ExactMsr = "0x150",
                    Domain = "IA_CORE / 0",
                    Commands = new[]
                    {
                        "0x01 GET_OC_CAPABILITIES",
                        "0x10 GET_VOLTAGE_FREQUENCY",
                        "0x07 GET_FUSED_P0_RATIO_VOLTAGE"
                    }
                },

                GuardRegisters = new
                {
                    PrimaryTurboRatioLimit = new
                    {
                        Msr = "0x1AD",
                        Before = Hex(baselineTurbo),
                        After = Hex(afterTurbo),
                        Unchanged = turboUnchanged
                    },
                    PackagePowerLimit = new
                    {
                        Msr = "0x610",
                        Before = Hex(baselinePower),
                        After = Hex(afterPower),
                        Unchanged = powerUnchanged
                    }
                },

                Capabilities = new
                {
                    Query = capabilities,
                    Decoded = capDecoded
                },

                VoltageFrequency = new
                {
                    Query = vf,
                    Decoded = vfDecoded
                },

                FusedP0 = new
                {
                    Query = fusedP0,
                    Decoded = p0Decoded
                }
            };

            WriteJson(
                Path.Combine(options.OutputDirectory, "oc-mailbox-ia-core-query.json"),
                evidence);

            var summary = new
            {
                CompletedUtc = DateTimeOffset.UtcNow,
                Result = result,
                PrimaryTurboRatioUnchanged = turboUnchanged,
                PackagePowerLimitUnchanged = powerUnchanged,

                CapabilitiesCompletion = capabilities.CompletionCode,
                capDecoded.MaxOcRatioLimit,
                capDecoded.RatioOcSupported,
                capDecoded.VoltageOverrideSupported,
                capDecoded.VoltageOffsetSupported,

                VoltageFrequencyCompletion = vf.CompletionCode,
                vfDecoded.CurrentMaxOcRatio,
                vfDecoded.VoltageTargetRaw,
                vfDecoded.VoltageMode,
                vfDecoded.VoltageOffsetRaw,

                FusedP0Completion = fusedP0.CompletionCode,
                p0Decoded.FusedP0Ratio,
                p0Decoded.FusedP0VoltageRaw
            };

            WriteJson(
                Path.Combine(options.OutputDirectory, "oc-mailbox-ia-core-summary.json"),
                summary);

            Console.WriteLine(result);
            Console.WriteLine(
                $"Capabilities: completion={capabilities.CompletionCode?.ToString() ?? "-"} " +
                $"maxRatio={capDecoded.MaxOcRatioLimit?.ToString() ?? "-"} " +
                $"ratioOC={capDecoded.RatioOcSupported?.ToString() ?? "-"} " +
                $"vOverride={capDecoded.VoltageOverrideSupported?.ToString() ?? "-"} " +
                $"vOffset={capDecoded.VoltageOffsetSupported?.ToString() ?? "-"}");
            Console.WriteLine(
                $"Current IA V/F: completion={vf.CompletionCode?.ToString() ?? "-"} " +
                $"maxRatio={vfDecoded.CurrentMaxOcRatio?.ToString() ?? "-"} " +
                $"targetRaw={vfDecoded.VoltageTargetRaw?.ToString() ?? "-"} " +
                $"mode={vfDecoded.VoltageMode?.ToString() ?? "-"} " +
                $"offsetRaw={vfDecoded.VoltageOffsetRaw?.ToString() ?? "-"}");
            Console.WriteLine(
                $"Fused P0: completion={fusedP0.CompletionCode?.ToString() ?? "-"} " +
                $"ratio={p0Decoded.FusedP0Ratio?.ToString() ?? "-"} " +
                $"voltageRaw={p0Decoded.FusedP0VoltageRaw?.ToString() ?? "-"}");
            Console.WriteLine($"0x1AD unchanged: {turboUnchanged}");
            Console.WriteLine($"0x610 unchanged: {powerUnchanged}");
            Console.WriteLine($"Evidence: {options.OutputDirectory}");

            if (!turboUnchanged || !powerUnchanged)
                return 3;

            return result == "IA_CORE_QUERY_COMPLETE" ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "IA-core OC mailbox query refused/failed: " + ex.Message);
            return 2;
        }
    }

    private static MailboxQueryResult Query(
        PawnIoModuleSession session,
        byte command,
        byte param1,
        byte param2)
    {
        var interfaceData =
            0x80000000u |
            command |
            ((uint)param1 << 8) |
            ((uint)param2 << 16);

        var request = ((ulong)interfaceData << 32);

        try
        {
            session.Execute(
                "ioctl_write_msr",
                new ulong[] { MsrOcMailbox, request },
                0);
        }
        catch (Exception ex)
        {
            return new MailboxQueryResult(
                command,
                param1,
                param2,
                false,
                false,
                false,
                null,
                null,
                null,
                ex.GetType().Name + ": " + ex.Message);
        }

        ulong response = 0;
        for (var attempt = 1; attempt <= MaxMailboxReads; attempt++)
        {
            try
            {
                response = ReadMsr(session, MsrOcMailbox);
            }
            catch (Exception ex)
            {
                return new MailboxQueryResult(
                    command,
                    param1,
                    param2,
                    true,
                    false,
                    false,
                    null,
                    null,
                    null,
                    ex.GetType().Name + ": " + ex.Message);
            }

            var busy = (response & (1UL << 63)) != 0;
            if (!busy)
            {
                var completion = (int)((response >> 32) & 0xFF);
                var data = (uint)(response & 0xFFFFFFFF);
                return new MailboxQueryResult(
                    command,
                    param1,
                    param2,
                    true,
                    true,
                    false,
                    completion,
                    data,
                    Hex(response),
                    completion == 0 ? "OK" : CompletionStatus(completion));
            }

            Thread.SpinWait(128);
        }

        return new MailboxQueryResult(
            command,
            param1,
            param2,
            true,
            true,
            true,
            null,
            null,
            Hex(response),
            "MAILBOX_BUSY_AFTER_BOUNDED_POLL");
    }

    private static CapabilityFields DecodeCapabilities(MailboxQueryResult query)
    {
        if (!query.Success || !query.Data.HasValue)
            return new CapabilityFields(null, null, null, null);

        var data = query.Data.Value;
        return new CapabilityFields(
            (int)(data & 0xFF),
            (data & 0x100) != 0,
            (data & 0x200) != 0,
            (data & 0x400) != 0);
    }

    private static VoltageFrequencyFields DecodeVoltageFrequency(
        MailboxQueryResult query)
    {
        if (!query.Success || !query.Data.HasValue)
            return new VoltageFrequencyFields(null, null, null, null);

        var data = query.Data.Value;
        return new VoltageFrequencyFields(
            (int)(data & 0xFF),
            (int)((data & 0x0007FF80) >> 7),
            (int)((data >> 19) & 0x1),
            (int)((data & 0x7FF00000) >> 20));
    }

    private static FusedP0Fields DecodeFusedP0(MailboxQueryResult query)
    {
        if (!query.Success || !query.Data.HasValue)
            return new FusedP0Fields(null, null);

        var data = query.Data.Value;
        return new FusedP0Fields(
            (int)(data & 0xFF),
            (int)((data & 0x000FFF00) >> 8));
    }

    private static string CompletionStatus(int code) => code switch
    {
        1 => "OC_LOCKED",
        2 => "INVALID_DOMAIN",
        3 => "MAX_RATIO_EXCEEDED",
        4 => "MAX_VOLTAGE_EXCEEDED",
        5 => "OC_NOT_SUPPORTED",
        6 => "WRITE_FAILED",
        7 => "READ_FAILED",
        _ => $"MAILBOX_COMPLETION_{code}"
    };

    private static ProbeOptions Parse(string[] args)
    {
        if (args.Length != 4 ||
            args[0] != "--module" ||
            args[2] != "--output-dir")
        {
            throw new ArgumentException(
                "Use --oc-mailbox-ia-core-query --module <IntelMSR.bin> --output-dir <new-dir>.");
        }

        var module = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[3]);

        if (!File.Exists(module))
            throw new FileNotFoundException("Signed IntelMSR.bin was not found.", module);

        if (Directory.Exists(output))
            throw new IOException("Output directory must be new.");

        return new ProbeOptions(module, output);
    }

    private static void ValidateEnvironment(string modulePath)
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.Is64BitProcess ||
            !X86Base.IsSupported)
        {
            throw new InvalidOperationException(
                "An elevated Windows x64 process on an x86-64 CPU is required.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Administrator privileges are required.");

        var target = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(target, out var reason))
            throw new InvalidOperationException("Exact 8C40/F.18 target mismatch: " + reason);

        var cpuName = ReadCpuName();
        if (!cpuName.Contains("i7-13700H", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Exact i7-13700H target mismatch: " + cpuName);

        if (Environment.ProcessorCount != 20)
            throw new InvalidOperationException(
                $"Expected 20 logical processors, found {Environment.ProcessorCount}.");

        foreach (var name in new[]
        {
            "VictusFanControl",
            "VictusFanControl.App",
            "VictusFanControl.PerformanceGuardian",
            "ThrottleStop",
            "XTU"
        })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"Close {name} before the IA-core OC mailbox query.");
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        if (!AcOnline())
            throw new InvalidOperationException(
                "AC power must be connected and its status readable.");

        _ = modulePath;
    }

    private static string ReadCpuName()
    {
        using var cpuKey = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

        return cpuKey?.GetValue("ProcessorNameString")?.ToString() ?? "unknown";
    }

    private static ulong ReadMsr(PawnIoModuleSession session, uint msr)
    {
        var values = session.Execute("ioctl_read_msr", new ulong[] { msr }, 1);

        if (values.Length != 1)
            throw new InvalidDataException(
                $"IntelMSR returned {values.Length} cells for MSR 0x{msr:X}.");

        return values[0];
    }

    private static bool AcOnline()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "GetSystemPowerStatus failed.");
        }

        return status.AcLineStatus == 1;
    }

    private static string Hex(ulong value) => $"0x{value:X16}";

    private static void WriteJson(string path, object value)
    {
        var json = JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private sealed record ProbeOptions(string ModulePath, string OutputDirectory);

    internal sealed record MailboxQueryResult(
        byte Command,
        byte Param1,
        byte Param2,
        bool WriteAccepted,
        bool ReadAccepted,
        bool Busy,
        int? CompletionCode,
        uint? Data,
        string? RawResponse,
        string Status)
    {
        public bool Success =>
            WriteAccepted &&
            ReadAccepted &&
            !Busy &&
            CompletionCode == 0;

        public static MailboxQueryResult NotAttempted(byte command, string status) =>
            new(command, 0, 0, false, false, false, null, null, null, status);
    }

    private readonly record struct CapabilityFields(
        int? MaxOcRatioLimit,
        bool? RatioOcSupported,
        bool? VoltageOverrideSupported,
        bool? VoltageOffsetSupported);

    private readonly record struct VoltageFrequencyFields(
        int? CurrentMaxOcRatio,
        int? VoltageTargetRaw,
        int? VoltageMode,
        int? VoltageOffsetRaw);

    private readonly record struct FusedP0Fields(
        int? FusedP0Ratio,
        int? FusedP0VoltageRaw);
}
