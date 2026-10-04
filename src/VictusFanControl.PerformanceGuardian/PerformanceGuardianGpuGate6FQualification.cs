using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>
/// Step 6F first physical gate: one real GPU domain only.
///
/// This is not a production run mode. The outer qualification process must
/// explicitly confirm the exact target. The detached Guardian then receives
/// only semantic HELLO / ENABLE_SESSION / DISABLE_SESSION / SHUTDOWN commands.
///
/// Expected physical sequence:
///   initial AC -> Apply 210..1850
///   unplug -> Battery -> Set 210..1200
///   plug -> AC -> Set 210..1850
///   DISABLE_SESSION -> one final Reset
///
/// Every Set/Reset is issued by GpuClockSessionController after its durable
/// journal phase. CPU remains disabled and no RAPL writer is constructed.
/// </summary>
internal static class PerformanceGuardianGpuGate6FQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const string ExpectedGpuName =
        "NVIDIA GeForce RTX 4060 Laptop GPU";

    private static string Executable =>
        Path.Combine(
            AppContext.BaseDirectory,
            "VictusFanControl.PerformanceGuardian.exe");

    internal static async Task<int> RunOuterAsync(
        string[] args)
    {
        if (!TryParseOuter(
                args,
                out var options,
                out var parseError))
        {
            Console.Error.WriteLine(
                parseError);

            PrintUsage();
            return 2;
        }

        try
        {
            RequireExactTargetAndElevation();

            var initialReader =
                new WindowsPerformancePowerSourceReader();

            var initial =
                initialReader.Read();

            if (!initial.Succeeded ||
                initial.Source !=
                    PerformancePowerSourceKind.Ac)
            {
                throw new InvalidOperationException(
                    "Step 6F GPU qualification must start with AC connected and directly confirmed by GetSystemPowerStatus.");
            }

            var root =
                options.OutputDirectory ??
                Path.Combine(
                    Environment.CurrentDirectory,
                    "logs",
                    "performance-guardian-6f-gpu_" +
                    DateTime.Now.ToString(
                        "yyyy-MM-dd_HHmmss"));

            root =
                Path.GetFullPath(
                    root);

            Directory.CreateDirectory(
                root);

            var guardianReportPath =
                Path.Combine(
                    root,
                    "guardian-report.json");

            var qualificationReportPath =
                Path.Combine(
                    root,
                    "qualification.json");

            var readyPath =
                Path.Combine(
                    root,
                    "guardian-ready.txt");

            var journalPath =
                Path.Combine(
                    root,
                    "gpu-clock-session.json");

            if (File.Exists(
                    journalPath))
            {
                throw new InvalidOperationException(
                    "Step 6F GPU journal already exists; no write will be attempted until it is reviewed: " +
                    journalPath);
            }

            using var owner =
                Process.GetCurrentProcess();

            var ownerStartTicks =
                owner.StartTime
                    .ToUniversalTime()
                    .Ticks;

            var nonce =
                Guid.NewGuid();

            var pipeName =
                "VictusFanControl.PerformanceGuardian.6F.Gpu." +
                Guid.NewGuid().ToString("N");

            var mutexName =
                PerformanceGuardianHost.ProductionMutexName(
                    TargetProfileId);

            var startedAt =
                DateTimeOffset.UtcNow;

            using var guardian =
                Process.Start(
                    NewGuardianStart(
                        pipeName,
                        mutexName,
                        nonce,
                        owner.Id,
                        ownerStartTicks,
                        guardianReportPath,
                        readyPath,
                        journalPath)) ??
                throw new IOException(
                    "Step 6F detached GPU Guardian did not start.");

            PerformanceGuardianResponse? enableResponse =
                null;

            PerformanceGuardianResponse? disableResponse =
                null;

            PerformanceGuardianResponse? shutdownResponse =
                null;

            PerformancePowerSourceObservation? batterySeen =
                null;

            PerformancePowerSourceObservation? acReturnSeen =
                null;

            Exception? failure =
                null;

            try
            {
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(
                            options.TimeoutSeconds * 3 +
                            30));

                await WaitForFileAsync(
                    readyPath,
                    timeout.Token).ConfigureAwait(false);

                using var pipe =
                    await ConnectAsync(
                        pipeName,
                        timeout.Token).ConfigureAwait(false);

                var hello =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.Hello,
                            owner.Id,
                            ownerStartTicks),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    hello.Ok &&
                    string.Equals(
                        hello.Code,
                        "HELLO_OK",
                        StringComparison.Ordinal),
                    "Step 6F HELLO was not accepted.");

                enableResponse =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.EnableSession,
                            cpuEnabled: false,
                            gpuEnabled: true),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    enableResponse.Ok &&
                    enableResponse.SessionEnabled &&
                    !enableResponse.CpuEnabled &&
                    enableResponse.GpuEnabled,
                    "Step 6F GPU-only ENABLE_SESSION failed.");

                Console.WriteLine();
                Console.WriteLine(
                    "Step 6F GPU: AC preset 210..1850 MHz has been journaled/applied by the detached Guardian.");
                Console.WriteLine(
                    $"Disconnect the charger now. Waiting up to {options.TimeoutSeconds} seconds for Battery confirmation...");

                batterySeen =
                    await WaitForSourceAsync(
                        PerformancePowerSourceKind.Battery,
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                Console.WriteLine(
                    "Battery confirmed by direct GetSystemPowerStatus. Guardian should now have selected 210..1200 MHz.");
                Console.WriteLine(
                    $"Reconnect the charger now. Waiting up to {options.TimeoutSeconds} seconds for AC confirmation...");

                acReturnSeen =
                    await WaitForSourceAsync(
                        PerformancePowerSourceKind.Ac,
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                // Give the Guardian message loop a bounded moment to process
                // the same source transition observed by the qualification
                // process before issuing cleanup.
                await Task.Delay(
                    1000,
                    timeout.Token).ConfigureAwait(false);

                disableResponse =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.DisableSession),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    disableResponse.Ok &&
                    !disableResponse.SessionEnabled,
                    "Step 6F DISABLE_SESSION did not complete normal GPU release.");

                shutdownResponse =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.Shutdown),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    shutdownResponse.Ok,
                    "Step 6F SHUTDOWN was not accepted.");

                await guardian.WaitForExitAsync(
                    timeout.Token).ConfigureAwait(false);

                await WaitForFileAsync(
                    guardianReportPath,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure =
                    ex;

                // Best-effort controlled cleanup while the owner is still
                // alive. If this path cannot reach the Guardian, owner death at
                // process exit remains the final release trigger.
                try
                {
                    using var cleanupTimeout =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(8));

                    using var cleanupPipe =
                        await ConnectAsync(
                            pipeName,
                            cleanupTimeout.Token).ConfigureAwait(false);

                    var hello =
                        await RoundTripAsync(
                            cleanupPipe,
                            NewRequest(
                                nonce,
                                PerformanceGuardianProtocol.Hello,
                                owner.Id,
                                ownerStartTicks),
                            cleanupTimeout.Token).ConfigureAwait(false);

                    if (hello.Ok &&
                        hello.SessionEnabled)
                    {
                        _ =
                            await RoundTripAsync(
                                cleanupPipe,
                                NewRequest(
                                    nonce,
                                    PerformanceGuardianProtocol.DisableSession),
                                cleanupTimeout.Token).ConfigureAwait(false);
                    }

                    _ =
                        await RoundTripAsync(
                            cleanupPipe,
                            NewRequest(
                                nonce,
                                PerformanceGuardianProtocol.Shutdown),
                            cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            GuardianReportView? guardianReport =
                null;

            if (File.Exists(
                    guardianReportPath))
            {
                guardianReport =
                    JsonSerializer.Deserialize<GuardianReportView>(
                        File.ReadAllText(
                            guardianReportPath));
            }

            var journalPresent =
                File.Exists(
                    journalPath);

            var passed =
                failure is null &&
                guardian.ExitCode == 0 &&
                guardianReport is not null &&
                guardianReport.EnableCalls == 1 &&
                guardianReport.ReleaseCalls == 1 &&
                guardianReport.CpuHardwareWriteAttempts == 0 &&
                guardianReport.GpuHardwareWriteAttempts == 4 &&
                guardianReport.SourceCpuDispatchAttempts == 0 &&
                guardianReport.SourceGpuDispatchAttempts == 2 &&
                guardianReport.SourceLastSource ==
                    PerformancePowerSourceKind.Ac.ToString() &&
                string.Equals(
                    guardianReport.GpuDomainState,
                    GpuClockSessionState.Disabled.ToString(),
                    StringComparison.Ordinal) &&
                guardianReport.HardwareWritesPerformed &&
                guardianReport.SourceFailure is null &&
                !journalPresent;

            var report =
                new QualificationReport(
                    SchemaVersion: 1,
                    TargetProfileId,
                    StartedAtUtc:
                        startedAt,
                    CompletedAtUtc:
                        DateTimeOffset.UtcNow,
                    Initial:
                        initial,
                    BatterySeen:
                        batterySeen,
                    AcReturnSeen:
                        acReturnSeen,
                    EnableResponse:
                        enableResponse,
                    DisableResponse:
                        disableResponse,
                    ShutdownResponse:
                        shutdownResponse,
                    GuardianExitCode:
                        guardian.HasExited
                            ? guardian.ExitCode
                            : null,
                    GuardianReport:
                        guardianReport,
                    JournalPresent:
                        journalPresent,
                    ProductionHardwareWritesAuthorized:
                        false,
                    QualificationHardwareWritesAuthorized:
                        true,
                    Result:
                        passed
                            ? "PASS"
                            : "FAIL",
                    Failure:
                        failure?.ToString());

            DurableJson(
                qualificationReportPath,
                report);

            Console.WriteLine();
            Console.WriteLine(
                "Step 6F GPU qualification report: " +
                qualificationReportPath);

            if (!passed)
            {
                Console.Error.WriteLine(
                    "Step 6F GPU qualification did not close. Preserve the report and any remaining journal; do not rerun blindly.");

                return 6;
            }

            Console.WriteLine(
                "Step 6F GPU qualification: PASS. Detached Guardian performed initial AC Set, Battery Set, AC Set and one final normal Reset; CPU remained disabled.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Step 6F GPU qualification: FAIL - " +
                ex);

            return 5;
        }
    }

    internal static async Task<int> RunGuardianAsync(
        string[] args)
    {
        if (!TryParseGuardian(
                args,
                out var options,
                out var journalPath,
                out var confirmTarget,
                out var error))
        {
            Console.Error.WriteLine(
                error);

            return 2;
        }

        if (!string.Equals(
                confirmTarget,
                TargetProfileId,
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Exact Step 6F target confirmation token is required.");

            return 2;
        }

        RequireExactTargetAndElevation();

        using var nvml =
            new NvmlClient(
                ExpectedGpuName,
                requirePreferredDevice: true);

        var backend =
            new NvmlGpuClockLimitBackend(
                nvml,
                hardwareWritesAuthorized: true);

        var journal =
            new JsonGpuClockSessionJournal(
                journalPath,
                TargetProfileId);

        using var domains =
            new QualifiedGpuGuardianDomainLifecycle(
                backend,
                journal,
                GpuClockPresetSet.UserRequestedVictus);

        var cpuRecording =
            new RecordingGuardianCpuSourceTransitionSink();

        using var sourceRuntime =
            new GuardianPerformancePowerSourceRuntime(
                new WindowsPerformancePowerSourceReader(),
                cpuRecording,
                domains,
                new WindowsGuardianPowerSourceNotificationListenerFactory());

        var host =
            new PerformanceGuardianHost(
                options,
                domains,
                sourceRuntime);

        return await host.RunAsync(
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal static int SelfTest(
        TextWriter output)
    {
        try
        {
            var temp =
                Path.Combine(
                    Path.GetTempPath(),
                    "vfc-6f-selftest");

            Require(
                TryParseOuter(
                    new[]
                    {
                        "--gpu-gate-6f",
                        "--confirm-target",
                        TargetProfileId,
                        "--timeout-seconds",
                        "45",
                        "--output-directory",
                        temp
                    },
                    out var outer,
                    out _),
                "Step 6F outer arguments parse");

            Require(
                outer.TimeoutSeconds == 45 &&
                string.Equals(
                    outer.OutputDirectory,
                    Path.GetFullPath(temp),
                    StringComparison.Ordinal),
                "Step 6F outer parse values");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--gpu-gate-6f",
                        "--confirm-target",
                        "WRONG"
                    },
                    out _,
                    out _),
                "wrong target rejected");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--gpu-gate-6f",
                        "--confirm-target",
                        TargetProfileId,
                        "--timeout-seconds",
                        "301"
                    },
                    out _,
                    out _),
                "excessive timeout rejected");

            output.WriteLine(
                "Step 6F GPU qualification harness self-test: PASS (argument/target gates only, zero NVML load, zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6F GPU qualification harness self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static ProcessStartInfo NewGuardianStart(
        string pipeName,
        string mutexName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks,
        string reportPath,
        string readyPath,
        string journalPath)
    {
        var start =
            new ProcessStartInfo(
                Executable)
            {
                UseShellExecute =
                    false,
                CreateNoWindow =
                    true
            };

        start.ArgumentList.Add(
            "--run-gpu-gate-6f");

        Add(
            start,
            "--confirm-target",
            TargetProfileId);

        Add(
            start,
            "--target",
            TargetProfileId);

        Add(
            start,
            "--pipe",
            pipeName);

        Add(
            start,
            "--mutex",
            mutexName);

        Add(
            start,
            "--nonce",
            nonce.ToString("D"));

        Add(
            start,
            "--owner-pid",
            ownerPid.ToString());

        Add(
            start,
            "--owner-start-ticks",
            ownerStartTicks.ToString());

        Add(
            start,
            "--report",
            reportPath);

        Add(
            start,
            "--ready",
            readyPath);

        Add(
            start,
            "--journal",
            journalPath);

        return start;
    }

    private static async Task<PerformancePowerSourceObservation> WaitForSourceAsync(
        PerformancePowerSourceKind expected,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var reader =
            new WindowsPerformancePowerSourceReader();

        var deadline =
            DateTimeOffset.UtcNow.AddSeconds(
                timeoutSeconds);

        while (DateTimeOffset.UtcNow <
            deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var observation =
                reader.Read();

            if (observation.Succeeded &&
                observation.Source ==
                    expected)
            {
                return observation;
            }

            await Task.Delay(
                250,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Timed out waiting for directly confirmed source " +
            expected +
            ".");
    }

    private static void RequireExactTargetAndElevation()
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.Is64BitProcess)
        {
            throw new InvalidOperationException(
                "Step 6F requires Windows x64.");
        }

        using var identity =
            WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(
                identity)
            .IsInRole(
                WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "Step 6F hardware qualification requires an elevated Administrator process.");
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

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var pipe =
            new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous |
                PipeOptions.WriteThrough);

        await pipe.ConnectAsync(
            cancellationToken).ConfigureAwait(false);

        return pipe;
    }

    private static async Task<PerformanceGuardianResponse> RoundTripAsync(
        Stream stream,
        PerformanceGuardianRequest request,
        CancellationToken cancellationToken)
    {
        await PerformanceGuardianCodec.WriteRequestAsync(
            stream,
            request,
            cancellationToken).ConfigureAwait(false);

        return
            await PerformanceGuardianCodec.ReadResponseAsync(
                stream,
                cancellationToken).ConfigureAwait(false) ??
            throw new EndOfStreamException(
                "Step 6F Guardian pipe closed before response.");
    }

    private static PerformanceGuardianRequest NewRequest(
        Guid nonce,
        string type,
        int? ownerPid = null,
        long? ownerStartTicks = null,
        bool? cpuEnabled = null,
        bool? gpuEnabled = null) =>
        new(
            PerformanceGuardianProtocol.Version,
            Guid.NewGuid(),
            TargetProfileId,
            nonce,
            type,
            ownerPid,
            ownerStartTicks,
            cpuEnabled,
            gpuEnabled);

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (!File.Exists(
            path))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(
                25,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryParseOuter(
        string[] args,
        out OuterOptions options,
        out string error)
    {
        options =
            default;

        error =
            "Invalid Step 6F GPU qualification arguments.";

        if (args.Length < 3 ||
            args[0] !=
                "--gpu-gate-6f")
        {
            return false;
        }

        string? confirm =
            null;

        var timeoutSeconds =
            60;

        string? outputDirectory =
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

                case "--output-directory"
                    when index + 1 <
                         args.Length:
                    outputDirectory =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    error =
                        "Unknown or incomplete Step 6F argument: " +
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
                "Exact target confirmation is required: " +
                TargetProfileId;

            return false;
        }

        if (timeoutSeconds is < 15 or > 300)
        {
            error =
                "TimeoutSeconds must be between 15 and 300.";

            return false;
        }

        options =
            new OuterOptions(
                timeoutSeconds,
                outputDirectory);

        return true;
    }

    private static bool TryParseGuardian(
        string[] args,
        out GuardianHostOptions options,
        out string journalPath,
        out string confirmTarget,
        out string error)
    {
        options =
            default;

        journalPath =
            string.Empty;

        confirmTarget =
            string.Empty;

        error =
            "Invalid Step 6F detached Guardian arguments.";

        if (args.Length < 2 ||
            args[0] !=
                "--run-gpu-gate-6f")
        {
            return false;
        }

        string? target =
            null;

        string? pipe =
            null;

        string? mutex =
            null;

        Guid nonce =
            Guid.Empty;

        int ownerPid =
            0;

        long ownerStartTicks =
            0;

        string? report =
            null;

        string? ready =
            null;

        string? journal =
            null;

        string? confirm =
            null;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            if (index + 1 >=
                args.Length)
            {
                return false;
            }

            var name =
                args[index];

            var value =
                args[++index];

            switch (name)
            {
                case "--confirm-target":
                    confirm =
                        value;
                    break;

                case "--target":
                    target =
                        value;
                    break;

                case "--pipe":
                    pipe =
                        value;
                    break;

                case "--mutex":
                    mutex =
                        value;
                    break;

                case "--nonce":
                    if (!Guid.TryParse(
                            value,
                            out nonce))
                    {
                        return false;
                    }
                    break;

                case "--owner-pid":
                    if (!int.TryParse(
                            value,
                            out ownerPid))
                    {
                        return false;
                    }
                    break;

                case "--owner-start-ticks":
                    if (!long.TryParse(
                            value,
                            out ownerStartTicks))
                    {
                        return false;
                    }
                    break;

                case "--report":
                    report =
                        Path.GetFullPath(
                            value);
                    break;

                case "--ready":
                    ready =
                        Path.GetFullPath(
                            value);
                    break;

                case "--journal":
                    journal =
                        Path.GetFullPath(
                            value);
                    break;

                default:
                    return false;
            }
        }

        if (!string.Equals(
                target,
                TargetProfileId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                pipe) ||
            !string.Equals(
                mutex,
                PerformanceGuardianHost.ProductionMutexName(
                    TargetProfileId),
                StringComparison.Ordinal) ||
            nonce ==
                Guid.Empty ||
            ownerPid <= 0 ||
            ownerStartTicks <= 0 ||
            string.IsNullOrWhiteSpace(
                report) ||
            string.IsNullOrWhiteSpace(
                ready) ||
            string.IsNullOrWhiteSpace(
                journal) ||
            !string.Equals(
                confirm,
                TargetProfileId,
                StringComparison.Ordinal))
        {
            return false;
        }

        options =
            new GuardianHostOptions(
                TargetProfileId,
                pipe!,
                mutex!,
                nonce,
                ownerPid,
                ownerStartTicks,
                report!,
                ready);

        journalPath =
            journal!;

        confirmTarget =
            confirm!;

        return true;
    }

    private static void Add(
        ProcessStartInfo start,
        string name,
        string value)
    {
        start.ArgumentList.Add(
            name);

        start.ArgumentList.Add(
            value);
    }

    private static void DurableJson<T>(
        string path,
        T value)
    {
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
                new UTF8Encoding(false),
                4096,
                leaveOpen: true);

        writer.Write(
            json);

        writer.Flush();
        stream.Flush(true);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --gpu-gate-6f --confirm-target HP-8C40-9D0R1LA-F18 [--timeout-seconds 15..300] [--output-directory <path>]");
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

    private readonly record struct OuterOptions(
        int TimeoutSeconds,
        string? OutputDirectory);

    private sealed record QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        PerformancePowerSourceObservation Initial,
        PerformancePowerSourceObservation? BatterySeen,
        PerformancePowerSourceObservation? AcReturnSeen,
        PerformanceGuardianResponse? EnableResponse,
        PerformanceGuardianResponse? DisableResponse,
        PerformanceGuardianResponse? ShutdownResponse,
        int? GuardianExitCode,
        GuardianReportView? GuardianReport,
        bool JournalPresent,
        bool ProductionHardwareWritesAuthorized,
        bool QualificationHardwareWritesAuthorized,
        string Result,
        string? Failure);

    private sealed record GuardianReportView(
        int SchemaVersion,
        string TargetProfileId,
        int OwnerPid,
        long OwnerStartUtcTicks,
        bool ParentLostDetected,
        string ExitReason,
        string? FinalPhase,
        int Connections,
        int AcceptedRequests,
        int RejectedRequests,
        int EnableCalls,
        int ReleaseCalls,
        string? LastReleaseReason,
        string? InitialSource,
        int CpuHardwareWriteAttempts,
        int GpuHardwareWriteAttempts,
        string? CpuDomainState,
        string? GpuDomainState,
        string? CpuDomainStatus,
        string? GpuDomainStatus,
        bool SourceRuntimeActive,
        int SourceRuntimeStartCalls,
        int SourceRuntimeStopCalls,
        int SourceListenerRegistrations,
        int SourceNotificationSignals,
        int SourceDuplicateSignals,
        int SourceCpuDispatchAttempts,
        int SourceGpuDispatchAttempts,
        string SourceLastSource,
        string? SourceLastStatus,
        string? SourceLastStopReason,
        string? SourceFailure,
        bool HardwareWritesPerformed,
        string? Failure);
}
