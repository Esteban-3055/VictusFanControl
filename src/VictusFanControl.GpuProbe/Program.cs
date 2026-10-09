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

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 &&
                args[0] == "--self-test")
            {
                return SelfTest();
            }

            if (args.Length == 1 &&
                args[0] == "--clock-write-self-test")
            {
                return GpuClockWriteQualification.SelfTest(
                    Console.Out);
            }

            if (args.Length > 0 &&
                args[0] == "--clock-write-test")
            {
                return GpuClockWriteQualification.Run(
                    args);
            }

            if (args.Length == 1 &&
                args[0] == "--clock-transition-self-test")
            {
                return GpuClockTransitionQualification.SelfTest(
                    Console.Out);
            }

            if (args.Length > 0 &&
                args[0] == "--clock-transition-test")
            {
                return GpuClockTransitionQualification.Run(
                    args);
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

            var clockAvailability =
                client.GpuClockControlAvailability;

            var clockCapabilities =
                new GpuClockBackendCapabilities(
                    clockAvailability.SetLockedGraphicsClocksExportAvailable,
                    clockAvailability.ResetLockedGraphicsClocksExportAvailable,
                    clockAvailability.CurrentGraphicsClockExportAvailable,
                    clockAvailability.ApplicationGraphicsClockTargetExportAvailable,
                    clockAvailability.CurrentClocksEventReasonsExportAvailable,
                    clockAvailability.ExactLockedRangeReadbackAvailable,
                    HardwareWritesAuthorized: false);

            var clockObservation =
                new GpuClockBackendObservation(
                    Succeeded: current.IsSuccess,
                    CurrentGraphicsClockMHz:
                        current.IsSuccess ? current.Value : null,
                    ApplicationGraphicsClockTargetMHz:
                        appTarget.IsSuccess ? appTarget.Value : null,
                    CurrentClocksEventReasons:
                        eventReasons.IsSuccess ? eventReasons.Value : null,
                    ExactLockedRange: null,
                    ProvesExactLockedRangeOwnership: false,
                    FailureKind:
                        current.IsSuccess
                            ? GpuClockBackendFailureKind.None
                            : GpuClockBackendFailureKind.Unknown,
                    NvmlResult: current.Result,
                    Status:
                        "READ_ONLY_GPU_CLOCK_OBSERVABILITY_PROBE");

            var ac =
                GpuClockOwnershipQualification.Assess(
                    new GpuClockLimitRequest(210, 1850),
                    clockCapabilities,
                    clockObservation);

            var battery =
                GpuClockOwnershipQualification.Assess(
                    new GpuClockLimitRequest(210, 1200),
                    clockCapabilities,
                    clockObservation);

            var powerSnapshot =
                ReadPowerLimitSnapshot(client);

            var powerQualification =
                GpuPowerLimitQualification.Assess(
                    powerSnapshot);

            var powerFieldSnapshot =
                client.ReadPowerFieldSnapshotOnce();

            var powerFieldQualification =
                GpuPowerFieldQualification.Assess(
                    powerFieldSnapshot,
                    client.GpuPowerLimitAvailability
                        .SetPowerManagementLimitExportAvailable);

            var report =
                new GpuObservabilityReport(
                    SchemaVersion: 3,
                    CapturedAtUtc: DateTimeOffset.UtcNow,
                    TargetProfileId,
                    Label: label,
                    DeviceName: client.DeviceName,
                    Availability: clockAvailability,
                    CurrentGraphicsClock: current,
                    ApplicationGraphicsClockTarget: appTarget,
                    CurrentClocksEventReasons: eventReasons,
                    AcOwnershipQualification: ac,
                    BatteryOwnershipQualification: battery,
                    PowerAvailability: powerSnapshot.Availability,
                    PowerManagementMode: powerSnapshot.PowerManagementMode,
                    PowerManagementLimit: powerSnapshot.CurrentLimit,
                    DefaultPowerManagementLimit: powerSnapshot.DefaultLimit,
                    PowerManagementLimitConstraints: powerSnapshot.Constraints,
                    EnforcedPowerLimit: powerSnapshot.EnforcedLimit,
                    PowerLimitQualification: powerQualification,
                    PowerFieldSnapshot: powerFieldSnapshot,
                    PowerFieldQualification: powerFieldQualification,
                    HardwareWritesPerformed: false,
                    Notes:
                        "Read-only. Legacy GPU power-management getters plus NVML field IDs 187/188/189/190/192 are queried; no setter is invoked.");

            var json =
                JsonSerializer.Serialize(
                    report,
                    JsonOptions);

            Console.WriteLine(json);

            if (!string.IsNullOrWhiteSpace(outputPath))
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

    private static GpuPowerLimitReadSnapshot ReadPowerLimitSnapshot(
        INvmlGpuPowerLimitReadTransport client) =>
        new(
            client.GpuPowerLimitAvailability,
            client.ReadPowerManagementModeOnce(),
            client.ReadPowerManagementLimitOnce(),
            client.ReadDefaultPowerManagementLimitOnce(),
            client.ReadPowerManagementLimitConstraintsOnce(),
            client.ReadEnforcedPowerLimitOnce());

    private static int SelfTest()
    {
        var clockAvailability =
            new NvmlGpuClockControlAvailability(
                true,
                true,
                true,
                true,
                true,
                false);

        var powerAvailability =
            new NvmlGpuPowerLimitAvailability(
                true,
                true,
                true,
                true,
                true,
                true);

        var powerSnapshot =
            new GpuPowerLimitReadSnapshot(
                powerAvailability,
                new NvmlUIntCallResult(true, 0, 1),
                new NvmlUIntCallResult(true, 0, 115000),
                new NvmlUIntCallResult(true, 0, 115000),
                new NvmlPowerLimitConstraintsCallResult(
                    true,
                    0,
                    60000,
                    115000),
                new NvmlUIntCallResult(true, 0, 110000));

        var powerQualification =
            GpuPowerLimitQualification.Assess(
                powerSnapshot);

        var powerFieldSnapshot =
            new NvmlGpuPowerFieldSnapshot(
                ExportAvailable: true,
                QueryResult: 0,
                MinLimit:
                    FieldFixture(
                        187,
                        5000),
                MaxLimit:
                    FieldFixture(
                        188,
                        75000),
                DefaultLimit:
                    FieldFixture(
                        189,
                        60000),
                CurrentLimit:
                    FieldFixture(
                        190,
                        70000),
                RequestedLimit:
                    FieldFixture(
                        192,
                        60000));

        var powerFieldQualification =
            GpuPowerFieldQualification.Assess(
                powerFieldSnapshot,
                setterExportAvailable: true);

        var report =
            new GpuObservabilityReport(
                SchemaVersion: 3,
                CapturedAtUtc: DateTimeOffset.UnixEpoch,
                TargetProfileId,
                Label: "fixture",
                DeviceName: TargetDeviceName,
                Availability: clockAvailability,
                CurrentGraphicsClock:
                    new NvmlUIntCallResult(true, 0, 210),
                ApplicationGraphicsClockTarget:
                    new NvmlUIntCallResult(true, 3, 0),
                CurrentClocksEventReasons:
                    new NvmlULongCallResult(true, 3, 0),
                AcOwnershipQualification:
                    BlockedFixtureAssessment(210, 1850),
                BatteryOwnershipQualification:
                    BlockedFixtureAssessment(210, 1200),
                PowerAvailability: powerAvailability,
                PowerManagementMode: powerSnapshot.PowerManagementMode,
                PowerManagementLimit: powerSnapshot.CurrentLimit,
                DefaultPowerManagementLimit: powerSnapshot.DefaultLimit,
                PowerManagementLimitConstraints: powerSnapshot.Constraints,
                EnforcedPowerLimit: powerSnapshot.EnforcedLimit,
                PowerLimitQualification: powerQualification,
                PowerFieldSnapshot: powerFieldSnapshot,
                PowerFieldQualification: powerFieldQualification,
                HardwareWritesPerformed: false,
                Notes: "fixture");

        var json =
            JsonSerializer.Serialize(
                report,
                JsonOptions);

        if (string.IsNullOrWhiteSpace(json) ||
            report.HardwareWritesPerformed ||
            report.AcOwnershipQualification.ManagedOwnershipAllowed ||
            report.BatteryOwnershipQualification.ManagedOwnershipAllowed ||
            !report.PowerLimitQualification.ExactConfiguredLimitReadbackAvailable ||
            !report.PowerLimitQualification.ControlledWriteQualificationRecommended ||
            report.PowerLimitQualification.ProductionWriteAuthorized ||
            !report.PowerFieldQualification.ExactUserspaceRequestedLimitReadbackAvailable ||
            !report.PowerFieldQualification.ControlledWriteQualificationRecommended ||
            report.PowerFieldQualification.ProductionWriteAuthorized)
        {
            throw new InvalidOperationException(
                "read-only probe fixture invariant failed");
        }

        Console.WriteLine(
            "GPU NVML observability probe self-test: PASS (clock + legacy power + field-value power surfaces, no NVML load, no hardware I/O).");

        return 0;
    }

    private static NvmlFieldUnsignedCallResult FieldFixture(
        uint fieldId,
        uint value) =>
        new(
            ExportAvailable: true,
            QueryResult: 0,
            FieldResult: 0,
            FieldId: fieldId,
            ValueType: 1,
            UnsignedValueDecoded: true,
            Value: value);

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
                    when index + 1 < args.Length:
                    label = args[++index];
                    break;

                case "--output"
                    when index + 1 < args.Length:
                    outputPath =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    return false;
            }
        }

        return !string.IsNullOrWhiteSpace(label);
    }

    private static void DurableWrite(
        string path,
        string content)
    {
        var directory =
            Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
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
                    encoderShouldEmitUTF8Identifier: false),
                4096,
                leaveOpen: true);

        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            "VictusFanControl.GpuProbe --observe [--label <name>] [--output <json-path>]");

        Console.WriteLine(
            "VictusFanControl.GpuProbe --self-test");

        Console.WriteLine(
            "VictusFanControl.GpuProbe --clock-write-self-test");

        Console.WriteLine(
            "VictusFanControl.GpuProbe --clock-write-test --confirm-target HP-8C40-9D0R1LA-F18 --preset <ac|battery> [--hold-seconds 1..30]");

        Console.WriteLine(
            "VictusFanControl.GpuProbe --clock-transition-self-test");

        Console.WriteLine(
            "VictusFanControl.GpuProbe --clock-transition-test --confirm-target HP-8C40-9D0R1LA-F18 [--hold-seconds 1..30]");

        Console.WriteLine(
            "--observe is read-only. --clock-write-test and --clock-transition-test are explicit qualification-only NVML write paths.");
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
        GpuClockOwnershipQualificationResult AcOwnershipQualification,
        GpuClockOwnershipQualificationResult BatteryOwnershipQualification,
        NvmlGpuPowerLimitAvailability PowerAvailability,
        NvmlUIntCallResult PowerManagementMode,
        NvmlUIntCallResult PowerManagementLimit,
        NvmlUIntCallResult DefaultPowerManagementLimit,
        NvmlPowerLimitConstraintsCallResult PowerManagementLimitConstraints,
        NvmlUIntCallResult EnforcedPowerLimit,
        GpuPowerLimitQualificationResult PowerLimitQualification,
        NvmlGpuPowerFieldSnapshot PowerFieldSnapshot,
        GpuPowerFieldQualificationResult PowerFieldQualification,
        bool HardwareWritesPerformed,
        string Notes);
}
