using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Performance;

namespace VictusFanControl.GpuProbe;

internal static class Program
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const string TargetDeviceName =
        "NVIDIA GeForce RTX 4060 Laptop GPU";

    public static int Main(
        string[] args)
    {
        try
        {
            if (args.Length == 1 &&
                args[0] == "--self-test")
            {
                return SelfTest();
            }

            if (!TryParseObserveArgs(
                    args,
                    out var label,
                    out var outputPath))
            {
                PrintHelp();
                return 2;
            }

            using var client =
                new NvmlClient(
                    TargetDeviceName,
                    requirePreferredDevice: true);

            var current =
                client.ReadCurrentGraphicsClockOnce();

            var appTarget =
                client.ReadApplicationGraphicsClockTargetOnce();

            var eventReasons =
                client.ReadCurrentClocksEventReasonsOnce();

            var availability =
                client.GpuClockControlAvailability;

            var capabilities =
                new GpuClockBackendCapabilities(
                    availability
                        .SetLockedGraphicsClocksExportAvailable,
                    availability
                        .ResetLockedGraphicsClocksExportAvailable,
                    availability
                        .CurrentGraphicsClockExportAvailable,
                    availability
                        .ApplicationGraphicsClockTargetExportAvailable,
                    availability
                        .CurrentClocksEventReasonsExportAvailable,
                    availability
                        .ExactLockedRangeReadbackAvailable,
                    HardwareWritesAuthorized: false);

            var observation =
                new GpuClockBackendObservation(
                    Succeeded:
                        current.IsSuccess,
                    CurrentGraphicsClockMHz:
                        current.IsSuccess
                            ? current.Value
                            : null,
                    ApplicationGraphicsClockTargetMHz:
                        appTarget.IsSuccess
                            ? appTarget.Value
                            : null,
                    CurrentClocksEventReasons:
                        eventReasons.IsSuccess
                            ? eventReasons.Value
                            : null,
                    ExactLockedRange: null,
                    ProvesExactLockedRangeOwnership:
                        false,
                    FailureKind:
                        current.IsSuccess
                            ? GpuClockBackendFailureKind.None
                            : GpuClockBackendFailureKind.Unknown,
                    NvmlResult:
                        current.Result,
                    Status:
                        "READ_ONLY_GPU_CLOCK_OBSERVABILITY_PROBE");

            var ac =
                GpuClockOwnershipQualification.Assess(
                    new GpuClockLimitRequest(
                        210,
                        1850),
                    capabilities,
                    observation);

            var battery =
                GpuClockOwnershipQualification.Assess(
                    new GpuClockLimitRequest(
                        210,
                        1200),
                    capabilities,
                    observation);

            var report =
                new GpuObservabilityReport(
                    SchemaVersion: 1,
                    CapturedAtUtc:
                        DateTimeOffset.UtcNow,
                    TargetProfileId,
                    Label: label,
                    DeviceName:
                        client.DeviceName,
                    Availability:
                        availability,
                    CurrentGraphicsClock:
                        current,
                    ApplicationGraphicsClockTarget:
                        appTarget,
                    CurrentClocksEventReasons:
                        eventReasons,
                    AcOwnershipQualification:
                        ac,
                    BatteryOwnershipQualification:
                        battery,
                    HardwareWritesPerformed:
                        false,
                    Notes:
                        "Read-only. APP_CLOCK_TARGET and event reasons are diagnostic only; exact locked min/max is not exposed by public NVML.");

            var json =
                JsonSerializer.Serialize(
                    report,
                    JsonOptions);

            Console.WriteLine(json);

            if (!string.IsNullOrWhiteSpace(
                    outputPath))
            {
                DurableWrite(
                    outputPath!,
                    json);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "GPU NVML observability probe failed: " +
                ex.Message);

            return 3;
        }
    }

    private static int SelfTest()
    {
        var availability =
            new NvmlGpuClockControlAvailability(
                true,
                true,
                true,
                true,
                true,
                false);

        var report =
            new GpuObservabilityReport(
                SchemaVersion: 1,
                CapturedAtUtc:
                    DateTimeOffset.UnixEpoch,
                TargetProfileId,
                Label: "fixture",
                DeviceName:
                    TargetDeviceName,
                Availability:
                    availability,
                CurrentGraphicsClock:
                    new NvmlUIntCallResult(
                        true,
                        0,
                        210),
                ApplicationGraphicsClockTarget:
                    new NvmlUIntCallResult(
                        true,
                        3,
                        0),
                CurrentClocksEventReasons:
                    new NvmlULongCallResult(
                        true,
                        3,
                        0),
                AcOwnershipQualification:
                    BlockedFixtureAssessment(
                        210,
                        1850),
                BatteryOwnershipQualification:
                    BlockedFixtureAssessment(
                        210,
                        1200),
                HardwareWritesPerformed:
                    false,
                Notes:
                    "fixture");

        var json =
            JsonSerializer.Serialize(
                report,
                JsonOptions);

        if (string.IsNullOrWhiteSpace(
                json) ||
            report.HardwareWritesPerformed ||
            report.AcOwnershipQualification
                .ManagedOwnershipAllowed ||
            report.BatteryOwnershipQualification
                .ManagedOwnershipAllowed)
        {
            throw new InvalidOperationException(
                "read-only probe fixture invariant failed");
        }

        Console.WriteLine(
            "GPU NVML observability probe self-test: PASS (no NVML load, no hardware I/O).");

        return 0;
    }

    private static GpuClockOwnershipQualificationResult
        BlockedFixtureAssessment(
            uint minMHz,
            uint maxMHz)
    {
        var capabilities =
            new GpuClockBackendCapabilities(
                true,
                true,
                true,
                true,
                true,
                false,
                false);

        var observation =
            new GpuClockBackendObservation(
                true,
                210,
                null,
                null,
                null,
                false,
                GpuClockBackendFailureKind.None,
                0,
                "FIXTURE");

        return GpuClockOwnershipQualification.Assess(
            new GpuClockLimitRequest(
                minMHz,
                maxMHz),
            capabilities,
            observation);
    }

    private static bool TryParseObserveArgs(
        string[] args,
        out string label,
        out string? outputPath)
    {
        label = "manual";
        outputPath = null;

        if (args.Length < 1 ||
            args[0] != "--observe")
        {
            return false;
        }

        for (var index = 1;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--label"
                    when index + 1 <
                         args.Length:
                    label =
                        args[++index];
                    break;

                case "--output"
                    when index + 1 <
                         args.Length:
                    outputPath =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    return false;
            }
        }

        return !string.IsNullOrWhiteSpace(
            label);
    }

    private static void DurableWrite(
        string path,
        string content)
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

        using var stream =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);

        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false),
                4096,
                leaveOpen: true);

        writer.Write(
            content);

        writer.Flush();
        stream.Flush(
            flushToDisk: true);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            "VictusFanControl.GpuProbe --observe [--label <name>] [--output <json-path>]");

        Console.WriteLine(
            "VictusFanControl.GpuProbe --self-test");

        Console.WriteLine(
            "This probe is read-only and never calls GPU clock Set/Reset.");
    }

    private static readonly JsonSerializerOptions
        JsonOptions =
            new()
            {
                WriteIndented = true
            };

    private readonly record struct GpuObservabilityReport(
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        string TargetProfileId,
        string Label,
        string DeviceName,
        NvmlGpuClockControlAvailability Availability,
        NvmlUIntCallResult CurrentGraphicsClock,
        NvmlUIntCallResult ApplicationGraphicsClockTarget,
        NvmlULongCallResult CurrentClocksEventReasons,
        GpuClockOwnershipQualificationResult
            AcOwnershipQualification,
        GpuClockOwnershipQualificationResult
            BatteryOwnershipQualification,
        bool HardwareWritesPerformed,
        string Notes);
}
