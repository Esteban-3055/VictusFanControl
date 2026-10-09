using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Performance;

namespace VictusFanControl.GpuProbe;

internal static class GpuClockWriteQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const string TargetDeviceName =
        "NVIDIA GeForce RTX 4060 Laptop GPU";

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

        var request =
            options.Preset ==
                "ac"
                ? new GpuClockLimitRequest(
                    210,
                    1850)
                : new GpuClockLimitRequest(
                    210,
                    1200);

        var outputPath =
            options.OutputPath ??
            DefaultOutputPath(
                options.Preset);

        var journalPath =
            Path.Combine(
                Path.GetDirectoryName(
                    outputPath) ??
                Environment.CurrentDirectory,
                "gpu-clock-nvml-write-session.json");

        if (File.Exists(journalPath))
        {
            Console.Error.WriteLine(
                "A previous GPU clock qualification journal already exists. " +
                "No write will be attempted. Review/recover it first: " +
                journalPath);

            return 4;
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(
                outputPath) ??
            Environment.CurrentDirectory);

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
                "GPU clock qualification session is not Disabled: " +
                session.State);

            return 4;
        }

        var startedAt =
            DateTimeOffset.UtcNow;

        var before =
            backend.ReadObservation();

        var applySucceeded =
            false;

        var releaseSucceeded =
            false;

        var releaseAttempted =
            false;

        GpuClockBackendObservation?
            during = null;

        GpuClockBackendObservation?
            after = null;

        string? exceptionText =
            null;

        try
        {
            applySucceeded =
                session.Apply(
                    request);

            if (!applySucceeded)
            {
                WriteReport(
                    outputPath,
                    new QualificationReport(
                        SchemaVersion: 1,
                        TargetProfileId,
                        TargetDeviceName,
                        options.Preset,
                        request,
                        options.HoldSeconds,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        before,
                        during,
                        after,
                        ApplySucceeded: false,
                        ReleaseAttempted: false,
                        ReleaseSucceeded: false,
                        FinalSessionState:
                            session.State,
                        SessionStatus:
                            session.LastStatus,
                        ExactLockedRangeVerified:
                            false,
                        HardwareWritesAuthorizedOnlyForThisQualification:
                            true,
                        ProductionHardwareWritesAuthorized:
                            false,
                        Exception: null));

                return 5;
            }

            Thread.Sleep(
                TimeSpan.FromSeconds(
                    options.HoldSeconds));

            during =
                backend.ReadObservation();

            releaseAttempted =
                true;

            releaseSucceeded =
                session.Release();

            if (releaseSucceeded)
            {
                after =
                    backend.ReadObservation();
            }
        }
        catch (Exception ex)
        {
            exceptionText =
                ex.ToString();
        }
        finally
        {
            if (!releaseAttempted &&
                session.State ==
                    GpuClockSessionState.ActiveUnverified)
            {
                releaseAttempted =
                    true;

                releaseSucceeded =
                    session.Release();
            }
        }

        WriteReport(
            outputPath,
            new QualificationReport(
                SchemaVersion: 1,
                TargetProfileId,
                TargetDeviceName,
                options.Preset,
                request,
                options.HoldSeconds,
                startedAt,
                DateTimeOffset.UtcNow,
                before,
                during,
                after,
                ApplySucceeded:
                    applySucceeded,
                ReleaseAttempted:
                    releaseAttempted,
                ReleaseSucceeded:
                    releaseSucceeded,
                FinalSessionState:
                    session.State,
                SessionStatus:
                    session.LastStatus,
                ExactLockedRangeVerified:
                    false,
                HardwareWritesAuthorizedOnlyForThisQualification:
                    true,
                ProductionHardwareWritesAuthorized:
                    false,
                Exception:
                    exceptionText));

        Console.WriteLine(
            "GPU clock qualification report: " +
            outputPath);

        if (!releaseSucceeded)
        {
            Console.Error.WriteLine(
                "The qualification did not confirm a normal Reset. " +
                "The durable session journal was intentionally retained; " +
                "do not run another write test until it is reviewed.");

            return 6;
        }

        if (exceptionText is not null)
        {
            Console.Error.WriteLine(
                exceptionText);

            return 7;
        }

        Console.WriteLine(
            $"GPU NVML clock write qualification completed for {options.Preset}: " +
            $"Set {request.MinGraphicsClockMHz}..{request.MaxGraphicsClockMHz} MHz accepted, " +
            "then one normal Reset accepted.");

        Console.WriteLine(
            "Exact min/max readback is unavailable; this qualifies the direct NVML command path, not exact ownership.");

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
                        "--clock-write-test",
                        "--confirm-target",
                        TargetProfileId,
                        "--preset",
                        "ac",
                        "--hold-seconds",
                        "5"
                    },
                    out var ac,
                    out _),
                "AC qualification arguments parse");

            Require(
                ac.Preset == "ac" &&
                ac.HoldSeconds == 5,
                "AC qualification parse values");

            Require(
                TryParse(
                    new[]
                    {
                        "--clock-write-test",
                        "--confirm-target",
                        TargetProfileId,
                        "--preset",
                        "battery",
                        "--hold-seconds",
                        "1"
                    },
                    out var battery,
                    out _),
                "Battery qualification arguments parse");

            Require(
                battery.Preset == "battery",
                "Battery qualification preset");

            Require(
                !TryParse(
                    new[]
                    {
                        "--clock-write-test",
                        "--confirm-target",
                        "WRONG-TARGET",
                        "--preset",
                        "ac"
                    },
                    out _,
                    out _),
                "wrong target token rejected before NVML load");

            Require(
                !TryParse(
                    new[]
                    {
                        "--clock-write-test",
                        "--confirm-target",
                        TargetProfileId,
                        "--preset",
                        "custom"
                    },
                    out _,
                    out _),
                "arbitrary clock preset rejected");

            Require(
                !TryParse(
                    new[]
                    {
                        "--clock-write-test",
                        "--confirm-target",
                        TargetProfileId,
                        "--preset",
                        "battery",
                        "--hold-seconds",
                        "31"
                    },
                    out _,
                    out _),
                "excessive hold rejected");

            output.WriteLine(
                "GPU clock NVML write qualification harness self-test: PASS (argument/token gates only, no NVML load, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU clock NVML write qualification harness self-test: FAIL - " +
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
                Preset: string.Empty,
                HoldSeconds: 5,
                OutputPath: null);

        error =
            "Invalid GPU clock qualification arguments.";

        if (args.Length < 1 ||
            args[0] !=
                "--clock-write-test")
        {
            return false;
        }

        string? confirm =
            null;

        string? preset =
            null;

        var holdSeconds =
            5;

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

                case "--preset"
                    when index + 1 <
                         args.Length:
                    preset =
                        args[++index]
                            .Trim()
                            .ToLowerInvariant();
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
                        "Unknown or incomplete GPU clock qualification argument: " +
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

        if (preset is not
                "ac" and not
                "battery")
        {
            error =
                "Only the qualified presets 'ac' and 'battery' are accepted.";

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
                preset,
                holdSeconds,
                outputPath);

        return true;
    }

    private static string DefaultOutputPath(
        string preset)
    {
        var root =
            Path.Combine(
                Environment.CurrentDirectory,
                "logs");

        Directory.CreateDirectory(
            root);

        return Path.Combine(
            root,
            "gpu-clock-nvml-write_" +
            DateTime.Now.ToString(
                "yyyy-MM-dd_HHmmss") +
            "_" +
            preset +
            ".json");
    }

    private static void WriteReport(
        string path,
        QualificationReport report)
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
            "VictusFanControl.GpuProbe --clock-write-test --confirm-target HP-8C40-9D0R1LA-F18 --preset <ac|battery> [--hold-seconds 1..30] [--output <json-path>]");
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
        string Preset,
        int HoldSeconds,
        string? OutputPath);

    private readonly record struct QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        string DeviceName,
        string Preset,
        GpuClockLimitRequest Request,
        int HoldSeconds,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        GpuClockBackendObservation Before,
        GpuClockBackendObservation? During,
        GpuClockBackendObservation? After,
        bool ApplySucceeded,
        bool ReleaseAttempted,
        bool ReleaseSucceeded,
        GpuClockSessionState FinalSessionState,
        string? SessionStatus,
        bool ExactLockedRangeVerified,
        bool HardwareWritesAuthorizedOnlyForThisQualification,
        bool ProductionHardwareWritesAuthorized,
        string? Exception);
}
