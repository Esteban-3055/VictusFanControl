using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class PerformanceGuardianCpuGate6GQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const string ExpectedCpuToken =
        "i7-13700H";

    private static string ActiveJournalPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl",
            "Performance",
            TargetProfileId,
            "cpu-power-session.json");

    internal static int RunPreflight(
        string[] args)
    {
        if (!TryParsePreflight(
                args,
                out var modulePath,
                out var outputPath,
                out var error))
        {
            Console.Error.WriteLine(
                error);

            return 2;
        }

        var capturedAt =
            DateTimeOffset.UtcNow;

        string? cpuName =
            null;

        string? moduleSha256 =
            null;

        PerformancePowerSourceObservation? source =
            null;

        Version? pawnIoVersion =
            null;

        int? physicalCoreCount =
            null;

        ulong? unitsRaw =
            null;

        ulong? powerInfoRaw =
            null;

        double? powerUnitWatts =
            null;

        CpuRaplPowerInfo? powerInfo =
            null;

        var baselineSamples =
            new List<CpuPowerLimitSnapshot>();

        CpuPowerLimitApplyPlan? acPlan =
            null;

        CpuPowerLimitApplyPlan? batteryPlan =
            null;

        string? acPlanFailure =
            null;

        string? batteryPlanFailure =
            null;

        var mutexAvailable =
            false;

        var activeJournalPresent =
            File.Exists(
                ActiveJournalPath);

        var baselineStable =
            false;

        var pl1Enabled =
            false;

        var pl2Enabled =
            false;

        var writeGateClosedProved =
            false;

        string? failure =
            null;

        try
        {
            RequireExactTargetAndElevation();

            cpuName =
                ReadCpuName();

            if (string.IsNullOrWhiteSpace(
                    cpuName) ||
                !cpuName.Contains(
                    ExpectedCpuToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Exact CPU identity mismatch. Expected Intel Core i7-13700H; observed: " +
                    (cpuName ??
                     "<unavailable>"));
            }

            if (!File.Exists(
                    modulePath))
            {
                throw new FileNotFoundException(
                    "Signed IntelMSR.bin is required for the read-only CPU preflight.",
                    modulePath);
            }

            moduleSha256 =
                Convert.ToHexString(
                        SHA256.HashData(
                            File.ReadAllBytes(
                                modulePath)))
                    .ToLowerInvariant();

            if (activeJournalPresent)
            {
                throw new InvalidOperationException(
                    "A stale Step 6G CPU journal exists. No new CPU authority is permitted until it is reviewed: " +
                    ActiveJournalPath);
            }

            using (var mutex =
                   new Mutex(
                       initiallyOwned: true,
                       PerformanceGuardianHost.ProductionMutexName(
                           TargetProfileId),
                       out var createdNew))
            {
                mutexAvailable =
                    createdNew;

                if (!createdNew)
                {
                    throw new InvalidOperationException(
                        "Another PerformanceGuardian target writer already owns the production mutex.");
                }

                mutex.ReleaseMutex();
            }

            source =
                new WindowsPerformancePowerSourceReader()
                    .Read();

            if (!source.Value.Succeeded ||
                source.Value.Source !=
                    PerformancePowerSourceKind.Ac)
            {
                throw new InvalidOperationException(
                    "Step 6G CPU preflight requires directly confirmed AC before any later physical qualification.");
            }

            using var backend =
                new PawnIoCpuPowerLimitBackend(
                    modulePath,
                    hardwareWritesAuthorized:
                        false);

            pawnIoVersion =
                backend.PawnIoVersion;

            physicalCoreCount =
                backend.PhysicalCoreCount;

            unitsRaw =
                backend.UnitsRaw;

            powerInfoRaw =
                backend.PowerInfoRaw;

            powerUnitWatts =
                backend.PowerUnitWatts;

            powerInfo =
                backend.PowerInfo;

            if (pawnIoVersion.Major < 2 ||
                (pawnIoVersion.Major == 2 &&
                 pawnIoVersion.Minor < 2))
            {
                throw new InvalidOperationException(
                    "Step 6G requires PawnIO 2.2 or newer; observed " +
                    pawnIoVersion +
                    ".");
            }

            if (physicalCoreCount !=
                14)
            {
                throw new InvalidOperationException(
                    "Expected 14 physical CPU cores for i7-13700H; observed " +
                    physicalCoreCount +
                    ".");
            }

            for (var index = 0;
                 index < 3;
                 index++)
            {
                baselineSamples.Add(
                    backend.Read());

                if (index !=
                    2)
                {
                    Thread.Sleep(
                        100);
                }
            }

            baselineStable =
                baselineSamples
                    .Select(
                        sample =>
                            sample.Raw)
                    .Distinct()
                    .Count() ==
                1;

            if (!baselineStable)
            {
                throw new InvalidOperationException(
                    "MSR 0x610 changed during the read-only preflight; CPU product writes remain closed.");
            }

            var baseline =
                baselineSamples[0];

            if (baseline.Locked)
            {
                throw new InvalidOperationException(
                    "MSR 0x610 lock bit is set; CPU product writes remain closed.");
            }

            pl1Enabled =
                (baseline.Raw &
                 (1UL << 15)) !=
                0;

            pl2Enabled =
                (baseline.Raw &
                 (1UL << 47)) !=
                0;

            if (!pl1Enabled ||
                !pl2Enabled)
            {
                throw new InvalidOperationException(
                    "Baseline PL1/PL2 enable bits are not both set; CPU product writes remain closed.");
            }

            var presets =
                CpuPowerProductDefaults
                    .CreateDefaultPresetSet();

            try
            {
                acPlan =
                    backend.BuildApplyPlan(
                        baseline,
                        presets.Ac.ToRequest());
            }
            catch (Exception ex)
            {
                acPlanFailure =
                    ex.Message;
            }

            try
            {
                batteryPlan =
                    backend.BuildApplyPlan(
                        baseline,
                        presets.Battery.ToRequest());
            }
            catch (Exception ex)
            {
                batteryPlanFailure =
                    ex.Message;
            }

            if (acPlan.HasValue)
            {
                try
                {
                    backend.Write(
                        acPlan.Value.RequestedRaw);

                    throw new InvalidOperationException(
                        "Closed-gate probe unexpectedly returned from Write.");
                }
                catch (InvalidOperationException ex)
                    when (string.Equals(
                        ex.Message,
                        "CPU_POWER_BACKEND_HARDWARE_WRITE_GATE_CLOSED",
                        StringComparison.Ordinal))
                {
                    writeGateClosedProved =
                        true;
                }
            }

            if (!writeGateClosedProved)
            {
                throw new InvalidOperationException(
                    "Step 6G could not prove the CPU backend hardware-write gate is closed.");
            }

            if (!acPlan.HasValue)
            {
                throw new InvalidOperationException(
                    "AC product preset 35/60 W is not admissible on the live RAPL baseline: " +
                    (acPlanFailure ??
                     "unknown reason"));
            }

            if (!batteryPlan.HasValue)
            {
                throw new InvalidOperationException(
                    "Battery product preset 8/15 W is not admissible on the live RAPL constraints: " +
                    (batteryPlanFailure ??
                     "unknown reason"));
            }
        }
        catch (Exception ex)
        {
            failure =
                ex.ToString();
        }

        var passed =
            failure is null &&
            mutexAvailable &&
            !activeJournalPresent &&
            source.HasValue &&
            source.Value.Succeeded &&
            source.Value.Source ==
                PerformancePowerSourceKind.Ac &&
            baselineSamples.Count ==
                3 &&
            baselineStable &&
            pl1Enabled &&
            pl2Enabled &&
            acPlan.HasValue &&
            batteryPlan.HasValue &&
            writeGateClosedProved;

        var report =
            new CpuPreflightReport(
                SchemaVersion:
                    1,
                TargetProfileId,
                CapturedAtUtc:
                    capturedAt,
                CpuName:
                    cpuName,
                ModulePath:
                    modulePath,
                ModuleSha256:
                    moduleSha256,
                PawnIoVersion:
                    pawnIoVersion?.ToString(),
                PhysicalCoreCount:
                    physicalCoreCount,
                Source:
                    source,
                UnitsRaw:
                    unitsRaw,
                UnitsRawHex:
                    Hex(
                        unitsRaw),
                PowerInfoRaw:
                    powerInfoRaw,
                PowerInfoRawHex:
                    Hex(
                        powerInfoRaw),
                PowerUnitWatts:
                    powerUnitWatts,
                PowerInfo:
                    powerInfo,
                BaselineSamples:
                    baselineSamples.ToArray(),
                BaselineRawHex:
                    baselineSamples.Count >
                        0
                        ? "0x" +
                          baselineSamples[0].Raw
                              .ToString("X16")
                        : null,
                BaselineStable:
                    baselineStable,
                Pl1Enabled:
                    pl1Enabled,
                Pl2Enabled:
                    pl2Enabled,
                AcRequest:
                    new CpuPowerLimitRequest(
                        CpuPowerProductDefaults.DefaultAcPl1Watts,
                        CpuPowerProductDefaults.DefaultAcPl2Watts),
                AcPlan:
                    acPlan,
                AcPlanFailure:
                    acPlanFailure,
                BatteryRequest:
                    new CpuPowerLimitRequest(
                        CpuPowerProductDefaults.DefaultBatteryPl1Watts,
                        CpuPowerProductDefaults.DefaultBatteryPl2Watts),
                BatteryPlan:
                    batteryPlan,
                BatteryPlanFailure:
                    batteryPlanFailure,
                ActiveJournalPath,
                ActiveJournalPresent:
                    activeJournalPresent,
                ProductionMutexAvailable:
                    mutexAvailable,
                HardwareWriteGateClosedProved:
                    writeGateClosedProved,
                HardwareWritesPerformed:
                    false,
                Result:
                    passed
                        ? "PASS"
                        : "FAIL",
                Failure:
                    failure);

        if (!string.IsNullOrWhiteSpace(
                outputPath))
        {
            DurableJson(
                outputPath!,
                report);

            Console.WriteLine(
                "Step 6G CPU read-only preflight report: " +
                outputPath);
        }

        if (!passed)
        {
            Console.Error.WriteLine(
                "Step 6G CPU read-only preflight: FAIL. No hardware write was performed.");

            if (powerInfo.HasValue)
            {
                Console.Error.WriteLine(
                    $"RAPL 0x614 reports minimum PL1={powerInfo.Value.MinimumWatts:0.###} W, maximum={powerInfo.Value.MaximumWatts:0.###} W.");
            }

            if (!string.IsNullOrWhiteSpace(
                    batteryPlanFailure))
            {
                Console.Error.WriteLine(
                    "Battery 8/15 validation: " +
                    batteryPlanFailure);
            }

            return 5;
        }

        Console.WriteLine(
            $"Step 6G CPU read-only preflight: PASS. Live 0x614 minimum={powerInfo!.Value.MinimumWatts:0.###} W; AC 35/60 and Battery 8/15 are admissible; hardware write gate remained closed.");

        return 0;
    }

    internal static int SelfTest(
        TextWriter output)
    {
        try
        {
            var module =
                Path.Combine(
                    Path.GetTempPath(),
                    "IntelMSR.bin");

            var report =
                Path.Combine(
                    Path.GetTempPath(),
                    "cpu-6g-preflight.json");

            Require(
                TryParsePreflight(
                    new[]
                    {
                        "--cpu-gate-6g-preflight",
                        "--confirm-target",
                        TargetProfileId,
                        "--module",
                        module,
                        "--output",
                        report
                    },
                    out var parsedModule,
                    out var parsedOutput,
                    out _),
                "Step 6G preflight arguments parse");

            Require(
                string.Equals(
                    parsedModule,
                    Path.GetFullPath(
                        module),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    parsedOutput,
                    Path.GetFullPath(
                        report),
                    StringComparison.OrdinalIgnoreCase),
                "Step 6G parser preserves exact module/report paths");

            Require(
                !TryParsePreflight(
                    new[]
                    {
                        "--cpu-gate-6g-preflight",
                        "--confirm-target",
                        "WRONG",
                        "--module",
                        module
                    },
                    out _,
                    out _,
                    out _),
                "wrong target rejected");

            Require(
                !TryParsePreflight(
                    new[]
                    {
                        "--cpu-gate-6g-preflight",
                        "--confirm-target",
                        TargetProfileId
                    },
                    out _,
                    out _,
                    out _),
                "missing module rejected");

            output.WriteLine(
                "Step 6G CPU preflight harness self-test: PASS (argument/target gates only, no PawnIO load and zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6G CPU preflight harness self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static bool TryParsePreflight(
        string[] args,
        out string modulePath,
        out string? outputPath,
        out string error)
    {
        modulePath =
            string.Empty;

        outputPath =
            null;

        error =
            "Invalid Step 6G CPU preflight arguments.";

        string? confirmTarget =
            null;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            var name =
                args[index];

            if (index + 1 >=
                args.Length)
            {
                error =
                    "Incomplete Step 6G CPU preflight argument: " +
                    name;

                return false;
            }

            var value =
                args[++index];

            switch (name)
            {
                case "--confirm-target":
                    confirmTarget =
                        value;
                    break;

                case "--module":
                    modulePath =
                        Path.GetFullPath(
                            value);
                    break;

                case "--output":
                    outputPath =
                        Path.GetFullPath(
                            value);
                    break;

                default:
                    error =
                        "Unknown Step 6G CPU preflight argument: " +
                        name;

                    return false;
            }
        }

        if (!string.Equals(
                confirmTarget,
                TargetProfileId,
                StringComparison.Ordinal))
        {
            error =
                "Exact Step 6G target confirmation token is required.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                modulePath))
        {
            error =
                "Step 6G CPU preflight requires --module <IntelMSR.bin>.";

            return false;
        }

        return true;
    }

    private static void RequireExactTargetAndElevation()
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.Is64BitProcess)
        {
            throw new InvalidOperationException(
                "Step 6G requires Windows x64.");
        }

        using var identity =
            WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(
                identity)
            .IsInRole(
                WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "Step 6G read-only PawnIO qualification requires an elevated Administrator process.");
        }

        var hardware =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var reason))
        {
            throw new InvalidOperationException(
                "Exact HP-8C40-9D0R1LA-F18 target mismatch: " +
                reason);
        }
    }

    private static string? ReadCpuName() =>
        Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                null)
            ?.ToString()
            ?.Trim();

    private static string? Hex(
        ulong? value) =>
        value.HasValue
            ? "0x" +
              value.Value.ToString(
                  "X16")
            : null;

    private static void DurableJson<T>(
        string path,
        T value)
    {
        var directory =
            Path.GetDirectoryName(
                path);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        var temporary =
            path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream =
                   new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    value,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(
                    temporary))
            {
                File.Delete(
                    temporary);
            }
        }
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                label);
        }
    }

    private sealed record CpuPreflightReport(
        int SchemaVersion,
        string TargetProfileId,
        DateTimeOffset CapturedAtUtc,
        string? CpuName,
        string ModulePath,
        string? ModuleSha256,
        string? PawnIoVersion,
        int? PhysicalCoreCount,
        PerformancePowerSourceObservation? Source,
        ulong? UnitsRaw,
        string? UnitsRawHex,
        ulong? PowerInfoRaw,
        string? PowerInfoRawHex,
        double? PowerUnitWatts,
        CpuRaplPowerInfo? PowerInfo,
        CpuPowerLimitSnapshot[] BaselineSamples,
        string? BaselineRawHex,
        bool BaselineStable,
        bool Pl1Enabled,
        bool Pl2Enabled,
        CpuPowerLimitRequest AcRequest,
        CpuPowerLimitApplyPlan? AcPlan,
        string? AcPlanFailure,
        CpuPowerLimitRequest BatteryRequest,
        CpuPowerLimitApplyPlan? BatteryPlan,
        string? BatteryPlanFailure,
        string ActiveJournalPath,
        bool ActiveJournalPresent,
        bool ProductionMutexAvailable,
        bool HardwareWriteGateClosedProved,
        bool HardwareWritesPerformed,
        string Result,
        string? Failure);
}
