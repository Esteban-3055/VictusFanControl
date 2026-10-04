using System.Text;
using System.Text.Json;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceProbe;

internal static class PowerSourceCoordinatorQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        string[] args)
    {
        if (!TryParse(
                args,
                out var options,
                out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        var reader =
            new WindowsPerformancePowerSourceReader();

        var cpu =
            new RecordingCpuSink();

        var gpu =
            new RecordingGpuSink();

        var coordinator =
            new PerformanceSourceTransitionCoordinator(
                reader,
                cpu,
                gpu);

        var prime =
            coordinator.Prime();

        if (!prime.Observation.Succeeded)
        {
            Console.Error.WriteLine(
                "Initial coordinator prime query failed: " +
                prime.Observation.Status);

            return 3;
        }

        if (prime.Observation.Source ==
            options.ExpectedSource)
        {
            Console.Error.WriteLine(
                "Initial source already equals expected destination. " +
                "No coordinator transition qualification was attempted.");

            return 4;
        }

        var events =
            new List<PerformanceSourceDispatchResult>();

        var startedAt =
            DateTimeOffset.UtcNow;

        var matched =
            false;

        string? listenerError =
            null;

        using var context =
            new ApplicationContext();

        using var timeout =
            new System.Windows.Forms.Timer
            {
                Interval =
                    checked(
                        options.TimeoutSeconds *
                        1000)
            };

        timeout.Tick +=
            (_, _) =>
            {
                timeout.Stop();
                context.ExitThread();
            };

        try
        {
            using var window =
                new PowerSourceNotificationQualification
                    .PowerNotificationWindow(
                        () =>
                        {
                            var dispatch =
                                coordinator.HandleNotificationSignal();

                            events.Add(
                                dispatch);

                            Console.WriteLine(
                                $"Coordinator signal -> source={dispatch.Observation.Source}, " +
                                $"raw={dispatch.Observation.RawAcLineStatus?.ToString() ?? "null"}, " +
                                $"duplicate={dispatch.DuplicateSuppressed}, " +
                                $"cpuAttempted={dispatch.CpuAttempted}, " +
                                $"gpuAttempted={dispatch.GpuAttempted}, " +
                                $"status={dispatch.Status}");

                            if (dispatch.Observation.Succeeded &&
                                dispatch.Observation.Source ==
                                    options.ExpectedSource &&
                                !dispatch.DuplicateSuppressed &&
                                dispatch.CpuAttempted &&
                                dispatch.GpuAttempted)
                            {
                                matched = true;
                                context.ExitThread();
                            }
                        });

            timeout.Start();

            Console.WriteLine(
                $"Coordinator primed source: {prime.Observation.Source} " +
                $"raw={prime.Observation.RawAcLineStatus?.ToString() ?? "null"}.");

            Console.WriteLine(
                $"Waiting up to {options.TimeoutSeconds}s for a real source change to {options.ExpectedSource}.");

            Application.Run(
                context);
        }
        catch (Exception ex)
        {
            listenerError =
                ex.ToString();
        }
        finally
        {
            timeout.Stop();
        }

        var report =
            new QualificationReport(
                SchemaVersion: 1,
                TargetProfileId,
                ExpectedSource:
                    options.ExpectedSource,
                TimeoutSeconds:
                    options.TimeoutSeconds,
                StartedAtUtc:
                    startedAt,
                CompletedAtUtc:
                    DateTimeOffset.UtcNow,
                Prime:
                    prime,
                NotificationDispatches:
                    events.ToArray(),
                CpuDispatchCount:
                    cpu.Calls,
                CpuLastSource:
                    cpu.LastSource,
                GpuDispatchCount:
                    gpu.Calls,
                GpuLastSource:
                    gpu.LastSource,
                ExpectedSourceDispatchedToBothDomains:
                    matched &&
                    cpu.Calls == 1 &&
                    gpu.Calls == 1 &&
                    cpu.LastSource ==
                        options.ExpectedSource &&
                    gpu.LastSource ==
                        options.ExpectedSource,
                ListenerError:
                    listenerError,
                HardwareWritesPerformed:
                    false);

        DurableJson(
            options.OutputPath,
            report);

        Console.WriteLine(
            "Performance source coordinator report: " +
            options.OutputPath);

        if (listenerError is not null)
        {
            Console.Error.WriteLine(
                listenerError);

            return 5;
        }

        if (!report.ExpectedSourceDispatchedToBothDomains)
        {
            Console.Error.WriteLine(
                "Expected source was not dispatched exactly once to both recording domains.");

            return 6;
        }

        Console.WriteLine(
            "Performance source coordinator qualification: PASS. " +
            "Direct query confirmed the changed source and one independent dispatch reached each recording domain.");

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
                        "--watch-coordinator",
                        "--expect",
                        "battery",
                        "--timeout-seconds",
                        "30",
                        "--output",
                        Path.Combine(
                            Path.GetTempPath(),
                            "vfc-source-coordinator.json")
                    },
                    out var battery,
                    out _),
                "coordinator Battery args parse");

            Require(
                battery.ExpectedSource ==
                    PerformancePowerSourceKind.Battery &&
                battery.TimeoutSeconds == 30,
                "coordinator Battery parse values");

            Require(
                TryParse(
                    new[]
                    {
                        "--watch-coordinator",
                        "--expect",
                        "ac",
                        "--timeout-seconds",
                        "5",
                        "--output",
                        Path.Combine(
                            Path.GetTempPath(),
                            "vfc-source-coordinator-ac.json")
                    },
                    out var ac,
                    out _),
                "coordinator AC args parse");

            Require(
                ac.ExpectedSource ==
                    PerformancePowerSourceKind.Ac,
                "coordinator AC parse value");

            Require(
                !TryParse(
                    new[]
                    {
                        "--watch-coordinator",
                        "--expect",
                        "unknown",
                        "--output",
                        "x.json"
                    },
                    out _,
                    out _),
                "coordinator Unknown expected source rejected");

            output.WriteLine(
                "Performance source coordinator qualification harness self-test: PASS (argument gates only, no listener registration, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance source coordinator qualification harness self-test: FAIL - " +
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
            default;

        error =
            "Invalid performance source coordinator qualification arguments.";

        if (args.Length < 1 ||
            args[0] !=
                "--watch-coordinator")
        {
            return false;
        }

        PerformancePowerSourceKind?
            expected = null;

        var timeoutSeconds =
            60;

        string? outputPath =
            null;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--expect"
                    when index + 1 <
                         args.Length:
                {
                    var value =
                        args[++index]
                            .Trim()
                            .ToLowerInvariant();

                    expected =
                        value switch
                        {
                            "ac" =>
                                PerformancePowerSourceKind.Ac,

                            "battery" =>
                                PerformancePowerSourceKind.Battery,

                            _ => null
                        };

                    if (!expected.HasValue)
                    {
                        error =
                            "Expected destination must be 'ac' or 'battery'.";

                        return false;
                    }

                    break;
                }

                case "--timeout-seconds"
                    when index + 1 <
                         args.Length &&
                         int.TryParse(
                             args[index + 1],
                             out var parsed):
                    index++;
                    timeoutSeconds =
                        parsed;
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
                        "Unknown or incomplete coordinator qualification argument: " +
                        args[index];

                    return false;
            }
        }

        if (!expected.HasValue)
        {
            error =
                "--expect ac|battery is required.";

            return false;
        }

        if (timeoutSeconds is < 5 or > 120)
        {
            error =
                "TimeoutSeconds must be between 5 and 120.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                outputPath))
        {
            error =
                "--output is required.";

            return false;
        }

        options =
            new Options(
                expected.Value,
                timeoutSeconds,
                outputPath!);

        return true;
    }

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

        var json =
            JsonSerializer.Serialize(
                value,
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

        writer.Write(json);
        writer.Flush();
        stream.Flush(
            flushToDisk: true);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "VictusFanControl.PerformanceProbe --watch-coordinator --expect <ac|battery> --timeout-seconds <5..120> --output <json-path>");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private readonly record struct Options(
        PerformancePowerSourceKind ExpectedSource,
        int TimeoutSeconds,
        string OutputPath);

    private readonly record struct QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        PerformancePowerSourceKind ExpectedSource,
        int TimeoutSeconds,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        PerformanceSourceDispatchResult Prime,
        PerformanceSourceDispatchResult[] NotificationDispatches,
        int CpuDispatchCount,
        PerformancePowerSourceKind? CpuLastSource,
        int GpuDispatchCount,
        PerformancePowerSourceKind? GpuLastSource,
        bool ExpectedSourceDispatchedToBothDomains,
        string? ListenerError,
        bool HardwareWritesPerformed);

    private sealed class RecordingCpuSink :
        ICpuPowerSourceTransitionSink
    {
        internal int Calls;
        internal PerformancePowerSourceKind? LastSource;

        public CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
            PerformancePowerSourceKind source)
        {
            Calls++;
            LastSource =
                source;

            return new CpuPowerPresetTransitionResult(
                CpuPowerPresetTransitionDisposition.EnabledPresetSwitched,
                source,
                source ==
                    PerformancePowerSourceKind.Ac
                    ? PerformancePresetSlot.Ac
                    : source ==
                        PerformancePowerSourceKind.Battery
                        ? PerformancePresetSlot.Battery
                        : null,
                Succeeded: true,
                Status:
                    "RECORDING_CPU_DOMAIN_NO_HARDWARE_IO");
        }
    }

    private sealed class RecordingGpuSink :
        IGpuClockSourceTransitionSink
    {
        internal int Calls;
        internal PerformancePowerSourceKind? LastSource;

        public GpuClockPresetTransitionResult HandleConfirmedSourceChange(
            PerformancePowerSourceKind source)
        {
            Calls++;
            LastSource =
                source;

            return new GpuClockPresetTransitionResult(
                GpuClockPresetTransitionDisposition.EnabledPresetSwitched,
                source,
                source ==
                    PerformancePowerSourceKind.Ac
                    ? PerformancePresetSlot.Ac
                    : source ==
                        PerformancePowerSourceKind.Battery
                        ? PerformancePresetSlot.Battery
                        : null,
                Succeeded: true,
                SessionState:
                    GpuClockSessionState.ActiveUnverified,
                Status:
                    "RECORDING_GPU_DOMAIN_NO_HARDWARE_IO");
        }
    }
}
