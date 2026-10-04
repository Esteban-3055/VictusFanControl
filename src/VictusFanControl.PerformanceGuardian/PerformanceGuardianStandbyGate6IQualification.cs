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
/// Step 6I: physical Modern Standby qualification for the already-qualified
/// combined CPU+GPU Guardian.
///
/// Exact HP 8C40 policy is inherited from the physically qualified M6 fan
/// lifecycle characterization:
///   SESSION_DISPLAY_STATUS Off -> proactive release
///   PBT_APMSUSPEND             -> fallback/confirmation only
///   resume broadcasts Off      -> never reacquire
///   SESSION_DISPLAY_STATUS On  -> fresh source query + reacquire
///
/// This remains an explicit qualification-only entry point.
/// </summary>
internal static class PerformanceGuardianStandbyGate6IQualification
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
                    "Step 6I requires absent CPU/GPU journals. Recover any prior qualification state before Modern Standby testing.");
            }

            var root =
                options.OutputDirectory ??
                Path.Combine(
                    Environment.CurrentDirectory,
                    "logs",
                    "performance-guardian-6i-standby_" +
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

            var lifecycleReportPath =
                Path.Combine(
                    root,
                    "standby-lifecycle-report.json");

            var preSleepPath =
                Path.Combine(
                    root,
                    "standby-presleep.json");

            var resumePath =
                Path.Combine(
                    root,
                    "standby-resume.json");

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
                "Step 6I read-only preflight: PASS. AC confirmed; CPU/GPU gates closed.");

            using var owner =
                Process.GetCurrentProcess();

            var ownerStartTicks =
                owner.StartTime
                    .ToUniversalTime()
                    .Ticks;

            var nonce =
                Guid.NewGuid();

            var pipeName =
                "VictusFanControl.PerformanceGuardian.6I.Standby." +
                Guid.NewGuid().ToString("N");

            var startedAt =
                DateTimeOffset.UtcNow;

            using var guardian =
                Process.Start(
                    NewGuardianStart(
                        pipeName,
                        PerformanceGuardianHost.ProductionMutexName(
                            TargetProfileId),
                        nonce,
                        owner.Id,
                        ownerStartTicks,
                        guardianReportPath,
                        lifecycleReportPath,
                        preSleepPath,
                        resumePath,
                        readyPath,
                        options.ModulePath)) ??
                throw new IOException(
                    "Step 6I detached Guardian did not start.");

            PerformanceGuardianResponse? enableResponse =
                null;

            PerformanceGuardianResponse? disableResponse =
                null;

            PerformanceGuardianResponse? shutdownResponse =
                null;

            CpuPowerSessionJournalRecord? initialCpu =
                null;

            GpuClockSessionJournalRecord? initialGpu =
                null;

            CpuPowerSessionJournalRecord? resumedCpu =
                null;

            GpuClockSessionJournalRecord? resumedGpu =
                null;

            StandbyBoundaryEvidence? preSleepEvidence =
                null;

            StandbyBoundaryEvidence? resumeEvidence =
                null;

            GuardianModernStandbyLifecycleSnapshot? lifecycle =
                null;

            GuardianReportView? guardianReport =
                null;

            CpuPowerLimitSnapshot? finalCpu =
                null;

            Exception? failure =
                null;

            try
            {
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(20));

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
                    "Step 6I HELLO was not accepted.");

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
                    "Step 6I combined ENABLE_SESSION failed.");

                initialCpu =
                    await WaitForCpuJournalAsync(
                        new CpuPowerLimitRequest(
                            35,
                            60),
                        timeout.Token).ConfigureAwait(false);

                initialGpu =
                    await WaitForGpuJournalAsync(
                        new GpuClockLimitRequest(
                            210,
                            1850),
                        timeout.Token).ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine(
                    "STEP 6I READY: CPU 35/60 W + GPU 210..1850 MHz are active.");
                Console.WriteLine(
                    "Keep the charger CONNECTED.");
                Console.WriteLine(
                    "Choose Windows Start -> Power -> Sleep now.");
                Console.WriteLine(
                    "Leave the notebook in Modern Standby for at least 30 seconds, then wake it normally.");
                Console.WriteLine(
                    "Do not close/kill this PowerShell or any VictusFanControl.PerformanceGuardian process.");

                await WaitForFileAsync(
                    preSleepPath,
                    timeout.Token).ConfigureAwait(false);

                preSleepEvidence =
                    JsonSerializer.Deserialize<StandbyBoundaryEvidence>(
                        File.ReadAllText(
                            preSleepPath));

                Require(
                    preSleepEvidence is not null,
                    "Step 6I pre-sleep evidence could not be parsed.");

                await WaitForFileAsync(
                    resumePath,
                    timeout.Token).ConfigureAwait(false);

                resumeEvidence =
                    JsonSerializer.Deserialize<StandbyBoundaryEvidence>(
                        File.ReadAllText(
                            resumePath));

                Require(
                    resumeEvidence is not null,
                    "Step 6I resume evidence could not be parsed.");

                var sleepInterval =
                    resumeEvidence!.CapturedAtUtc -
                    preSleepEvidence!.CapturedAtUtc;

                Require(
                    sleepInterval >=
                        TimeSpan.FromSeconds(15),
                    "Step 6I requires at least 15 seconds between display-Off release and display-On reacquire.");

                resumedCpu =
                    await WaitForCpuJournalAsync(
                        new CpuPowerLimitRequest(
                            35,
                            60),
                        timeout.Token,
                        excludedSessionId:
                            initialCpu.SessionId).ConfigureAwait(false);

                resumedGpu =
                    await WaitForGpuJournalAsync(
                        new GpuClockLimitRequest(
                            210,
                            1850),
                        timeout.Token,
                        excludedSessionId:
                            initialGpu.SessionId).ConfigureAwait(false);

                Console.WriteLine(
                    "Step 6I: display-On reacquire confirmed with fresh CPU/GPU sessions.");

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
                    "Step 6I final DISABLE_SESSION failed.");

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
                        shutdownResponse.Phase,
                        PerformanceGuardianAuthorityPhase.Stopped.ToString(),
                        StringComparison.Ordinal),
                    "Step 6I final SHUTDOWN did not reach Stopped.");

                await guardian.WaitForExitAsync(
                    timeout.Token).ConfigureAwait(false);

                await WaitForFileAsync(
                    guardianReportPath,
                    timeout.Token).ConfigureAwait(false);

                await WaitForFileAsync(
                    lifecycleReportPath,
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
                                TimeSpan.FromSeconds(15));

                        await guardian.WaitForExitAsync(
                            exitTimeout.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }

            if (File.Exists(
                    guardianReportPath))
            {
                guardianReport =
                    JsonSerializer.Deserialize<GuardianReportView>(
                        File.ReadAllText(
                            guardianReportPath));
            }

            if (File.Exists(
                    lifecycleReportPath))
            {
                lifecycle =
                    JsonSerializer.Deserialize<GuardianModernStandbyLifecycleSnapshot>(
                        File.ReadAllText(
                            lifecycleReportPath));
            }

            if (guardian.HasExited)
            {
                try
                {
                    using var cpu =
                        new PawnIoCpuPowerLimitBackend(
                            options.ModulePath,
                            hardwareWritesAuthorized:
                                false);

                    finalCpu =
                        cpu.Read();
                }
                catch (Exception ex)
                {
                    failure ??=
                        new InvalidOperationException(
                            "Step 6I final CPU readback failed.",
                            ex);
                }
            }

            var cpuJournalPresent =
                File.Exists(
                    CpuJournalPath);

            var gpuJournalPresent =
                File.Exists(
                    GpuJournalPath);

            var freshCpuSession =
                initialCpu is not null &&
                resumedCpu is not null &&
                initialCpu.SessionId !=
                    resumedCpu.SessionId;

            var freshGpuSession =
                initialGpu is not null &&
                resumedGpu is not null &&
                initialGpu.SessionId !=
                    resumedGpu.SessionId;

            var finalCpuRestored =
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
                preSleepEvidence is not null &&
                resumeEvidence is not null &&
                preSleepEvidence.Boundary ==
                    "SESSION_DISPLAY_STATUS_OFF" &&
                preSleepEvidence.Lifecycle.PrimaryDisplayReleaseCompleted &&
                preSleepEvidence.Lifecycle.SuspendReleaseSuccesses == 1 &&
                !preSleepEvidence.CpuJournalPresent &&
                !preSleepEvidence.GpuJournalPresent &&
                preSleepEvidence.CpuSnapshot.Pl1Watts == 45 &&
                preSleepEvidence.CpuSnapshot.Pl2Watts == 115 &&
                resumeEvidence.Boundary ==
                    "SESSION_DISPLAY_STATUS_ON" &&
                resumeEvidence.Lifecycle.ResumeReacquireSuccesses == 1 &&
                resumeEvidence.CpuJournalPresent &&
                resumeEvidence.GpuJournalPresent &&
                freshCpuSession &&
                freshGpuSession &&
                lifecycle.HasValue &&
                lifecycle.Value.PrimaryDisplayReleaseCompleted &&
                lifecycle.Value.SuspendReleaseAttempts == 1 &&
                lifecycle.Value.SuspendReleaseSuccesses == 1 &&
                lifecycle.Value.SuspendFallbackReleaseAttempts == 0 &&
                lifecycle.Value.SuspendSignals >= 1 &&
                lifecycle.Value.ResumeReacquireAttempts == 1 &&
                lifecycle.Value.ResumeReacquireSuccesses == 1 &&
                lifecycle.Value.MaintenanceResumeSignalsSuppressed >= 1 &&
                lifecycle.Value.Failure is null &&
                guardianReport is not null &&
                guardianReport.AcceptedRequests == 4 &&
                guardianReport.RejectedRequests == 0 &&
                guardianReport.EnableCalls == 2 &&
                guardianReport.ReleaseCalls == 2 &&
                guardianReport.CpuHardwareWriteAttempts == 4 &&
                guardianReport.GpuHardwareWriteAttempts == 4 &&
                guardianReport.SourceRuntimeStartCalls == 2 &&
                guardianReport.SourceRuntimeStopCalls == 2 &&
                guardianReport.SourceListenerRegistrations == 2 &&
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
                guardianReport.SourceFailure is null &&
                guardianReport.Failure is null &&
                finalCpuRestored &&
                !cpuJournalPresent &&
                !gpuJournalPresent;

            var report =
                new QualificationReport(
                    SchemaVersion: 1,
                    TargetProfileId,
                    StartedAtUtc:
                        startedAt,
                    CompletedAtUtc:
                        DateTimeOffset.UtcNow,
                    ReadOnlyPreflight:
                        preflight,
                    InitialCpuJournal:
                        initialCpu,
                    InitialGpuJournal:
                        initialGpu,
                    PreSleep:
                        preSleepEvidence,
                    Resume:
                        resumeEvidence,
                    ResumedCpuJournal:
                        resumedCpu,
                    ResumedGpuJournal:
                        resumedGpu,
                    Lifecycle:
                        lifecycle,
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
                    FreshCpuSessionAfterResume:
                        freshCpuSession,
                    FreshGpuSessionAfterResume:
                        freshGpuSession,
                    FinalCpuOwnedFieldsRestored:
                        finalCpuRestored,
                    CpuJournalPresent:
                        cpuJournalPresent,
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
                "Step 6I standby qualification report: " +
                qualificationPath);

            if (!passed)
            {
                Console.Error.WriteLine(
                    "Step 6I standby qualification did not close. Preserve the entire evidence directory and any remaining journals.");

                return 6;
            }

            Console.WriteLine(
                "Step 6I standby qualification: PASS. Display-Off released CPU/GPU before Modern Standby; maintenance wake did not reacquire; display-On created fresh CPU/GPU sessions; final cleanup restored defaults.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Step 6I standby qualification: FAIL - " +
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
                out var lifecycleReportPath,
                out var preSleepPath,
                out var resumePath,
                out var error))
        {
            Console.Error.WriteLine(
                error);

            return 2;
        }

        RequireExactTargetAndElevation();
        RequireQualifiedModule(
            modulePath);

        if (File.Exists(
                CpuJournalPath) ||
            File.Exists(
                GpuJournalPath))
        {
            throw new InvalidOperationException(
                "Step 6I child refuses hardware authority while a persistent CPU/GPU journal exists.");
        }

        var source =
            new WindowsPerformancePowerSourceReader()
                .Read();

        if (!source.Succeeded ||
            source.Source !=
                PerformancePowerSourceKind.Ac)
        {
            throw new InvalidOperationException(
                "Step 6I must start with directly confirmed AC.");
        }

        using var cpuBackend =
            new PawnIoCpuPowerLimitBackend(
                modulePath,
                hardwareWritesAuthorized:
                    true);

        var baseline =
            cpuBackend.Read();

        if (baseline.Locked ||
            baseline.Pl1Watts != 45 ||
            baseline.Pl2Watts != 115)
        {
            throw new InvalidOperationException(
                "Step 6I CPU baseline must be the qualified unlocked 45/115 W state.");
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
                "Step 6I requires the complete qualified NVML Set/Reset surface.");
        }

        var cpuDomain =
            new QualifiedCpuGuardianDomainLifecycle(
                cpuBackend,
                new JsonCpuPowerSessionJournal(
                    CpuJournalPath,
                    TargetProfileId),
                CpuPowerProductDefaults.CreateDefaultPresetSet(),
                requiredInitialSource:
                    PerformancePowerSourceKind.Ac);

        var gpuDomain =
            new QualifiedGpuGuardianDomainLifecycle(
                gpuBackend,
                new JsonGpuClockSessionJournal(
                    GpuJournalPath,
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

        using var innerSource =
            new GuardianPerformancePowerSourceRuntime(
                new WindowsPerformancePowerSourceReader(),
                combined,
                combined,
                new WindowsGuardianPowerSourceNotificationListenerFactory());

        GuardianModernStandbyLifecycleRuntime? standby =
            null;

        void WriteBoundaryEvidence(
            GuardianModernStandbyEvidence evidence)
        {
            var document =
                new StandbyBoundaryEvidence(
                    SchemaVersion: 1,
                    Boundary:
                        evidence.Boundary,
                    CapturedAtUtc:
                        evidence.CapturedAtUtc,
                    Lifecycle:
                        evidence.Lifecycle,
                    Domains:
                        evidence.Domains,
                    Source:
                        evidence.Source,
                    CpuSnapshot:
                        cpuBackend.Read(),
                    GpuObservation:
                        gpuBackend.ReadObservation(),
                    CpuJournalPresent:
                        File.Exists(
                            CpuJournalPath),
                    GpuJournalPresent:
                        File.Exists(
                            GpuJournalPath));

            if (evidence.Boundary ==
                "SESSION_DISPLAY_STATUS_OFF")
            {
                DurableJson(
                    preSleepPath,
                    document);
            }
            else if (evidence.Boundary ==
                     "SESSION_DISPLAY_STATUS_ON")
            {
                DurableJson(
                    resumePath,
                    document);
            }
        }

        standby =
            new GuardianModernStandbyLifecycleRuntime(
                innerSource,
                combined,
                new WindowsGuardianModernStandbyNotificationListenerFactory(),
                WriteBoundaryEvidence);

        using (standby)
        {
            var host =
                new PerformanceGuardianHost(
                    options,
                    combined,
                    standby);

            try
            {
                return await host.RunAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    DurableJson(
                        lifecycleReportPath,
                        standby.StandbySnapshot);
                }
                catch
                {
                }
            }
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

            Require(
                TryParseOuter(
                    new[]
                    {
                        "--standby-gate-6i",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--confirm-exclusive-gpu-controller",
                        "--confirm-modern-standby",
                        "--module",
                        module
                    },
                    out var options,
                    out _),
                "Step 6I exact bounded outer arguments parse");

            Require(
                options.CpuHardwareWritesConfirmed &&
                options.ExclusiveGpuControllerConfirmed &&
                options.ModernStandbyConfirmed,
                "Step 6I all three explicit confirmations are required");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--standby-gate-6i",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--confirm-exclusive-gpu-controller",
                        "--module",
                        module
                    },
                    out _,
                    out _),
                "Step 6I refuses missing Modern Standby confirmation");

            output.WriteLine(
                "Step 6I standby physical harness self-test: PASS (argument/target/explicit-write/exclusive-controller/standby gates only, zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6I standby physical harness self-test: FAIL - " +
                ex);

            return 1;
        }
    }

    private static StandbyPreflight CaptureReadOnlyPreflight(
        string modulePath)
    {
        var source =
            new WindowsPerformancePowerSourceReader()
                .Read();

        if (!source.Succeeded ||
            source.Source !=
                PerformancePowerSourceKind.Ac)
        {
            throw new InvalidOperationException(
                "Step 6I preflight requires directly confirmed AC.");
        }

        using var mutex =
            new Mutex(
                initiallyOwned:
                    true,
                PerformanceGuardianHost.ProductionMutexName(
                    TargetProfileId),
                out var createdNew);

        if (!createdNew)
        {
            throw new InvalidOperationException(
                "Another PerformanceGuardian owns the target mutex.");
        }

        mutex.ReleaseMutex();

        using var cpu =
            new PawnIoCpuPowerLimitBackend(
                modulePath,
                hardwareWritesAuthorized:
                    false);

        var baseline =
            cpu.Read();

        if (cpu.PawnIoVersion <
                new Version(
                    2,
                    2,
                    0) ||
            cpu.PhysicalCoreCount !=
                14 ||
            baseline.Locked ||
            baseline.Pl1Watts != 45 ||
            baseline.Pl2Watts != 115)
        {
            throw new InvalidOperationException(
                "Step 6I CPU preflight requires qualified PawnIO/14-core/unlocked 45/115 baseline.");
        }

        var cpuGateClosed =
            false;

        try
        {
            cpu.Write(
                cpu.BuildApplyPlan(
                        baseline,
                        new CpuPowerLimitRequest(
                            35,
                            60))
                    .RequestedRaw);
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
                "Step 6I could not prove the CPU write gate closed.");
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

        var observation =
            gpu.ReadObservation();

        var closedProbe =
            gpu.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1850));

        if (!gpu.Capabilities.HasCompleteCommandSurface ||
            !observation.Succeeded ||
            closedProbe.Succeeded ||
            closedProbe.FailureKind !=
                GpuClockBackendFailureKind.WriteGateClosed)
        {
            throw new InvalidOperationException(
                "Step 6I GPU read-only preflight failed.");
        }

        return new StandbyPreflight(
            CapturedAtUtc:
                DateTimeOffset.UtcNow,
            Source:
                source,
            PawnIoVersion:
                cpu.PawnIoVersion.ToString(),
            PhysicalCoreCount:
                cpu.PhysicalCoreCount,
            CpuBaseline:
                baseline,
            CpuWriteGateClosed:
                true,
            GpuDeviceName:
                nvml.DeviceName,
            GpuObservation:
                observation,
            GpuWriteGateClosed:
                true,
            CpuJournalPresent:
                File.Exists(
                    CpuJournalPath),
            GpuJournalPresent:
                File.Exists(
                    GpuJournalPath),
            ProductionMutexAvailable:
                true,
            HardwareWritesPerformed:
                false,
            Result:
                "PASS");
    }

    private static async Task<CpuPowerSessionJournalRecord> WaitForCpuJournalAsync(
        CpuPowerLimitRequest expected,
        CancellationToken cancellationToken,
        Guid? excludedSessionId = null)
    {
        var journal =
            new JsonCpuPowerSessionJournal(
                CpuJournalPath,
                TargetProfileId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var record =
                    journal.Load();

                if (record is not null &&
                    record.Phase ==
                        CpuPowerJournalPhase.Owned &&
                    record.Request ==
                        expected &&
                    !record.PendingRaw.HasValue &&
                    (!excludedSessionId.HasValue ||
                     record.SessionId !=
                        excludedSessionId.Value))
                {
                    return record;
                }

                if (record is not null &&
                    record.Phase is
                        CpuPowerJournalPhase.Yielded or
                        CpuPowerJournalPhase.Unresolved)
                {
                    throw new InvalidOperationException(
                        "Step 6I CPU journal entered " +
                        record.Phase +
                        ".");
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(
                50,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<GpuClockSessionJournalRecord> WaitForGpuJournalAsync(
        GpuClockLimitRequest expected,
        CancellationToken cancellationToken,
        Guid? excludedSessionId = null)
    {
        var journal =
            new JsonGpuClockSessionJournal(
                GpuJournalPath,
                TargetProfileId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var record =
                    journal.Load();

                if (record is not null &&
                    record.Phase ==
                        GpuClockJournalPhase.ActiveUnverified &&
                    record.CommittedRequest ==
                        expected &&
                    !record.PendingRequest.HasValue &&
                    (!excludedSessionId.HasValue ||
                     record.SessionId !=
                        excludedSessionId.Value))
                {
                    return record;
                }

                if (record is not null &&
                    record.Phase ==
                        GpuClockJournalPhase.RecoveryRequired)
                {
                    throw new InvalidOperationException(
                        "Step 6I GPU journal entered RecoveryRequired.");
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(
                50,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo NewGuardianStart(
        string pipeName,
        string mutexName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks,
        string guardianReportPath,
        string lifecycleReportPath,
        string preSleepPath,
        string resumePath,
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
            "--run-standby-gate-6i");

        Add(start, "--confirm-target", TargetProfileId);
        Add(start, "--confirm-cpu-hardware-writes", "true");
        Add(start, "--confirm-exclusive-gpu-controller", "true");
        Add(start, "--confirm-modern-standby", "true");
        Add(start, "--module", modulePath);
        Add(start, "--module-sha256", ExpectedIntelMsrSha256);
        Add(start, "--target", TargetProfileId);
        Add(start, "--pipe", pipeName);
        Add(start, "--mutex", mutexName);
        Add(start, "--nonce", nonce.ToString("D"));
        Add(start, "--owner-pid", ownerPid.ToString());
        Add(start, "--owner-start-ticks", ownerStartTicks.ToString());
        Add(start, "--report", guardianReportPath);
        Add(start, "--ready", readyPath);
        Add(start, "--lifecycle-report", lifecycleReportPath);
        Add(start, "--presleep", preSleepPath);
        Add(start, "--resume", resumePath);

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
            "Invalid Step 6I standby qualification arguments.";

        if (args.Length < 8 ||
            args[0] !=
                "--standby-gate-6i")
        {
            return false;
        }

        string? target =
            null;

        string? module =
            null;

        string? output =
            null;

        var cpu =
            false;

        var gpu =
            false;

        var standby =
            false;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--confirm-target"
                    when index + 1 < args.Length:
                    target =
                        args[++index];
                    break;

                case "--confirm-cpu-hardware-writes":
                    cpu =
                        true;
                    break;

                case "--confirm-exclusive-gpu-controller":
                    gpu =
                        true;
                    break;

                case "--confirm-modern-standby":
                    standby =
                        true;
                    break;

                case "--module"
                    when index + 1 < args.Length:
                    module =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                case "--output-directory"
                    when index + 1 < args.Length:
                    output =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    return false;
            }
        }

        if (!string.Equals(
                target,
                TargetProfileId,
                StringComparison.Ordinal) ||
            !cpu ||
            !gpu ||
            !standby ||
            string.IsNullOrWhiteSpace(
                module))
        {
            return false;
        }

        options =
            new OuterOptions(
                module!,
                output,
                cpu,
                gpu,
                standby);

        return true;
    }

    private static bool TryParseGuardian(
        string[] args,
        out GuardianHostOptions options,
        out string modulePath,
        out string lifecycleReportPath,
        out string preSleepPath,
        out string resumePath,
        out string error)
    {
        options =
            default;

        modulePath =
            string.Empty;

        lifecycleReportPath =
            string.Empty;

        preSleepPath =
            string.Empty;

        resumePath =
            string.Empty;

        error =
            "Invalid Step 6I child arguments.";

        if (args.Length < 3 ||
            args[0] !=
                "--run-standby-gate-6i")
        {
            return false;
        }

        var values =
            new Dictionary<string, string>(
                StringComparer.Ordinal);

        for (var index = 1;
             index < args.Length;
             index += 2)
        {
            if (index + 1 >=
                args.Length)
            {
                return false;
            }

            values[args[index]] =
                args[index + 1];
        }

        string Get(
            string name) =>
            values.TryGetValue(
                name,
                out var value)
                ? value
                : string.Empty;

        if (!string.Equals(
                Get("--confirm-target"),
                TargetProfileId,
                StringComparison.Ordinal) ||
            !string.Equals(
                Get("--confirm-cpu-hardware-writes"),
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Get("--confirm-exclusive-gpu-controller"),
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Get("--confirm-modern-standby"),
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Get("--target"),
                TargetProfileId,
                StringComparison.Ordinal) ||
            !string.Equals(
                Get("--module-sha256"),
                ExpectedIntelMsrSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(
                Get("--nonce"),
                out var nonce) ||
            !int.TryParse(
                Get("--owner-pid"),
                out var ownerPid) ||
            !long.TryParse(
                Get("--owner-start-ticks"),
                out var ownerTicks) ||
            ownerPid <= 0 ||
            ownerTicks <= 0)
        {
            return false;
        }

        var module =
            Get("--module");

        var pipe =
            Get("--pipe");

        var mutex =
            Get("--mutex");

        var report =
            Get("--report");

        var ready =
            Get("--ready");

        var lifecycleReport =
            Get("--lifecycle-report");

        var presleep =
            Get("--presleep");

        var resume =
            Get("--resume");

        if (string.IsNullOrWhiteSpace(module) ||
            string.IsNullOrWhiteSpace(pipe) ||
            string.IsNullOrWhiteSpace(mutex) ||
            string.IsNullOrWhiteSpace(report) ||
            string.IsNullOrWhiteSpace(lifecycleReport) ||
            string.IsNullOrWhiteSpace(presleep) ||
            string.IsNullOrWhiteSpace(resume))
        {
            return false;
        }

        options =
            new GuardianHostOptions(
                TargetProfileId,
                pipe,
                mutex,
                nonce,
                ownerPid,
                ownerTicks,
                Path.GetFullPath(
                    report),
                string.IsNullOrWhiteSpace(
                    ready)
                    ? null
                    : Path.GetFullPath(
                        ready));

        modulePath =
            Path.GetFullPath(
                module);

        lifecycleReportPath =
            Path.GetFullPath(
                lifecycleReport);

        preSleepPath =
            Path.GetFullPath(
                presleep);

        resumePath =
            Path.GetFullPath(
                resume);

        return true;
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
                    TimeSpan.FromSeconds(15));

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
                "Step 6I Guardian pipe closed before response.");
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
                50,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequireExactTargetAndElevation()
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.Is64BitProcess)
        {
            throw new InvalidOperationException(
                "Step 6I requires Windows x64.");
        }

        using var identity =
            WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(
                identity)
            .IsInRole(
                WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "Step 6I requires elevation.");
        }

        var hardware =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var reason))
        {
            throw new InvalidOperationException(
                "Step 6I exact target mismatch: " +
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
                "Step 6I requires the exact i7-13700H.");
        }
    }

    private static void RequireQualifiedModule(
        string modulePath)
    {
        if (!File.Exists(
                modulePath))
        {
            throw new FileNotFoundException(
                "Step 6I IntelMSR.bin is missing.",
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
                "Step 6I IntelMSR.bin SHA-256 mismatch.");
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
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

        writer.Flush();
        stream.Flush(
            flushToDisk:
                true);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --standby-gate-6i --confirm-target HP-8C40-9D0R1LA-F18 --confirm-cpu-hardware-writes --confirm-exclusive-gpu-controller --confirm-modern-standby --module <IntelMSR.bin> [--output-directory <path>]");
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
        string ModulePath,
        string? OutputDirectory,
        bool CpuHardwareWritesConfirmed,
        bool ExclusiveGpuControllerConfirmed,
        bool ModernStandbyConfirmed);

    private sealed record StandbyPreflight(
        DateTimeOffset CapturedAtUtc,
        PerformancePowerSourceObservation Source,
        string PawnIoVersion,
        int PhysicalCoreCount,
        CpuPowerLimitSnapshot CpuBaseline,
        bool CpuWriteGateClosed,
        string GpuDeviceName,
        GpuClockBackendObservation GpuObservation,
        bool GpuWriteGateClosed,
        bool CpuJournalPresent,
        bool GpuJournalPresent,
        bool ProductionMutexAvailable,
        bool HardwareWritesPerformed,
        string Result);

    private sealed record StandbyBoundaryEvidence(
        int SchemaVersion,
        string Boundary,
        DateTimeOffset CapturedAtUtc,
        GuardianModernStandbyLifecycleSnapshot Lifecycle,
        GuardianDomainLifecycleSnapshot Domains,
        GuardianPowerSourceRuntimeSnapshot Source,
        CpuPowerLimitSnapshot CpuSnapshot,
        GpuClockBackendObservation GpuObservation,
        bool CpuJournalPresent,
        bool GpuJournalPresent);

    private sealed record QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        StandbyPreflight ReadOnlyPreflight,
        CpuPowerSessionJournalRecord? InitialCpuJournal,
        GpuClockSessionJournalRecord? InitialGpuJournal,
        StandbyBoundaryEvidence? PreSleep,
        StandbyBoundaryEvidence? Resume,
        CpuPowerSessionJournalRecord? ResumedCpuJournal,
        GpuClockSessionJournalRecord? ResumedGpuJournal,
        GuardianModernStandbyLifecycleSnapshot? Lifecycle,
        PerformanceGuardianResponse? EnableResponse,
        PerformanceGuardianResponse? DisableResponse,
        PerformanceGuardianResponse? ShutdownResponse,
        int? GuardianExitCode,
        GuardianReportView? GuardianReport,
        CpuPowerLimitSnapshot? FinalCpuSnapshot,
        bool FreshCpuSessionAfterResume,
        bool FreshGpuSessionAfterResume,
        bool FinalCpuOwnedFieldsRestored,
        bool CpuJournalPresent,
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
