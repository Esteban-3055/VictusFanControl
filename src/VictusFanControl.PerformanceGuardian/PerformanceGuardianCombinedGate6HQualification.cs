using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>
/// Step 6H bounded combined CPU+GPU Guardian qualification.
///
/// This is not production mode. It composes only the independently qualified
/// CPU and GPU domain controllers and requires explicit acknowledgement for
/// CPU RAPL writes plus the human GPU exclusive-controller contract.
/// </summary>
internal static class PerformanceGuardianCombinedGate6HQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const string ExpectedCpuToken =
        "i7-13700H";

    private const string ExpectedGpuName =
        "NVIDIA GeForce RTX 4060 Laptop GPU";

    private const string ExpectedIntelMsrSha256 =
        "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";

    private static string Executable =>
        Path.Combine(
            AppContext.BaseDirectory,
            "VictusFanControl.PerformanceGuardian.exe");

    private static string CpuJournalPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl",
            "Performance",
            TargetProfileId,
            "cpu-power-session.json");

    private static string GpuJournalPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl",
            "Performance",
            TargetProfileId,
            "gpu-clock-session.json");

    internal static async Task<int> RunOuterAsync(
        string[] args)
    {
        if (!TryParseOuter(
                args,
                out var options,
                out var error))
        {
            Console.Error.WriteLine(
                error);

            PrintUsage();
            return 2;
        }

        try
        {
            RequireExactTargetAndElevation();
            RequireQualifiedModule(
                options.ModulePath);

            if (File.Exists(
                    CpuJournalPath) ||
                File.Exists(
                    GpuJournalPath))
            {
                throw new InvalidOperationException(
                    "Step 6H refuses to start while either persistent CPU/GPU journal exists. Preserve and review stale evidence before any rerun.");
            }

            var root =
                options.OutputDirectory ??
                Path.Combine(
                    Environment.CurrentDirectory,
                    "logs",
                    "performance-guardian-6h-combined_" +
                    DateTime.Now.ToString(
                        "yyyy-MM-dd_HHmmss"));

            root =
                Path.GetFullPath(
                    root);

            Directory.CreateDirectory(
                root);

            var preflightPath =
                Path.Combine(
                    root,
                    "preflight.json");

            var guardianReportPath =
                Path.Combine(
                    root,
                    "guardian-report.json");

            var qualificationPath =
                Path.Combine(
                    root,
                    "qualification.json");

            var readyPath =
                Path.Combine(
                    root,
                    "guardian-ready.txt");

            var preflight =
                CaptureReadOnlyPreflight(
                    options.ModulePath);

            DurableJson(
                preflightPath,
                preflight);

            Console.WriteLine(
                "Step 6H combined read-only preflight: PASS. CPU and GPU hardware-write gates remained closed.");

            using var owner =
                Process.GetCurrentProcess();

            var ownerStartTicks =
                owner.StartTime
                    .ToUniversalTime()
                    .Ticks;

            var nonce =
                Guid.NewGuid();

            var pipeName =
                "VictusFanControl.PerformanceGuardian.6H.Combined." +
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
                        options.ModulePath)) ??
                throw new IOException(
                    "Step 6H detached combined Guardian did not start.");

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

            CpuPowerSessionJournalRecord? initialCpu =
                null;

            CpuPowerSessionJournalRecord? batteryCpu =
                null;

            CpuPowerSessionJournalRecord? acReturnCpu =
                null;

            GpuClockSessionJournalRecord? initialGpu =
                null;

            GpuClockSessionJournalRecord? batteryGpu =
                null;

            GpuClockSessionJournalRecord? acReturnGpu =
                null;

            CpuPowerLimitSnapshot? finalCpu =
                null;

            Exception? failure =
                null;

            try
            {
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(
                            options.TimeoutSeconds * 3 +
                            60));

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
                    "Step 6H HELLO was not accepted.");

                enableResponse =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.EnableSession,
                            cpuEnabled: true,
                            gpuEnabled: true),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    enableResponse.Ok &&
                    enableResponse.SessionEnabled &&
                    enableResponse.CpuEnabled &&
                    enableResponse.GpuEnabled,
                    "Step 6H combined ENABLE_SESSION failed.");

                initialCpu =
                    await WaitForCpuJournalAsync(
                        new CpuPowerLimitRequest(
                            35,
                            60),
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                initialGpu =
                    await WaitForGpuJournalAsync(
                        new GpuClockLimitRequest(
                            210,
                            1850),
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                RequireCpuApplied(
                    initialCpu,
                    preflight.CpuPowerUnitWatts,
                    35,
                    60,
                    "initial AC");

                RequireGpuActive(
                    initialGpu,
                    210,
                    1850,
                    "initial AC");

                Console.WriteLine();
                Console.WriteLine(
                    "Step 6H: AC state confirmed: CPU 35/60 W + GPU 210..1850 MHz.");
                Console.WriteLine(
                    "Disconnect the charger now. Waiting up to " +
                    options.TimeoutSeconds +
                    " seconds for Battery confirmation...");

                batterySeen =
                    await WaitForSourceAsync(
                        PerformancePowerSourceKind.Battery,
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                batteryCpu =
                    await WaitForCpuJournalAsync(
                        new CpuPowerLimitRequest(
                            8,
                            15),
                        options.TimeoutSeconds,
                        timeout.Token,
                        initialCpu.Generation).ConfigureAwait(false);

                batteryGpu =
                    await WaitForGpuJournalAsync(
                        new GpuClockLimitRequest(
                            210,
                            1200),
                        options.TimeoutSeconds,
                        timeout.Token,
                        initialGpu.Generation).ConfigureAwait(false);

                RequireCpuApplied(
                    batteryCpu,
                    preflight.CpuPowerUnitWatts,
                    8,
                    15,
                    "Battery");

                RequireGpuActive(
                    batteryGpu,
                    210,
                    1200,
                    "Battery");

                Console.WriteLine(
                    "Step 6H: Battery state confirmed: CPU 8/15 W + GPU 210..1200 MHz.");
                Console.WriteLine(
                    "Reconnect the charger now. Waiting up to " +
                    options.TimeoutSeconds +
                    " seconds for AC confirmation...");

                acReturnSeen =
                    await WaitForSourceAsync(
                        PerformancePowerSourceKind.Ac,
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                acReturnCpu =
                    await WaitForCpuJournalAsync(
                        new CpuPowerLimitRequest(
                            35,
                            60),
                        options.TimeoutSeconds,
                        timeout.Token,
                        batteryCpu.Generation).ConfigureAwait(false);

                acReturnGpu =
                    await WaitForGpuJournalAsync(
                        new GpuClockLimitRequest(
                            210,
                            1850),
                        options.TimeoutSeconds,
                        timeout.Token,
                        batteryGpu.Generation).ConfigureAwait(false);

                RequireCpuApplied(
                    acReturnCpu,
                    preflight.CpuPowerUnitWatts,
                    35,
                    60,
                    "AC return");

                RequireGpuActive(
                    acReturnGpu,
                    210,
                    1850,
                    "AC return");

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
                    "Step 6H DISABLE_SESSION did not complete combined normal release.");

                shutdownResponse =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.Shutdown),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    shutdownResponse.Ok &&
                    string.Equals(
                        shutdownResponse.Code,
                        "SHUTDOWN_ACCEPTED",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        shutdownResponse.Phase,
                        PerformanceGuardianAuthorityPhase.Stopped.ToString(),
                        StringComparison.Ordinal),
                    "Step 6H SHUTDOWN did not finish in Stopped.");

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

                await BestEffortCleanupAsync(
                    pipeName,
                    nonce,
                    owner.Id,
                    ownerStartTicks).ConfigureAwait(false);

                if (!guardian.HasExited)
                {
                    try
                    {
                        using var exitTimeout =
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(10));

                        await guardian.WaitForExitAsync(
                            exitTimeout.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
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

            if (guardian.HasExited)
            {
                try
                {
                    using var cpuReadback =
                        new PawnIoCpuPowerLimitBackend(
                            options.ModulePath,
                            hardwareWritesAuthorized:
                                false);

                    finalCpu =
                        cpuReadback.Read();
                }
                catch (Exception ex)
                {
                    failure ??=
                        new InvalidOperationException(
                            "Step 6H final CPU readback failed.",
                            ex);
                }
            }

            var cpuJournalPresent =
                File.Exists(
                    CpuJournalPath);

            var gpuJournalPresent =
                File.Exists(
                    GpuJournalPath);

            var cpuSameSession =
                initialCpu is not null &&
                batteryCpu is not null &&
                acReturnCpu is not null &&
                initialCpu.SessionId ==
                    batteryCpu.SessionId &&
                initialCpu.SessionId ==
                    acReturnCpu.SessionId;

            var gpuSameSession =
                initialGpu is not null &&
                batteryGpu is not null &&
                acReturnGpu is not null &&
                initialGpu.SessionId ==
                    batteryGpu.SessionId &&
                initialGpu.SessionId ==
                    acReturnGpu.SessionId;

            var cpuGenerations =
                initialCpu is not null &&
                batteryCpu is not null &&
                acReturnCpu is not null &&
                initialCpu.Generation <
                    batteryCpu.Generation &&
                batteryCpu.Generation <
                    acReturnCpu.Generation;

            var gpuGenerations =
                initialGpu is not null &&
                batteryGpu is not null &&
                acReturnGpu is not null &&
                initialGpu.Generation <
                    batteryGpu.Generation &&
                batteryGpu.Generation <
                    acReturnGpu.Generation;

            var cpuImmutableBaseline =
                initialCpu is not null &&
                batteryCpu is not null &&
                acReturnCpu is not null &&
                initialCpu.OriginalBaseline ==
                    batteryCpu.OriginalBaseline &&
                initialCpu.OriginalBaseline ==
                    acReturnCpu.OriginalBaseline;

            var finalCpuOwnedFieldsRestored =
                initialCpu is not null &&
                finalCpu.HasValue &&
                CpuRaplPowerLimitCodec.OwnedFieldsMatch(
                    initialCpu.OriginalBaseline.Raw,
                    finalCpu.Value.Raw);

            var passed =
                failure is null &&
                guardian.HasExited &&
                guardian.ExitCode == 0 &&
                enableResponse?.Ok == true &&
                disableResponse?.Ok == true &&
                shutdownResponse?.Ok == true &&
                cpuSameSession &&
                gpuSameSession &&
                cpuGenerations &&
                gpuGenerations &&
                cpuImmutableBaseline &&
                finalCpuOwnedFieldsRestored &&
                guardianReport is not null &&
                guardianReport.AcceptedRequests == 4 &&
                guardianReport.RejectedRequests == 0 &&
                guardianReport.EnableCalls == 1 &&
                guardianReport.ReleaseCalls == 1 &&
                guardianReport.CpuHardwareWriteAttempts == 4 &&
                guardianReport.GpuHardwareWriteAttempts == 4 &&
                guardianReport.SourceCpuDispatchAttempts == 2 &&
                guardianReport.SourceGpuDispatchAttempts == 2 &&
                guardianReport.SourceNotificationSignals >= 2 &&
                guardianReport.SourceReconciliationSignals == 1 &&
                guardianReport.SourceDuplicateSignals >= 1 &&
                guardianReport.SourceRuntimeStartCalls == 1 &&
                guardianReport.SourceRuntimeStopCalls == 1 &&
                string.Equals(
                    guardianReport.InitialSource,
                    PerformancePowerSourceKind.Ac.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.SourceLastSource,
                    PerformancePowerSourceKind.Ac.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.CpuDomainState,
                    CpuPowerLimiterState.Disabled.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.GpuDomainState,
                    GpuClockSessionState.Disabled.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.ExitReason,
                    "CLIENT_SHUTDOWN",
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.FinalPhase,
                    PerformanceGuardianAuthorityPhase.Stopped.ToString(),
                    StringComparison.Ordinal) &&
                guardianReport.HardwareWritesPerformed &&
                guardianReport.SourceFailure is null &&
                guardianReport.Failure is null &&
                !cpuJournalPresent &&
                !gpuJournalPresent;

            var report =
                new QualificationReport(
                    SchemaVersion:
                        1,
                    TargetProfileId,
                    StartedAtUtc:
                        startedAt,
                    CompletedAtUtc:
                        DateTimeOffset.UtcNow,
                    ModulePath:
                        options.ModulePath,
                    ModuleSha256:
                        ExpectedIntelMsrSha256,
                    ExclusiveGpuControllerConfirmed:
                        options.ExclusiveGpuControllerConfirmed,
                    ReadOnlyPreflight:
                        preflight,
                    BatterySeen:
                        batterySeen,
                    AcReturnSeen:
                        acReturnSeen,
                    InitialCpuJournal:
                        initialCpu,
                    BatteryCpuJournal:
                        batteryCpu,
                    AcReturnCpuJournal:
                        acReturnCpu,
                    InitialGpuJournal:
                        initialGpu,
                    BatteryGpuJournal:
                        batteryGpu,
                    AcReturnGpuJournal:
                        acReturnGpu,
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
                    FinalCpuSnapshot:
                        finalCpu,
                    CpuSameSession:
                        cpuSameSession,
                    GpuSameSession:
                        gpuSameSession,
                    CpuIncreasingGenerations:
                        cpuGenerations,
                    GpuIncreasingGenerations:
                        gpuGenerations,
                    CpuImmutableOriginalBaseline:
                        cpuImmutableBaseline,
                    FinalCpuOwnedFieldsRestored:
                        finalCpuOwnedFieldsRestored,
                    CpuJournalPath,
                    CpuJournalPresent:
                        cpuJournalPresent,
                    GpuJournalPath,
                    GpuJournalPresent:
                        gpuJournalPresent,
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
                qualificationPath,
                report);

            Console.WriteLine();
            Console.WriteLine(
                "Step 6H combined qualification report: " +
                qualificationPath);

            if (!passed)
            {
                Console.Error.WriteLine(
                    "Step 6H combined qualification did not close. Preserve the full evidence directory and any remaining CPU/GPU journal; do not rerun blindly.");

                return 6;
            }

            Console.WriteLine(
                "Step 6H combined qualification: PASS. One Guardian completed CPU+GPU AC -> Battery -> AC and normal dual-domain cleanup.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Step 6H combined qualification: FAIL - " +
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
                out var modulePath,
                out var cpuJournalPath,
                out var gpuJournalPath,
                out var error))
        {
            Console.Error.WriteLine(
                error);

            return 2;
        }

        RequireExactTargetAndElevation();
        RequireQualifiedModule(
            modulePath);

        if (!string.Equals(
                Path.GetFullPath(
                    cpuJournalPath),
                Path.GetFullPath(
                    CpuJournalPath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFullPath(
                    gpuJournalPath),
                Path.GetFullPath(
                    GpuJournalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Step 6H child requires the exact target-scoped CPU and GPU journal paths.");
        }

        if (File.Exists(
                cpuJournalPath) ||
            File.Exists(
                gpuJournalPath))
        {
            throw new InvalidOperationException(
                "Step 6H child refuses new write authority while a persistent CPU/GPU journal already exists.");
        }

        var source =
            new WindowsPerformancePowerSourceReader()
                .Read();

        if (!source.Succeeded ||
            source.Source !=
                PerformancePowerSourceKind.Ac)
        {
            throw new InvalidOperationException(
                "Step 6H child requires direct AC confirmation before hardware-write authority.");
        }

        var cpuBackend =
            new PawnIoCpuPowerLimitBackend(
                modulePath,
                hardwareWritesAuthorized:
                    true);

        try
        {
            var baseline =
                cpuBackend.Read();

            if (cpuBackend.PawnIoVersion <
                    new Version(
                        2,
                        2,
                        0) ||
                cpuBackend.PhysicalCoreCount !=
                    14 ||
                baseline.Locked ||
                Math.Abs(
                    baseline.Pl1Watts -
                    45) >
                    1e-9 ||
                Math.Abs(
                    baseline.Pl2Watts -
                    115) >
                    1e-9)
            {
                throw new InvalidOperationException(
                    "Step 6H child CPU baseline no longer matches the qualified 45/115 W unlocked target.");
            }

            if (cpuBackend.PowerInfo.MinimumWatts >
                    0 &&
                cpuBackend.PowerInfo.MinimumWatts >
                    8)
            {
                throw new InvalidOperationException(
                    "Live RAPL minimum now exceeds the qualified Battery PL1.");
            }

            using var nvml =
                new NvmlClient(
                    ExpectedGpuName,
                    requirePreferredDevice:
                        true);

            var gpuBackend =
                new NvmlGpuClockLimitBackend(
                    nvml,
                    hardwareWritesAuthorized:
                        true);

            if (!gpuBackend.Capabilities.HasCompleteCommandSurface)
            {
                throw new InvalidOperationException(
                    "Step 6H child NVML locked-clock command surface is incomplete.");
            }

            var cpuDomain =
                new QualifiedCpuGuardianDomainLifecycle(
                    cpuBackend,
                    new JsonCpuPowerSessionJournal(
                        cpuJournalPath,
                        TargetProfileId),
                    CpuPowerProductDefaults.CreateDefaultPresetSet(),
                    requiredInitialSource:
                        PerformancePowerSourceKind.Ac);

            var gpuDomain =
                new QualifiedGpuGuardianDomainLifecycle(
                    gpuBackend,
                    new JsonGpuClockSessionJournal(
                        gpuJournalPath,
                        TargetProfileId),
                    GpuClockPresetSet.UserRequestedVictus);

            using var combined =
                new CombinedGuardianDomainLifecycle(
                    cpuDomain,
                    cpuDomain,
                    gpuDomain,
                    gpuDomain,
                    requiredInitialSource:
                        PerformancePowerSourceKind.Ac);

            using var sourceRuntime =
                new GuardianPerformancePowerSourceRuntime(
                    new WindowsPerformancePowerSourceReader(),
                    combined,
                    combined,
                    new WindowsGuardianPowerSourceNotificationListenerFactory());

            var host =
                new PerformanceGuardianHost(
                    options,
                    combined,
                    sourceRuntime);

            return await host.RunAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            cpuBackend.Dispose();
            throw;
        }
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

            var outDir =
                Path.Combine(
                    Path.GetTempPath(),
                    "vfc-step6h");

            Require(
                TryParseOuter(
                    new[]
                    {
                        "--combined-gate-6h",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--confirm-exclusive-gpu-controller",
                        "--module",
                        module,
                        "--timeout-seconds",
                        "120",
                        "--output-directory",
                        outDir
                    },
                    out var outer,
                    out _),
                "Step 6H outer parser accepts exact bounded contract");

            Require(
                outer.TimeoutSeconds == 120 &&
                outer.CpuHardwareWritesConfirmed &&
                outer.ExclusiveGpuControllerConfirmed,
                "Step 6H parsed confirmations");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--combined-gate-6h",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--module",
                        module
                    },
                    out _,
                    out _),
                "GPU exclusive-controller confirmation is mandatory");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--combined-gate-6h",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-exclusive-gpu-controller",
                        "--module",
                        module
                    },
                    out _,
                    out _),
                "CPU hardware-write confirmation is mandatory");

            output.WriteLine(
                "Step 6H combined physical harness self-test: PASS (argument/target/dual-confirmation gates only, zero PawnIO/NVML load and zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6H combined physical harness self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static CombinedPreflight CaptureReadOnlyPreflight(
        string modulePath)
    {
        RequireExactTargetAndElevation();
        RequireQualifiedModule(
            modulePath);

        if (File.Exists(
                CpuJournalPath) ||
            File.Exists(
                GpuJournalPath))
        {
            throw new InvalidOperationException(
                "Step 6H preflight requires both persistent journals absent.");
        }

        using (var mutex =
               new Mutex(
                   initiallyOwned:
                       true,
                   PerformanceGuardianHost.ProductionMutexName(
                       TargetProfileId),
                   out var createdNew))
        {
            if (!createdNew)
            {
                throw new InvalidOperationException(
                    "Another PerformanceGuardian target writer owns the production mutex.");
            }

            mutex.ReleaseMutex();
        }

        var source =
            new WindowsPerformancePowerSourceReader()
                .Read();

        if (!source.Succeeded ||
            source.Source !=
                PerformancePowerSourceKind.Ac)
        {
            throw new InvalidOperationException(
                "Step 6H preflight requires direct AC confirmation.");
        }

        using var cpu =
            new PawnIoCpuPowerLimitBackend(
                modulePath,
                hardwareWritesAuthorized:
                    false);

        var cpuSamples =
            new List<CpuPowerLimitSnapshot>();

        for (var index = 0;
             index < 3;
             index++)
        {
            cpuSamples.Add(
                cpu.Read());

            if (index !=
                2)
            {
                Thread.Sleep(
                    100);
            }
        }

        if (cpuSamples
                .Select(
                    item =>
                        item.Raw)
                .Distinct()
                .Count() !=
            1)
        {
            throw new InvalidOperationException(
                "Step 6H CPU baseline changed during preflight.");
        }

        var baseline =
            cpuSamples[0];

        if (cpu.PawnIoVersion <
                new Version(
                    2,
                    2,
                    0) ||
            cpu.PhysicalCoreCount !=
                14 ||
            baseline.Locked ||
            Math.Abs(
                baseline.Pl1Watts -
                45) >
                1e-9 ||
            Math.Abs(
                baseline.Pl2Watts -
                115) >
                1e-9)
        {
            throw new InvalidOperationException(
                "Step 6H CPU preflight did not observe the qualified unlocked 45/115 W baseline.");
        }

        var cpuPresets =
            CpuPowerProductDefaults.CreateDefaultPresetSet();

        var cpuAcPlan =
            cpu.BuildApplyPlan(
                baseline,
                cpuPresets.Ac.ToRequest());

        var cpuBatteryPlan =
            cpu.BuildApplyPlan(
                baseline,
                cpuPresets.Battery.ToRequest());

        var cpuGateClosed =
            false;

        try
        {
            cpu.Write(
                cpuAcPlan.RequestedRaw);
        }
        catch (InvalidOperationException ex)
            when (string.Equals(
                ex.Message,
                "CPU_POWER_BACKEND_HARDWARE_WRITE_GATE_CLOSED",
                StringComparison.Ordinal))
        {
            cpuGateClosed =
                true;
        }

        if (!cpuGateClosed)
        {
            throw new InvalidOperationException(
                "Step 6H CPU write gate could not be proven closed.");
        }

        using var nvml =
            new NvmlClient(
                ExpectedGpuName,
                requirePreferredDevice:
                    true);

        var gpu =
            new NvmlGpuClockLimitBackend(
                nvml,
                hardwareWritesAuthorized:
                    false);

        var capabilities =
            gpu.Capabilities;

        if (!capabilities.HasCompleteCommandSurface)
        {
            throw new InvalidOperationException(
                "Step 6H NVML Set/Reset command surface is incomplete.");
        }

        var gpuObservation =
            gpu.ReadObservation();

        if (!gpuObservation.Succeeded)
        {
            throw new InvalidOperationException(
                "Step 6H GPU observation failed: " +
                gpuObservation.Status);
        }

        var gpuClosedProbe =
            gpu.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1850));

        if (gpuClosedProbe.Succeeded ||
            gpuClosedProbe.FailureKind !=
                GpuClockBackendFailureKind.WriteGateClosed)
        {
            throw new InvalidOperationException(
                "Step 6H GPU write gate could not be proven closed.");
        }

        return new CombinedPreflight(
            CapturedAtUtc:
                DateTimeOffset.UtcNow,
            Source:
                source,
            ModuleSha256:
                ExpectedIntelMsrSha256,
            PawnIoVersion:
                cpu.PawnIoVersion.ToString(),
            PhysicalCoreCount:
                cpu.PhysicalCoreCount,
            CpuPowerUnitWatts:
                cpu.PowerUnitWatts,
            CpuPowerInfo:
                cpu.PowerInfo,
            CpuBaselineSamples:
                cpuSamples.ToArray(),
            CpuAcPlan:
                cpuAcPlan,
            CpuBatteryPlan:
                cpuBatteryPlan,
            CpuHardwareWriteGateClosedProved:
                true,
            GpuDeviceName:
                nvml.DeviceName,
            GpuCapabilities:
                capabilities,
            GpuObservation:
                gpuObservation,
            GpuClosedGateProbe:
                gpuClosedProbe,
            GpuHardwareWriteGateClosedProved:
                true,
            CpuJournalPresent:
                false,
            GpuJournalPresent:
                false,
            ProductionMutexAvailable:
                true,
            HardwareWritesPerformed:
                false,
            Result:
                "PASS");
    }

    private static async Task<CpuPowerSessionJournalRecord> WaitForCpuJournalAsync(
        CpuPowerLimitRequest expected,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        long minimumGenerationExclusive = 0)
    {
        var journal =
            new JsonCpuPowerSessionJournal(
                CpuJournalPath,
                TargetProfileId);

        var deadline =
            DateTimeOffset.UtcNow.AddSeconds(
                timeoutSeconds);

        CpuPowerSessionJournalRecord? last =
            null;

        while (DateTimeOffset.UtcNow <
            deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                last =
                    journal.Load();
            }
            catch (IOException)
            {
            }

            if (last is not null &&
                last.Generation >
                    minimumGenerationExclusive &&
                last.Phase ==
                    CpuPowerJournalPhase.Owned &&
                last.Request ==
                    expected &&
                !last.PendingRaw.HasValue)
            {
                return last;
            }

            if (last is not null &&
                last.Phase is
                    CpuPowerJournalPhase.Contested or
                    CpuPowerJournalPhase.Yielded or
                    CpuPowerJournalPhase.Unresolved)
            {
                throw new InvalidOperationException(
                    "Step 6H CPU journal left clean Owned state: " +
                    last.Phase);
            }

            await Task.Delay(
                50,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Timed out waiting for CPU journal " +
            expected.Pl1Watts +
            "/" +
            expected.Pl2Watts +
            " W.");
    }

    private static async Task<GpuClockSessionJournalRecord> WaitForGpuJournalAsync(
        GpuClockLimitRequest expected,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        long minimumGenerationExclusive = 0)
    {
        var journal =
            new JsonGpuClockSessionJournal(
                GpuJournalPath,
                TargetProfileId);

        var deadline =
            DateTimeOffset.UtcNow.AddSeconds(
                timeoutSeconds);

        GpuClockSessionJournalRecord? last =
            null;

        while (DateTimeOffset.UtcNow <
            deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                last =
                    journal.Load();
            }
            catch (IOException)
            {
            }

            if (last is not null &&
                last.Generation >
                    minimumGenerationExclusive &&
                last.Phase ==
                    GpuClockJournalPhase.ActiveUnverified &&
                last.CommittedRequest ==
                    expected &&
                !last.PendingRequest.HasValue)
            {
                return last;
            }

            if (last is not null &&
                last.Phase ==
                    GpuClockJournalPhase.RecoveryRequired)
            {
                throw new InvalidOperationException(
                    "Step 6H GPU journal entered RecoveryRequired; blind Reset/retry is forbidden.");
            }

            await Task.Delay(
                50,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Timed out waiting for GPU journal " +
            expected.MinGraphicsClockMHz +
            ".." +
            expected.MaxGraphicsClockMHz +
            " MHz.");
    }

    private static void RequireCpuApplied(
        CpuPowerSessionJournalRecord record,
        double unit,
        double pl1,
        double pl2,
        string label)
    {
        var applied =
            CpuRaplPowerLimitCodec.DecodeSnapshot(
                record.AppliedRaw,
                unit);

        Require(
            record.Phase ==
                CpuPowerJournalPhase.Owned &&
            record.ExternalHandoff is null &&
            record.Conflict.State ==
                CpuPowerConflictState.Clear &&
            Math.Abs(
                applied.Pl1Watts -
                pl1) <
                1e-9 &&
            Math.Abs(
                applied.Pl2Watts -
                pl2) <
                1e-9,
            "Step 6H CPU " +
            label +
            " durable/readback evidence mismatch.");
    }

    private static void RequireGpuActive(
        GpuClockSessionJournalRecord record,
        uint min,
        uint max,
        string label)
    {
        Require(
            record.Phase ==
                GpuClockJournalPhase.ActiveUnverified &&
            record.CommittedRequest ==
                new GpuClockLimitRequest(
                    min,
                    max) &&
            !record.PendingRequest.HasValue &&
            string.IsNullOrWhiteSpace(
                record.RecoveryReason),
            "Step 6H GPU " +
            label +
            " durable ActiveUnverified evidence mismatch.");
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

    private static async Task BestEffortCleanupAsync(
        string pipeName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks)
    {
        try
        {
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));

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
                        ownerPid,
                        ownerStartTicks),
                    timeout.Token).ConfigureAwait(false);

            if (hello.Ok &&
                hello.SessionEnabled)
            {
                _ =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.DisableSession),
                        timeout.Token).ConfigureAwait(false);
            }

            _ =
                await RoundTripAsync(
                    pipe,
                    NewRequest(
                        nonce,
                        PerformanceGuardianProtocol.Shutdown),
                    timeout.Token).ConfigureAwait(false);
        }
        catch
        {
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
        string modulePath)
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
            "--run-combined-gate-6h");

        Add(
            start,
            "--confirm-target",
            TargetProfileId);

        Add(
            start,
            "--confirm-cpu-hardware-writes",
            "true");

        Add(
            start,
            "--confirm-exclusive-gpu-controller",
            "true");

        Add(
            start,
            "--module",
            modulePath);

        Add(
            start,
            "--module-sha256",
            ExpectedIntelMsrSha256);

        Add(
            start,
            "--cpu-journal",
            CpuJournalPath);

        Add(
            start,
            "--gpu-journal",
            GpuJournalPath);

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

        return start;
    }

    private static bool TryParseOuter(
        string[] args,
        out OuterOptions options,
        out string error)
    {
        options =
            default;

        error =
            "Invalid Step 6H combined qualification arguments.";

        if (args.Length < 7 ||
            args[0] !=
                "--combined-gate-6h")
        {
            return false;
        }

        string? target =
            null;

        string? module =
            null;

        string? output =
            null;

        var cpuConfirmed =
            false;

        var gpuConfirmed =
            false;

        var timeoutSeconds =
            120;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--confirm-target"
                    when index + 1 <
                         args.Length:
                    target =
                        args[++index];
                    break;

                case "--confirm-cpu-hardware-writes":
                    cpuConfirmed =
                        true;
                    break;

                case "--confirm-exclusive-gpu-controller":
                    gpuConfirmed =
                        true;
                    break;

                case "--module"
                    when index + 1 <
                         args.Length:
                    module =
                        Path.GetFullPath(
                            args[++index]);
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
                    output =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    error =
                        "Unknown or incomplete Step 6H argument: " +
                        args[index];

                    return false;
            }
        }

        if (!string.Equals(
                target,
                TargetProfileId,
                StringComparison.Ordinal))
        {
            error =
                "Exact Step 6H target confirmation is required.";

            return false;
        }

        if (!cpuConfirmed)
        {
            error =
                "--confirm-cpu-hardware-writes is required.";

            return false;
        }

        if (!gpuConfirmed)
        {
            error =
                "--confirm-exclusive-gpu-controller is required.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                module))
        {
            error =
                "--module <IntelMSR.bin> is required.";

            return false;
        }

        if (timeoutSeconds is < 15 or > 300)
        {
            error =
                "Step 6H timeout must be 15..300 seconds.";

            return false;
        }

        options =
            new OuterOptions(
                timeoutSeconds,
                module!,
                output,
                cpuConfirmed,
                gpuConfirmed);

        return true;
    }

    private static bool TryParseGuardian(
        string[] args,
        out GuardianHostOptions options,
        out string modulePath,
        out string cpuJournalPath,
        out string gpuJournalPath,
        out string error)
    {
        options =
            default;

        modulePath =
            string.Empty;

        cpuJournalPath =
            string.Empty;

        gpuJournalPath =
            string.Empty;

        error =
            "Invalid Step 6H detached Guardian arguments.";

        if (args.Length < 3 ||
            args[0] !=
                "--run-combined-gate-6h")
        {
            return false;
        }

        string? confirmTarget =
            null;

        string? target =
            null;

        string? pipe =
            null;

        string? mutex =
            null;

        string? report =
            null;

        string? ready =
            null;

        string? module =
            null;

        string? moduleSha =
            null;

        string? cpuJournal =
            null;

        string? gpuJournal =
            null;

        var cpuConfirmed =
            false;

        var gpuConfirmed =
            false;

        Guid nonce =
            Guid.Empty;

        int ownerPid =
            0;

        long ownerStartTicks =
            0;

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
                    confirmTarget =
                        value;
                    break;

                case "--confirm-cpu-hardware-writes":
                    cpuConfirmed =
                        string.Equals(
                            value,
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                    break;

                case "--confirm-exclusive-gpu-controller":
                    gpuConfirmed =
                        string.Equals(
                            value,
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                    break;

                case "--module":
                    module =
                        Path.GetFullPath(
                            value);
                    break;

                case "--module-sha256":
                    moduleSha =
                        value;
                    break;

                case "--cpu-journal":
                    cpuJournal =
                        Path.GetFullPath(
                            value);
                    break;

                case "--gpu-journal":
                    gpuJournal =
                        Path.GetFullPath(
                            value);
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

                default:
                    error =
                        "Unknown Step 6H child argument: " +
                        name;

                    return false;
            }
        }

        if (!cpuConfirmed ||
            !gpuConfirmed ||
            !string.Equals(
                confirmTarget,
                TargetProfileId,
                StringComparison.Ordinal) ||
            !string.Equals(
                target,
                TargetProfileId,
                StringComparison.Ordinal) ||
            !string.Equals(
                moduleSha,
                ExpectedIntelMsrSha256,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(
                module) ||
            string.IsNullOrWhiteSpace(
                cpuJournal) ||
            string.IsNullOrWhiteSpace(
                gpuJournal) ||
            string.IsNullOrWhiteSpace(
                pipe) ||
            string.IsNullOrWhiteSpace(
                mutex) ||
            string.IsNullOrWhiteSpace(
                report) ||
            nonce ==
                Guid.Empty ||
            ownerPid <= 0 ||
            ownerStartTicks <= 0)
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

        modulePath =
            module!;

        cpuJournalPath =
            cpuJournal!;

        gpuJournalPath =
            gpuJournal!;

        return true;
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
                "Step 6H Guardian pipe closed before response.");
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

    private static void RequireExactTargetAndElevation()
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.Is64BitProcess)
        {
            throw new InvalidOperationException(
                "Step 6H requires Windows x64.");
        }

        using var identity =
            WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(
                identity)
            .IsInRole(
                WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "Step 6H requires an elevated Administrator process.");
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

        var cpuName =
            Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString",
                    null)
                ?.ToString();

        if (string.IsNullOrWhiteSpace(
                cpuName) ||
            !cpuName.Contains(
                ExpectedCpuToken,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Exact i7-13700H CPU identity is required.");
        }
    }

    private static void RequireQualifiedModule(
        string modulePath)
    {
        if (!File.Exists(
                modulePath))
        {
            throw new FileNotFoundException(
                "Qualified IntelMSR.bin is missing.",
                modulePath);
        }

        var hash =
            Convert.ToHexString(
                    SHA256.HashData(
                        File.ReadAllBytes(
                            modulePath)))
                .ToLowerInvariant();

        if (!string.Equals(
                hash,
                ExpectedIntelMsrSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "IntelMSR.bin SHA-256 does not match the physically qualified module.");
        }
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
                new UTF8Encoding(false),
                4096,
                leaveOpen:
                    true);

        writer.Write(
            json);

        writer.Flush();
        stream.Flush(
            flushToDisk:
                true);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --combined-gate-6h --confirm-target HP-8C40-9D0R1LA-F18 --confirm-cpu-hardware-writes --confirm-exclusive-gpu-controller --module <IntelMSR.bin> [--timeout-seconds 15..300] [--output-directory <path>]");
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
        string ModulePath,
        string? OutputDirectory,
        bool CpuHardwareWritesConfirmed,
        bool ExclusiveGpuControllerConfirmed);

    private sealed record CombinedPreflight(
        DateTimeOffset CapturedAtUtc,
        PerformancePowerSourceObservation Source,
        string ModuleSha256,
        string PawnIoVersion,
        int PhysicalCoreCount,
        double CpuPowerUnitWatts,
        CpuRaplPowerInfo CpuPowerInfo,
        CpuPowerLimitSnapshot[] CpuBaselineSamples,
        CpuPowerLimitApplyPlan CpuAcPlan,
        CpuPowerLimitApplyPlan CpuBatteryPlan,
        bool CpuHardwareWriteGateClosedProved,
        string GpuDeviceName,
        GpuClockBackendCapabilities GpuCapabilities,
        GpuClockBackendObservation GpuObservation,
        GpuClockBackendWriteResult GpuClosedGateProbe,
        bool GpuHardwareWriteGateClosedProved,
        bool CpuJournalPresent,
        bool GpuJournalPresent,
        bool ProductionMutexAvailable,
        bool HardwareWritesPerformed,
        string Result);

    private sealed record QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        string ModulePath,
        string ModuleSha256,
        bool ExclusiveGpuControllerConfirmed,
        CombinedPreflight ReadOnlyPreflight,
        PerformancePowerSourceObservation? BatterySeen,
        PerformancePowerSourceObservation? AcReturnSeen,
        CpuPowerSessionJournalRecord? InitialCpuJournal,
        CpuPowerSessionJournalRecord? BatteryCpuJournal,
        CpuPowerSessionJournalRecord? AcReturnCpuJournal,
        GpuClockSessionJournalRecord? InitialGpuJournal,
        GpuClockSessionJournalRecord? BatteryGpuJournal,
        GpuClockSessionJournalRecord? AcReturnGpuJournal,
        PerformanceGuardianResponse? EnableResponse,
        PerformanceGuardianResponse? DisableResponse,
        PerformanceGuardianResponse? ShutdownResponse,
        int? GuardianExitCode,
        GuardianReportView? GuardianReport,
        CpuPowerLimitSnapshot? FinalCpuSnapshot,
        bool CpuSameSession,
        bool GpuSameSession,
        bool CpuIncreasingGenerations,
        bool GpuIncreasingGenerations,
        bool CpuImmutableOriginalBaseline,
        bool FinalCpuOwnedFieldsRestored,
        string CpuJournalPath,
        bool CpuJournalPresent,
        string GpuJournalPath,
        bool GpuJournalPresent,
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
        int SourceReconciliationSignals,
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
