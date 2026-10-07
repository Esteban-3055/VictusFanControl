using System.Diagnostics;
using System.ComponentModel;
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
/// Query-only OC mailbox characterization for the exact HP 8C40 / i7-13700H target.
///
/// IMPORTANT: Intel's OC mailbox is command based. Even a read/query command requires
/// an MSR write to 0x150 to submit the query, followed by an MSR read for the reply.
/// This probe therefore is NOT "physical read-only". It is deliberately limited to
/// the documented command 0x1C (favored-core / per-core ratio capability query) and
/// contains no caller-controlled MSR address, command or data payload.
///
/// The probe verifies that package power limits (0x610) and primary turbo ratio
/// limits (0x1AD) are bit-identical before and after the query sequence.
/// </summary>
internal static class OcMailboxRatioQueryProbe
{
    private const uint MsrOcMailbox = 0x150;
    private const uint MsrTurboRatioLimit = 0x1AD;
    private const uint MsrPkgPowerLimit = 0x610;

    private const ulong QueryPerCoreRatioCapabilities = 0x8000001C00000000UL;
    private const int MaxMailboxReads = 8;

    internal static int Run(string[] args)
    {
        try
        {
            var options = Parse(args);
            ValidateEnvironment(options.ModulePath);

            Directory.CreateDirectory(options.OutputDirectory);

            using var session = new PawnIoModuleSession(options.ModulePath);
            var baselineTurbo = ReadMsr(session, 0, MsrTurboRatioLimit);
            var baselinePower = ReadMsr(session, 0, MsrPkgPowerLimit);

            var topology = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(ReadLogicalTopology)
                .ToArray();

            var rows = new List<MailboxRow>();

            // Stage 1: prove that the exact query is accepted on LP0 before issuing
            // the same query on the remaining logical processors.
            var first = QueryOne(session, 0, topology[0]);
            rows.Add(first);

            if (!first.WriteAccepted || !first.ReadAccepted || first.Busy || first.CompletionCode != 0)
            {
                var afterTurboEarly = ReadMsr(session, 0, MsrTurboRatioLimit);
                var afterPowerEarly = ReadMsr(session, 0, MsrPkgPowerLimit);
                WriteEvidence(options, session, baselineTurbo, afterTurboEarly,
                    baselinePower, afterPowerEarly, topology, rows,
                    "QUERY_REJECTED_OR_UNSUPPORTED_ON_LP0");

                Console.WriteLine("OC_MAILBOX_QUERY_NOT_USABLE");
                PrintRow(first);
                Console.WriteLine($"Evidence: {options.OutputDirectory}");
                return 2;
            }

            for (var logical = 1; logical < Environment.ProcessorCount; logical++)
            {
                rows.Add(QueryOne(session, logical, topology[logical]));
            }

            var afterTurbo = ReadMsr(session, 0, MsrTurboRatioLimit);
            var afterPower = ReadMsr(session, 0, MsrPkgPowerLimit);

            var turboUnchanged = baselineTurbo == afterTurbo;
            var powerUnchanged = baselinePower == afterPower;
            var allCompleted = rows.All(row =>
                row.WriteAccepted && row.ReadAccepted && !row.Busy && row.CompletionCode == 0);

            var result = turboUnchanged && powerUnchanged
                ? allCompleted
                    ? "QUERY_ONLY_COMPLETE"
                    : "QUERY_PARTIAL_COMPLETION__LIMIT_REGISTERS_UNCHANGED"
                : "UNEXPECTED_LIMIT_REGISTER_CHANGE";

            WriteEvidence(options, session, baselineTurbo, afterTurbo,
                baselinePower, afterPower, topology, rows, result);

            Console.WriteLine(result);
            Console.WriteLine($"Exact mailbox command: 0x{QueryPerCoreRatioCapabilities:X16}");
            Console.WriteLine($"Query MSR writes attempted: {rows.Count}");
            Console.WriteLine($"Primary turbo-ratio register unchanged: {turboUnchanged}");
            Console.WriteLine($"RAPL package-limit register unchanged: {powerUnchanged}");

            foreach (var row in rows)
                PrintRow(row);

            Console.WriteLine($"Evidence: {options.OutputDirectory}");

            if (!turboUnchanged || !powerUnchanged)
                return 3;

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("OC mailbox ratio query refused/failed: " + ex.Message);
            return 2;
        }
    }

    private static MailboxRow QueryOne(
        PawnIoModuleSession session,
        int logicalProcessor,
        LogicalTopology topology)
    {
        return RunOnLogicalProcessor(logicalProcessor, () =>
        {
            try
            {
                session.Execute(
                    "ioctl_write_msr",
                    new ulong[] { MsrOcMailbox, QueryPerCoreRatioCapabilities },
                    0);
            }
            catch (Exception ex)
            {
                return new MailboxRow(
                    logicalProcessor,
                    topology.CoreType,
                    topology.X2ApicId,
                    false,
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    ex.GetType().Name + ": " + ex.Message);
            }

            ulong response = 0;
            Exception? readFailure = null;

            for (var attempt = 1; attempt <= MaxMailboxReads; attempt++)
            {
                try
                {
                    response = ReadMsrCurrentProcessor(session, MsrOcMailbox);
                }
                catch (Exception ex)
                {
                    readFailure = ex;
                    break;
                }

                if ((response & (1UL << 63)) == 0)
                {
                    var completionCode = (int)((response >> 32) & 0xFF);
                    return new MailboxRow(
                        logicalProcessor,
                        topology.CoreType,
                        topology.X2ApicId,
                        true,
                        true,
                        false,
                        completionCode,
                        (int)(response & 0xFF),
                        (int)((response >> 8) & 0xFF),
                        (int)((response >> 16) & 0xFF),
                        $"0x{response:X16}",
                        completionCode == 0 ? "OK" : $"MAILBOX_COMPLETION_{completionCode}");
                }

                Thread.SpinWait(128);
            }

            if (readFailure is not null)
            {
                return new MailboxRow(
                    logicalProcessor,
                    topology.CoreType,
                    topology.X2ApicId,
                    true,
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    readFailure.GetType().Name + ": " + readFailure.Message);
            }

            return new MailboxRow(
                logicalProcessor,
                topology.CoreType,
                topology.X2ApicId,
                true,
                true,
                true,
                null,
                null,
                null,
                null,
                $"0x{response:X16}",
                "MAILBOX_BUSY_AFTER_BOUNDED_POLL");
        });
    }

    private static void WriteEvidence(
        ProbeOptions options,
        PawnIoModuleSession session,
        ulong baselineTurbo,
        ulong afterTurbo,
        ulong baselinePower,
        ulong afterPower,
        IReadOnlyList<LogicalTopology> topology,
        IReadOnlyList<MailboxRow> rows,
        string result)
    {
        var moduleHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(options.ModulePath)));

        var evidence = new
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Result = result,
            Target = HardwareIdentityReader.ReadCurrent(),
            CpuName = ReadCpuName(),
            LogicalProcessors = Environment.ProcessorCount,
            PawnIoVersion = session.DriverVersion.ToString(),
            IntelModuleSha256 = moduleHash,

            Semantics = new
            {
                PhysicalReadOnly = false,
                QueryOnly = true,
                ConfigurationMutationRequested = false,
                ArbitraryMsrWriteSurface = false,
                ExactMsr = $"0x{MsrOcMailbox:X}",
                ExactCommand = $"0x{QueryPerCoreRatioCapabilities:X16}",
                Command = "OC_MAILBOX command 0x1C per-core ratio capability query",
                QueryMsrWritesAttempted = rows.Count
            },

            GuardRegisters = new
            {
                PrimaryTurboRatioLimit = new
                {
                    Msr = $"0x{MsrTurboRatioLimit:X}",
                    Before = $"0x{baselineTurbo:X16}",
                    After = $"0x{afterTurbo:X16}",
                    Unchanged = baselineTurbo == afterTurbo
                },
                PackagePowerLimit = new
                {
                    Msr = $"0x{MsrPkgPowerLimit:X}",
                    Before = $"0x{baselinePower:X16}",
                    After = $"0x{afterPower:X16}",
                    Unchanged = baselinePower == afterPower
                }
            },

            Topology = topology.Select((entry, logical) => new
            {
                LogicalProcessor = logical,
                entry.CoreType,
                entry.X2ApicId
            }).ToArray(),

            MailboxResults = rows
        };

        WriteJson(Path.Combine(options.OutputDirectory, "oc-mailbox-ratio-query.json"), evidence);

        var summary = new
        {
            CompletedUtc = DateTimeOffset.UtcNow,
            Result = result,
            QueryOnly = true,
            QueryMsrWritesAttempted = rows.Count,
            SuccessfulCompletionCount = rows.Count(row =>
                row.WriteAccepted && row.ReadAccepted && !row.Busy && row.CompletionCode == 0),
            PrimaryTurboRatioUnchanged = baselineTurbo == afterTurbo,
            PackagePowerLimitUnchanged = baselinePower == afterPower,
            PcoreReplies = rows
                .Where(row => row.CoreType == "Performance")
                .Select(row => new
                {
                    row.LogicalProcessor,
                    row.CurrentMaxRatio,
                    row.FusedMaxRatio,
                    row.FavoredCoreIndex,
                    row.CompletionCode,
                    row.Status
                })
                .ToArray(),
            EcoreReplies = rows
                .Where(row => row.CoreType == "Efficiency")
                .Select(row => new
                {
                    row.LogicalProcessor,
                    row.CurrentMaxRatio,
                    row.FusedMaxRatio,
                    row.FavoredCoreIndex,
                    row.CompletionCode,
                    row.Status
                })
                .ToArray()
        };

        WriteJson(Path.Combine(options.OutputDirectory, "oc-mailbox-ratio-summary.json"), summary);
    }

    private static void PrintRow(MailboxRow row)
    {
        Console.WriteLine(
            $"LP{row.LogicalProcessor:00} {row.CoreType,-11} " +
            $"completion={row.CompletionCode?.ToString() ?? "-"} " +
            $"current={row.CurrentMaxRatio?.ToString() ?? "-"} " +
            $"fused={row.FusedMaxRatio?.ToString() ?? "-"} " +
            $"favored={row.FavoredCoreIndex?.ToString() ?? "-"} " +
            $"status={row.Status}");
    }

    private static ProbeOptions Parse(string[] args)
    {
        if (args.Length != 4 ||
            args[0] != "--module" ||
            args[2] != "--output-dir")
        {
            throw new ArgumentException(
                "Use --oc-mailbox-ratio-query --module <IntelMSR.bin> --output-dir <new-dir>.");
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
                    throw new InvalidOperationException(
                        $"Close {name} before the OC mailbox characterization.");
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

    private static LogicalTopology ReadLogicalTopology(int logicalProcessor)
    {
        return RunOnLogicalProcessor(logicalProcessor, () =>
        {
            var maxBasic = unchecked((uint)X86Base.CpuId(0, 0).Eax);
            uint x2ApicId = unchecked((uint)X86Base.CpuId(1, 0).Ebx) >> 24;

            if (maxBasic >= 0x1F)
            {
                for (var subleaf = 0; subleaf < 8; subleaf++)
                {
                    var registers = X86Base.CpuId(0x1F, subleaf);
                    if ((unchecked((uint)registers.Ebx) & 0xFFFF) == 0)
                        break;

                    x2ApicId = unchecked((uint)registers.Edx);
                }
            }

            var coreType = "Unknown";
            if (maxBasic >= 0x1A)
            {
                var hybrid = X86Base.CpuId(0x1A, 0);
                var rawType = (unchecked((uint)hybrid.Eax) >> 24) & 0xFF;

                coreType = rawType switch
                {
                    0x40 => "Performance",
                    0x20 => "Efficiency",
                    _ => $"Unknown_0x{rawType:X2}"
                };
            }

            return new LogicalTopology(coreType, x2ApicId);
        });
    }

    private static ulong ReadMsr(
        PawnIoModuleSession session,
        int logicalProcessor,
        uint msr)
    {
        return RunOnLogicalProcessor(
            logicalProcessor,
            () => ReadMsrCurrentProcessor(session, msr));
    }

    private static ulong ReadMsrCurrentProcessor(
        PawnIoModuleSession session,
        uint msr)
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
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "GetSystemPowerStatus failed.");

        return status.AcLineStatus == 1;
    }

    private static void WriteJson(string path, object value)
    {
        var json = JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static T RunOnLogicalProcessor<T>(
        int logicalProcessor,
        Func<T> action)
    {
        if (logicalProcessor < 0 || logicalProcessor >= IntPtr.Size * 8)
            throw new ArgumentOutOfRangeException(nameof(logicalProcessor));

        var requestedMask = new UIntPtr(1UL << logicalProcessor);
        var thread = GetCurrentThread();
        var previousMask = SetThreadAffinityMask(thread, requestedMask);

        if (previousMask == UIntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"SetThreadAffinityMask failed for logical processor {logicalProcessor}.");
        }

        try
        {
            return action();
        }
        finally
        {
            if (SetThreadAffinityMask(thread, previousMask) == UIntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to restore thread affinity.");
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(
        IntPtr hThread,
        UIntPtr dwThreadAffinityMask);

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

    private readonly record struct LogicalTopology(
        string CoreType,
        uint X2ApicId);

    private sealed record MailboxRow(
        int LogicalProcessor,
        string CoreType,
        uint X2ApicId,
        bool WriteAccepted,
        bool ReadAccepted,
        bool Busy,
        int? CompletionCode,
        int? CurrentMaxRatio,
        int? FusedMaxRatio,
        int? FavoredCoreIndex,
        string? RawResponse,
        string Status);
}
