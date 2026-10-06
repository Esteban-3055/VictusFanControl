using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;

namespace VictusFanControl.CpuProbe;

internal static class FrequencyReadOnlyProbe
{
    private const uint MsrPlatformInfo = 0xCE;
    private const uint MsrIa32PerfStatus = 0x198;
    private const uint MsrTurboRatioLimit = 0x1AD;
    private const uint MsrPkgPowerLimit = 0x610;
    private const uint MsrIa32PmEnable = 0x770;
    private const uint MsrIa32HwpCapabilities = 0x771;
    private const uint MsrIa32HwpRequest = 0x774;

    internal static int Run(string[] args)
    {
        try
        {
            var options = Parse(args);
            ValidateEnvironment(options.ModulePath);

            Directory.CreateDirectory(options.OutputDirectory);
            using var session = new PawnIoModuleSession(options.ModulePath);

            var cpuid6 = X86Base.CpuId(0x06, 0);
            var cpuid6Eax = unchecked((uint)cpuid6.Eax);
            var logicalCount = Environment.ProcessorCount;
            var topology = Enumerable.Range(0, logicalCount)
                .Select(ReadLogicalTopology)
                .ToArray();

            var platformInfo = ReadRequired(session, 0, MsrPlatformInfo);
            var turboRatioLimit = ReadRequired(session, 0, MsrTurboRatioLimit);
            var packagePowerLimit = ReadRequired(session, 0, MsrPkgPowerLimit);

            var hwpRows = new List<object>(logicalCount);
            var hwpRequestReadable = true;

            foreach (var logical in Enumerable.Range(0, logicalCount))
            {
                var pmEnable = TryRead(session, logical, MsrIa32PmEnable);
                var capabilities = TryRead(session, logical, MsrIa32HwpCapabilities);
                var request = TryRead(session, logical, MsrIa32HwpRequest);
                hwpRequestReadable &= request.Success;

                hwpRows.Add(new
                {
                    LogicalProcessor = logical,
                    topology[logical].CoreType,
                    topology[logical].X2ApicId,
                    PmEnable = DecodePmEnable(pmEnable),
                    HwpCapabilities = DecodeCapabilities(capabilities),
                    HwpRequest = DecodeRequest(request)
                });
            }

            var moduleHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.ModulePath)));
            var staticEvidence = new
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                Target = HardwareIdentityReader.ReadCurrent(),
                CpuName = ReadCpuName(),
                LogicalProcessors = logicalCount,
                PawnIoVersion = session.DriverVersion.ToString(),
                IntelModuleSha256 = moduleHash,
                ReadOnly = true,
                WritesAttempted = 0,
                Cpuid06 = new
                {
                    Eax = $"0x{cpuid6Eax:X8}",
                    HwpBaseline = Bit(cpuid6Eax, 7),
                    HwpNotification = Bit(cpuid6Eax, 8),
                    HwpActivityWindow = Bit(cpuid6Eax, 9),
                    HwpEnergyPerformancePreference = Bit(cpuid6Eax, 10),
                    HwpPackageLevelRequest = Bit(cpuid6Eax, 11)
                },
                PlatformInfo = new
                {
                    Raw = Hex(platformInfo),
                    MaxNonTurboRatioField = (int)((platformInfo >> 8) & 0xFF),
                    ProgrammableTurboRatioLimit = ((platformInfo >> 28) & 1UL) != 0
                },
                TurboRatioLimit = new
                {
                    Raw = Hex(turboRatioLimit),
                    RatioFields = Enumerable.Range(0, 8)
                        .Select(index => (int)((turboRatioLimit >> (index * 8)) & 0xFF))
                        .ToArray()
                },
                PackagePowerLimitRaw = Hex(packagePowerLimit),
                HwpMsrReadAccess = hwpRows
            };

            WriteJson(Path.Combine(options.OutputDirectory, "frequency-static.json"), staticEvidence);

            var csvPath = Path.Combine(options.OutputDirectory, "frequency-samples.csv");
            using (var writer = new StreamWriter(csvPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("TimestampUtc,LogicalProcessor,CoreType,PerfStatusRaw,PerfRatioField15_8,HwpRequestRaw,HwpMin,HwpMax,HwpDesired,HwpEpp,HwpReadStatus");

                for (var second = 0; second < options.DurationSeconds; second++)
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    foreach (var logical in Enumerable.Range(0, logicalCount))
                    {
                        var perfStatus = ReadRequired(session, logical, MsrIa32PerfStatus);
                        ReadResult hwp = hwpRequestReadable
                            ? TryRead(session, logical, MsrIa32HwpRequest)
                            : ReadResult.NotAttempted("SIGNED_MODULE_DENIED_INITIAL_HWP_REQUEST_READ");

                        var decoded = DecodeRequestFields(hwp);
                        writer.WriteLine(string.Join(",",
                            timestamp.ToString("O", CultureInfo.InvariantCulture),
                            logical.ToString(CultureInfo.InvariantCulture),
                            topology[logical].CoreType,
                            Hex(perfStatus),
                            ((perfStatus >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture),
                            decoded.Raw,
                            decoded.Min,
                            decoded.Max,
                            decoded.Desired,
                            decoded.Epp,
                            Csv(hwp.Status)));
                    }

                    writer.Flush();
                    if (second + 1 < options.DurationSeconds)
                        Thread.Sleep(1000);
                }
            }

            var summary = new
            {
                CompletedUtc = DateTimeOffset.UtcNow,
                Result = "READ_ONLY_COMPLETE",
                WritesAttempted = 0,
                HwpBaselineAdvertisedByCpuid = Bit(cpuid6Eax, 7),
                HwpRequestReadableThroughCurrentSignedPawnIoModule = hwpRequestReadable,
                TurboRatioLimitProgrammableAccordingToPlatformInfoBit28 =
                    ((platformInfo >> 28) & 1UL) != 0,
                EvidenceDirectory = options.OutputDirectory
            };
            WriteJson(Path.Combine(options.OutputDirectory, "frequency-summary.json"), summary);

            Console.WriteLine("READ_ONLY_COMPLETE");
            Console.WriteLine($"CPUID HWP baseline support: {Bit(cpuid6Eax, 7)}");
            Console.WriteLine($"Current signed PawnIO module can read IA32_HWP_REQUEST 0x774: {hwpRequestReadable}");
            Console.WriteLine($"MSR_PLATFORM_INFO[28] programmable turbo-ratio indication: {((platformInfo >> 28) & 1UL) != 0}");
            Console.WriteLine($"Evidence: {options.OutputDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Frequency read-only probe refused/failed: " + ex.Message);
            return 2;
        }
    }

    private static ProbeOptions Parse(string[] args)
    {
        if (args.Length != 6 ||
            args[0] != "--module" ||
            args[2] != "--output-dir" ||
            args[4] != "--duration-seconds" ||
            !int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 10 or > 120)
        {
            throw new ArgumentException(
                "Use --frequency-readonly --module <IntelMSR.bin> --output-dir <new-dir> --duration-seconds <10..120>.");
        }

        var module = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[3]);
        if (!File.Exists(module))
            throw new FileNotFoundException("Signed IntelMSR.bin was not found.", module);
        if (Directory.Exists(output))
            throw new IOException("Output directory must be new.");

        return new ProbeOptions(module, output, seconds);
    }

    private static void ValidateEnvironment(string modulePath)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !X86Base.IsSupported)
            throw new InvalidOperationException("An elevated Windows x64 process on an x86-64 CPU is required.");

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Administrator privileges are required for PawnIO MSR reads.");

        var target = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(target, out var reason))
            throw new InvalidOperationException("Exact 8C40/F.18 target mismatch: " + reason);

        var cpuName = ReadCpuName();
        if (!cpuName.Contains("i7-13700H", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Exact i7-13700H target mismatch: " + cpuName);

        if (Environment.ProcessorCount != 20)
            throw new InvalidOperationException(
                $"Expected 20 logical processors for this qualified target, found {Environment.ProcessorCount}.");

        _ = modulePath;
    }

    private static string ReadCpuName()
    {
        using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
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

    private static ulong ReadRequired(PawnIoModuleSession session, int logicalProcessor, uint msr)
    {
        return RunOnLogicalProcessor(logicalProcessor, () =>
        {
            var values = session.Execute("ioctl_read_msr", new ulong[] { msr }, 1);
            if (values.Length != 1)
                throw new InvalidDataException($"IntelMSR returned {values.Length} cells for MSR 0x{msr:X}.");
            return values[0];
        });
    }

    private static ReadResult TryRead(PawnIoModuleSession session, int logicalProcessor, uint msr)
    {
        try
        {
            return new ReadResult(true, ReadRequired(session, logicalProcessor, msr), "OK");
        }
        catch (Exception ex)
        {
            return new ReadResult(false, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static object DecodePmEnable(ReadResult result)
    {
        return new
        {
            result.Success,
            Raw = result.Success ? Hex(result.Value) : null,
            HwpEnable = result.Success ? (bool?)((result.Value & 1UL) != 0) : null,
            result.Status
        };
    }

    private static object DecodeCapabilities(ReadResult result)
    {
        return new
        {
            result.Success,
            Raw = result.Success ? Hex(result.Value) : null,
            Highest = result.Success ? (int?)(result.Value & 0xFF) : null,
            Guaranteed = result.Success ? (int?)((result.Value >> 8) & 0xFF) : null,
            MostEfficient = result.Success ? (int?)((result.Value >> 16) & 0xFF) : null,
            Lowest = result.Success ? (int?)((result.Value >> 24) & 0xFF) : null,
            result.Status
        };
    }

    private static object DecodeRequest(ReadResult result)
    {
        var fields = DecodeRequestFields(result);
        return new
        {
            result.Success,
            fields.Raw,
            fields.Min,
            fields.Max,
            fields.Desired,
            fields.Epp,
            result.Status
        };
    }

    private static RequestFields DecodeRequestFields(ReadResult result)
    {
        if (!result.Success)
            return new RequestFields("", "", "", "", "");

        return new RequestFields(
            Hex(result.Value),
            (result.Value & 0xFF).ToString(CultureInfo.InvariantCulture),
            ((result.Value >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture),
            ((result.Value >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture),
            ((result.Value >> 24) & 0xFF).ToString(CultureInfo.InvariantCulture));
    }

    private static bool Bit(uint value, int bit) => (value & (1u << bit)) != 0;

    private static string Hex(ulong value) => $"0x{value:X16}";

    private static string Csv(string value) => """ + value.Replace(""", """", StringComparison.Ordinal) + """;

    private static void WriteJson(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static T RunOnLogicalProcessor<T>(int logicalProcessor, Func<T> action)
    {
        if (logicalProcessor < 0 || logicalProcessor >= IntPtr.Size * 8)
            throw new ArgumentOutOfRangeException(nameof(logicalProcessor));

        var requestedMask = new UIntPtr(1UL << logicalProcessor);
        var thread = GetCurrentThread();
        var previousMask = SetThreadAffinityMask(thread, requestedMask);
        if (previousMask == UIntPtr.Zero)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"SetThreadAffinityMask failed for logical processor {logicalProcessor}.");

        try
        {
            return action();
        }
        finally
        {
            if (SetThreadAffinityMask(thread, previousMask) == UIntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to restore thread affinity.");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

    private sealed record ProbeOptions(string ModulePath, string OutputDirectory, int DurationSeconds);
    private readonly record struct LogicalTopology(string CoreType, uint X2ApicId);
    private readonly record struct RequestFields(string Raw, string Min, string Max, string Desired, string Epp);
    private readonly record struct ReadResult(bool Success, ulong Value, string Status)
    {
        public static ReadResult NotAttempted(string status) => new(false, 0, status);
    }
}
