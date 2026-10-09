using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Performance;

namespace VictusFanControl.GpuProbe;

internal static class GpuClockTransitionQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const string TargetDeviceName =
        "NVIDIA GeForce RTX 4060 Laptop GPU";

    private static readonly GpuClockLimitRequest AcRequest =
        new(
            210,
            1850);

    private static readonly GpuClockLimitRequest BatteryRequest =
        new(
            210,
            1200);

    internal static int Run(
        string[] args)
    {
        if (!TryParse(
                args,
                out var options,
                out var parseError))
        {
            Console.Error.WriteLine(
                parseError);

            PrintUsage();
            return 2;
        }

        var outputPath =
            options.OutputPath ??
            DefaultOutputPath();

        var directory =
            Path.GetDirectoryName(
                outputPath) ??
            Environment.CurrentDirectory;

        Directory.CreateDirectory(
            directory);

        var journalPath =
            Path.Combine(
                directory,
                "gpu-clock-nvml-write-session.json");

        if (File.Exists(journalPath))
        {
            Console.Error.WriteLine(
                "A previous GPU clock qualification journal already exists. " +
                "No write will be attempted. Review/recover it first: " +
                journalPath);

            return 4;
        }

        using var client =
            new NvmlClient(
                TargetDeviceName,
                requirePreferredDevice: true);

        var backend =
            new NvmlGpuClockLimitBackend(
                client,
                hardwareWritesAuthorized: true);

        var journal =
            new JsonGpuClockSessionJournal(
                journalPath,
                TargetProfileId);

        using var session =
            new GpuClockSessionController(
                backend,
                journal);

        if (session.State !=
            GpuClockSessionState.Disabled)
        {
            Console.Error.WriteLine(
                "GPU clock transition qualification session is not Disabled: " +
                session.State);

            return 4;
        }

        var transitions =
            new GpuClockPresetTransitionController(
                new GpuClockPresetPolicy(
                    GpuClockPresetSet.UserRequestedVictus),
                session);

        var startedAt =
            DateTimeOffset.UtcNow;

        var before =
            backend.ReadObservation();

        GpuClockBackendObservation? afterAcApply =
            null;

        GpuClockBackendObservation? afterBatterySwitch =
            null;

        GpuClockBackendObservation? afterAcReturn =
            null;

        GpuClockBackendObservation? afterRelease =
            null;

        GpuClockPresetTransitionResult? batteryTransition =
            null;

        GpuClockPresetTransitionResult? acReturnTransition =
            null;

        var applyAcSucceeded =
            false;

        var releaseAttempted =
            false;

        var releaseSucceeded =
            false;

        var safetyCleanupAttempted =
            false;

        var safetyCleanupSucceeded =
            false;

        string? exceptionText =
            null;

        var exitCode =
            0;

        try
        {
            applyAcSucceeded =
                session.Apply(
                    AcRequest);

            if (!applyAcSucceeded)
            {
                exitCode = 5;
            }
            else
            {
                Thread.Sleep(
                    TimeSpan.FromSeconds(
                        options.HoldSeconds));

                afterAcApply =
                    backend.ReadObservation();

                batteryTransition =
                    transitions.HandleConfirmedSourceChange(
                        PerformancePowerSourceKind.Battery);

                if (!batteryTransition.Value.Succeeded ||
                    batteryTransition.Value.Disposition !=
                        GpuClockPresetTransitionDisposition.EnabledPresetSwitched)
                {
                    exitCode = 6;
                }
                else
                {
                    Thread.Sleep(
                        TimeSpan.FromSeconds(
                            options.HoldSeconds));

                    afterBatterySwitch =
                        backend.ReadObservation();

                    acReturnTransition =
                        transitions.HandleConfirmedSourceChange(
                            PerformancePowerSourceKind.Ac);

                    if (!acReturnTransition.Value.Succeeded ||
                        acReturnTransition.Value.Disposition !=
                            GpuClockPresetTransitionDisposition.EnabledPresetSwitched)
                    {
                        exitCode = 7;
                    }
                    else
                    {
                        Thread.Sleep(
                            TimeSpan.FromSeconds(
                                options.HoldSeconds));

                        afterAcReturn =
                            backend.ReadObservation();

                        releaseAttempted =
                            true;

                        releaseSucceeded =
                            session.Release();

                        if (!releaseSucceeded)
                        {
                            exitCode = 8;
                        }
                        else
                        {
                            afterRelease =
                                backend.ReadObservation();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            exceptionText =
                ex.ToString();

            exitCode = 9;
        }
        finally
        {
            if (!releaseSucceeded &&
                session.State ==
                    GpuClockSessionState.ActiveUnverified)
            {
                safetyCleanupAttempted =
                    true;

                releaseAttempted =
                    true;

                safetyCleanupSucceeded =
                    session.Release();

                releaseSucceeded =
                    safetyCleanupSucceeded;

                if (releaseSucceeded)
                {
                    afterRelease =
                        backend.ReadObservation();
                }
            }
        }

        WriteReport(
            outputPath,
            new QualificationReport(
                SchemaVersion: 1,
                TargetProfileId,
                TargetDeviceName,
                Sequence:
                    "AC_210_1850__BATTERY_210_1200__AC_210_1850__RESET",
                options.HoldSeconds,
                startedAt,
                DateTimeOffset.UtcNow,
                before,
                afterAcApply,
                batteryTransition,
                afterBatterySwitch,
                acReturnTransition,
                afterAcReturn,
                afterRelease,
                ApplyAcSucceeded:
                    applyAcSucceeded,
                ReleaseAttempted:
                    releaseAttempted,
                ReleaseSucceeded:
                    releaseSucceeded,
                SafetyCleanupAttempted:
                    safetyCleanupAttempted,
                SafetyCleanupSucceeded:
                    safetyCleanupSucceeded,
                FinalSessionState:
                    session.State,
                SessionStatus:
                    session.LastStatus,
                ExactLockedRangeVerified:
                    false,
                NoIntermediateResetExpected:
                    true,
                HardwareWritesAuthorizedOnlyForThisQualification:
                    true,
                ProductionHardwareWritesAuthorized:
                    false,
                ExitCode:
                    exitCode,
                Exception:
                    exceptionText));

        Console.WriteLine(
            "GPU clock transition qualification report: " +
            outputPath);

        if (exitCode != 0)
        {
            if (session.State ==
                GpuClockSessionState.RecoveryRequired)
            {
                Console.Error.WriteLine(
                    "Transition qualification entered RecoveryRequired. " +
                    "The durable journal was retained and no blind Reset was attempted. " +
                    "Do not run another write test until it is reviewed.");
            }
            else if (safetyCleanupSucceeded)
            {
                Console.Error.WriteLine(
                    "Transition qualification failed before the planned end, " +
                    "but the original live session completed a normal journaled Reset.");
            }

            return exitCode;
        }

        Console.WriteLine(
            "GPU NVML transition qualification completed: " +
            "AC 210..1850 -> Battery 210..1200 -> AC 210..1850 -> one final Reset.");

        Console.WriteLine(
            "No intermediate Reset is issued between enabled presets. " +
            "Exact locked min/max remains unobservable, so ActiveUnverified semantics are retained.");

        return 0;
    }

    internal static int SelfTest(
        TextWriter output)
    {
        try
        {
            Require(
                TryParse(
                    new[]
                    {
                        "--clock-transition-test",
                        "--confirm-target",
                        TargetProfileId,
                        "--hold-seconds",
                        "3"
                    },
                    out var valid,
                    out _),
                "transition qualification arguments parse");

            Require(
                valid.HoldSeconds == 3,
                "transition hold parse");

            Require(
                !TryParse(
                    new[]
                    {
                        "--clock-transition-test",
                        "--confirm-target",
                        "WRONG-TARGET"
                    },
                    out _,
                    out _),
                "wrong transition target rejected");

            Require(
                !TryParse(
                    new[]
                    {
                        "--clock-transition-test",
                        "--confirm-target",
                        TargetProfileId,
                        "--hold-seconds",
                        "0"
                    },
                    out _,
                    out _),
                "zero hold rejected");

            Require(
                !TryParse(
                    new[]
                    {
                        "--clock-transition-test",
                        "--confirm-target",
                        TargetProfileId,
                        "--hold-seconds",
                        "31"
                    },
                    out _,
                    out _),
                "excessive transition hold rejected");

            Require(
                AcRequest ==
                    new GpuClockLimitRequest(
                        210,
                        1850) &&
                BatteryRequest ==
                    new GpuClockLimitRequest(
                        210,
                        1200),
                "transition harness uses only fixed product presets");

            output.WriteLine(
                "GPU clock NVML transition qualification harness self-test: PASS (fixed AC->Battery->AC sequence, target/token gates only, no NVML load, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU clock NVML transition qualification harness self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static bool TryParse(
        string[] args,
        out Options options,
        out string error)
    {
        options =
            new Options(
                HoldSeconds: 3,
                OutputPath: null);

        error =
            "Invalid GPU clock transition qualification arguments.";

        if (args.Length < 1 ||
            args[0] !=
                "--clock-transition-test")
        {
            return false;
        }

        string? confirm =
            null;

        var holdSeconds =
            3;

        string? outputPath =
            null;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--confirm-target"
                    when index + 1 <
                         args.Length:
                    confirm =
                        args[++index];
                    break;

                case "--hold-seconds"
                    when index + 1 <
                         args.Length &&
                         int.TryParse(
                             args[index + 1],
                             out var parsedHold):
                    index++;
                    holdSeconds =
                        parsedHold;
                    break;

                case "--output"
                    when index + 1 <
                         args.Length:
                    outputPath =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    error =
                        "Unknown or incomplete GPU transition qualification argument: " +
                        args[index];

                    return false;
            }
        }

        if (!string.Equals(
                confirm,
                TargetProfileId,
                StringComparison.Ordinal))
        {
            error =
                "Exact target confirmation token is required: " +
                TargetProfileId;

            return false;
        }

        if (holdSeconds is < 1 or > 30)
        {
            error =
                "HoldSeconds must be between 1 and 30.";

            return false;
        }

        options =
            new Options(
                holdSeconds,
                outputPath);

        return true;
    }

    private static string DefaultOutputPath()
    {
        var root =
            Path.Combine(
                Environment.CurrentDirectory,
                "logs");

        Directory.CreateDirectory(
            root);

        return Path.Combine(
            root,
            "gpu-clock-nvml-transition_" +
            DateTime.Now.ToString(
                "yyyy-MM-dd_HHmmss") +
            "_ac-battery-ac.json");
    }

    private static void WriteReport(
        string path,
        QualificationReport report)
    {
        var json =
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

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
            json);

        writer.Flush();

        stream.Flush(
            flushToDisk: true);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "VictusFanControl.GpuProbe --clock-transition-test --confirm-target HP-8C40-9D0R1LA-F18 [--hold-seconds 1..30] [--output <json-path>]");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(
                label);
    }

    private readonly record struct Options(
        int HoldSeconds,
        string? OutputPath);

    private readonly record struct QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        string DeviceName,
        string Sequence,
        int HoldSeconds,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        GpuClockBackendObservation Before,
        GpuClockBackendObservation? AfterAcApply,
        GpuClockPresetTransitionResult? BatteryTransition,
        GpuClockBackendObservation? AfterBatterySwitch,
        GpuClockPresetTransitionResult? AcReturnTransition,
        GpuClockBackendObservation? AfterAcReturn,
        GpuClockBackendObservation? AfterRelease,
        bool ApplyAcSucceeded,
        bool ReleaseAttempted,
        bool ReleaseSucceeded,
        bool SafetyCleanupAttempted,
        bool SafetyCleanupSucceeded,
        GpuClockSessionState FinalSessionState,
        string? SessionStatus,
        bool ExactLockedRangeVerified,
        bool NoIntermediateResetExpected,
        bool HardwareWritesAuthorizedOnlyForThisQualification,
        bool ProductionHardwareWritesAuthorized,
        int ExitCode,
        string? Exception);
}
