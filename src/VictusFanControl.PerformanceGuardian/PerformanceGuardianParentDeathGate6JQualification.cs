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
/// Step 6J: destructive parent/owner-death qualification for the already
/// qualified combined CPU+GPU Guardian.
///
/// A controller process launches a disposable owner process. That owner starts
/// the exact Step 6H combined Guardian child, establishes HELLO + ENABLE_SESSION
/// and waits after CPU 35/60 W + GPU 210..1850 MHz are durably active. The
/// controller then kills ONLY the owner process (never the Guardian process).
///
/// The detached Guardian must observe its real parent Process handle, stop the
/// source listener, restore CPU through the live owned-field path, Reset GPU
/// through the still-live exclusive session, mark ParentLost and exit.
///
/// This tests an application/owner crash. It deliberately does NOT claim safe
/// automatic recovery after the Guardian process itself is killed; GPU stale
/// journals remain fail-closed for that separate case.
/// </summary>
internal static class PerformanceGuardianParentDeathGate6JQualification
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
                    "Step 6J requires a clean start with both CPU/GPU journals absent.");
            }

            var root =
                options.OutputDirectory ??
                Path.Combine(
                    Environment.CurrentDirectory,
                    "logs",
                    "performance-guardian-6j-parent-death_" +
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

            var ownerReadyPath =
                Path.Combine(
                    root,
                    "owner-ready.json");

            var guardianReadyPath =
                Path.Combine(
                    root,
                    "guardian-ready.txt");

            var guardianReportPath =
                Path.Combine(
                    root,
                    "guardian-report.json");

            var prekillPath =
                Path.Combine(
                    root,
                    "prekill.json");

            var qualificationPath =
                Path.Combine(
                    root,
                    "qualification.json");

            var preflight =
                CaptureReadOnlyPreflight(
                    options.ModulePath);

            DurableJson(
                preflightPath,
                preflight);

            Console.WriteLine(
                "Step 6J read-only preflight: PASS. Starting disposable owner process.");

            var startedAt =
                DateTimeOffset.UtcNow;

            using var owner =
                Process.Start(
                    NewOwnerStart(
                        options.ModulePath,
                        ownerReadyPath,
                        guardianReadyPath,
                        guardianReportPath)) ??
                throw new IOException(
                    "Step 6J disposable owner did not start.");

            ParentDeathOwnerReady? ownerReady =
                null;

            ParentDeathPrekillEvidence? prekill =
                null;

            GuardianReportView? guardianReport =
                null;

            CpuPowerLimitSnapshot? finalCpu =
                null;

            GpuClockBackendObservation? finalGpuObservation =
                null;

            bool mutexAvailableAfterCleanup =
                false;

            bool guardianExited =
                false;

            int? ownerExitCode =
                null;

            Exception? failure =
                null;

            try
            {
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(
                            options.TimeoutSeconds));

                await WaitForFileAsync(
                    ownerReadyPath,
                    timeout.Token).ConfigureAwait(false);

                ownerReady =
                    JsonSerializer.Deserialize<ParentDeathOwnerReady>(
                        File.ReadAllText(
                            ownerReadyPath));

                Require(
                    ownerReady is not null,
                    "Step 6J owner-ready evidence could not be parsed.");

                Require(
                    ownerReady!.OwnerPid ==
                        owner.Id &&
                    ownerReady.EnableResponse.Ok &&
                    ownerReady.EnableResponse.SessionEnabled &&
                    ownerReady.EnableResponse.CpuEnabled &&
                    ownerReady.EnableResponse.GpuEnabled,
                    "Step 6J disposable owner did not establish combined Guardian authority.");

                var currentSource =
                    new WindowsPerformancePowerSourceReader()
                        .Read();

                Require(
                    currentSource.Succeeded &&
                    currentSource.Source ==
                        PerformancePowerSourceKind.Ac,
                    "Step 6J must remain on AC before the destructive owner kill.");

                using (var cpuRead =
                       new PawnIoCpuPowerLimitBackend(
                           options.ModulePath,
                           hardwareWritesAuthorized:
                               false))
                {
                    var cpuSnapshot =
                        cpuRead.Read();

                    Require(
                        cpuSnapshot.Pl1Watts == 35 &&
                        cpuSnapshot.Pl2Watts == 60 &&
                        !cpuSnapshot.Locked,
                        "Step 6J pre-kill CPU state is not the qualified AC 35/60 W state.");

                    using var nvml =
                        new NvmlClient(
                            ExpectedGpuName,
                            requirePreferredDevice:
                                true);

                    var gpuRead =
                        new NvmlGpuClockLimitBackend(
                            nvml,
                            hardwareWritesAuthorized:
                                false);

                    var gpuObservation =
                        gpuRead.ReadObservation();

                    Require(
                        gpuObservation.Succeeded,
                        "Step 6J pre-kill GPU observation failed.");

                    var liveCpuJournal =
                        new JsonCpuPowerSessionJournal(
                            CpuJournalPath,
                            TargetProfileId)
                            .Load();

                    var liveGpuJournal =
                        new JsonGpuClockSessionJournal(
                            GpuJournalPath,
                            TargetProfileId)
                            .Load();

                    Require(
                        liveCpuJournal is not null &&
                        liveCpuJournal.Phase ==
                            CpuPowerJournalPhase.Owned &&
                        liveCpuJournal.Request ==
                            new CpuPowerLimitRequest(
                                35,
                                60) &&
                        !liveCpuJournal.PendingRaw.HasValue,
                        "Step 6J live CPU journal is not clean Owned 35/60.");

                    Require(
                        liveGpuJournal is not null &&
                        liveGpuJournal.Phase ==
                            GpuClockJournalPhase.ActiveUnverified &&
                        liveGpuJournal.CommittedRequest ==
                            new GpuClockLimitRequest(
                                210,
                                1850) &&
                        !liveGpuJournal.PendingRequest.HasValue,
                        "Step 6J live GPU journal is not clean ActiveUnverified 210..1850.");

                    prekill =
                        new ParentDeathPrekillEvidence(
                            SchemaVersion:
                                1,
                            CapturedAtUtc:
                                DateTimeOffset.UtcNow,
                            Source:
                                currentSource,
                            OwnerPid:
                                ownerReady.OwnerPid,
                            OwnerStartUtcTicks:
                                ownerReady.OwnerStartUtcTicks,
                            GuardianPid:
                                ownerReady.GuardianPid,
                            CpuSnapshot:
                                cpuSnapshot,
                            GpuObservation:
                                gpuObservation,
                            CpuJournal:
                                liveCpuJournal!,
                            GpuJournal:
                                liveGpuJournal!,
                            HardwareWritesPerformedByController:
                                false);

                    DurableJson(
                        prekillPath,
                        prekill);
                }

                Console.WriteLine();
                Console.WriteLine(
                    "Step 6J ARMED: disposable owner is live while CPU 35/60 W + GPU 210..1850 MHz are active.");
                Console.WriteLine(
                    "Killing ONLY the disposable owner process now. The detached Guardian must detect parent loss and clean both domains.");

                owner.Kill(
                    entireProcessTree:
                        false);

                await owner.WaitForExitAsync(
                    timeout.Token).ConfigureAwait(false);

                ownerExitCode =
                    owner.ExitCode;

                await WaitForFileAsync(
                    guardianReportPath,
                    timeout.Token).ConfigureAwait(false);

                guardianReport =
                    JsonSerializer.Deserialize<GuardianReportView>(
                        File.ReadAllText(
                            guardianReportPath));

                Require(
                    guardianReport is not null,
                    "Step 6J Guardian report could not be parsed.");

                guardianExited =
                    await WaitForProcessExitAsync(
                        ownerReady.GuardianPid,
                        timeout.Token).ConfigureAwait(false);

                await WaitForJournalsAbsentAsync(
                    timeout.Token).ConfigureAwait(false);

                mutexAvailableAfterCleanup =
                    await WaitForMutexAvailableAsync(
                        timeout.Token).ConfigureAwait(false);

                using (var cpuRead =
                       new PawnIoCpuPowerLimitBackend(
                           options.ModulePath,
                           hardwareWritesAuthorized:
                               false))
                {
                    finalCpu =
                        cpuRead.Read();
                }

                using (var nvml =
                       new NvmlClient(
                           ExpectedGpuName,
                           requirePreferredDevice:
                               true))
                {
                    var gpuRead =
                        new NvmlGpuClockLimitBackend(
                            nvml,
                            hardwareWritesAuthorized:
                                false);

                    finalGpuObservation =
                        gpuRead.ReadObservation();
                }
            }
            catch (Exception ex)
            {
                failure =
                    ex;

                if (!owner.HasExited)
                {
                    try
                    {
                        owner.Kill(
                            entireProcessTree:
                                false);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    using var cleanupWait =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(20));

                    await owner.WaitForExitAsync(
                        cleanupWait.Token).ConfigureAwait(false);
                }
                catch
                {
                }

                if (File.Exists(
                        guardianReportPath))
                {
                    try
                    {
                        guardianReport =
                            JsonSerializer.Deserialize<GuardianReportView>(
                                File.ReadAllText(
                                    guardianReportPath));
                    }
                    catch
                    {
                    }
                }
            }

            var cpuJournalPresent =
                File.Exists(
                    CpuJournalPath);

            var gpuJournalPresent =
                File.Exists(
                    GpuJournalPath);

            var finalCpuRestored =
                prekill is not null &&
                finalCpu.HasValue &&
                CpuRaplPowerLimitCodec.OwnedFieldsMatch(
                    prekill.CpuJournal.OriginalBaseline.Raw,
                    finalCpu.Value.Raw);

            var passed =
                failure is null &&
                ownerReady is not null &&
                prekill is not null &&
                owner.HasExited &&
                guardianExited &&
                guardianReport is not null &&
                guardianReport.ParentLostDetected &&
                string.Equals(
                    guardianReport.ExitReason,
                    "PARENT_LOST_RELEASED",
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.FinalPhase,
                    PerformanceGuardianAuthorityPhase.ParentLost.ToString(),
                    StringComparison.Ordinal) &&
                guardianReport.Connections == 1 &&
                guardianReport.AcceptedRequests == 2 &&
                guardianReport.RejectedRequests == 0 &&
                guardianReport.EnableCalls == 1 &&
                guardianReport.ReleaseCalls == 1 &&
                string.Equals(
                    guardianReport.LastReleaseReason,
                    "PARENT_PROCESS_EXIT",
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.InitialSource,
                    PerformancePowerSourceKind.Ac.ToString(),
                    StringComparison.Ordinal) &&
                guardianReport.CpuHardwareWriteAttempts == 2 &&
                guardianReport.GpuHardwareWriteAttempts == 2 &&
                string.Equals(
                    guardianReport.CpuDomainState,
                    CpuPowerLimiterState.Disabled.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.GpuDomainState,
                    GpuClockSessionState.Disabled.ToString(),
                    StringComparison.Ordinal) &&
                guardianReport.SourceRuntimeStartCalls == 1 &&
                guardianReport.SourceRuntimeStopCalls == 1 &&
                guardianReport.SourceListenerRegistrations == 1 &&
                guardianReport.SourceCpuDispatchAttempts == 0 &&
                guardianReport.SourceGpuDispatchAttempts == 0 &&
                string.Equals(
                    guardianReport.SourceLastSource,
                    PerformancePowerSourceKind.Ac.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    guardianReport.SourceLastStopReason,
                    "PARENT_PROCESS_EXIT",
                    StringComparison.Ordinal) &&
                guardianReport.SourceFailure is null &&
                guardianReport.Failure is null &&
                guardianReport.HardwareWritesPerformed &&
                finalCpuRestored &&
                finalCpu.HasValue &&
                finalCpu.Value.Pl1Watts == 45 &&
                finalCpu.Value.Pl2Watts == 115 &&
                !finalCpu.Value.Locked &&
                finalGpuObservation.HasValue &&
                finalGpuObservation.Value.Succeeded &&
                !cpuJournalPresent &&
                !gpuJournalPresent &&
                mutexAvailableAfterCleanup;

            var report =
                new QualificationReport(
                    SchemaVersion:
                        1,
                    TargetProfileId,
                    StartedAtUtc:
                        startedAt,
                    CompletedAtUtc:
                        DateTimeOffset.UtcNow,
                    ReadOnlyPreflight:
                        preflight,
                    OwnerReady:
                        ownerReady,
                    PreKill:
                        prekill,
                    OwnerExitCode:
                        ownerExitCode,
                    GuardianExited:
                        guardianExited,
                    GuardianReport:
                        guardianReport,
                    FinalCpuSnapshot:
                        finalCpu,
                    FinalGpuObservation:
                        finalGpuObservation,
                    FinalCpuOwnedFieldsRestored:
                        finalCpuRestored,
                    CpuJournalPresent:
                        cpuJournalPresent,
                    GpuJournalPresent:
                        gpuJournalPresent,
                    ProductionMutexAvailableAfterCleanup:
                        mutexAvailableAfterCleanup,
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
                "Step 6J parent-death qualification report: " +
                qualificationPath);

            if (!passed)
            {
                Console.Error.WriteLine(
                    "Step 6J parent-death qualification did not close. Preserve the complete evidence directory and any remaining journal.");

                return 6;
            }

            Console.WriteLine(
                "Step 6J parent-death qualification: PASS. Owner crash was detected by the detached Guardian; CPU was restored, GPU was Reset, both journals were removed and authority ended in ParentLost.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Step 6J parent-death qualification: FAIL - " +
                ex);

            return 5;
        }
    }

    internal static async Task<int> RunDisposableOwnerAsync(
        string[] args)
    {
        if (!TryParseOwner(
                args,
                out var options,
                out var error))
        {
            Console.Error.WriteLine(
                error);

            return 2;
        }

        RequireExactTargetAndElevation();
        RequireQualifiedModule(
            options.ModulePath);

        if (File.Exists(
                CpuJournalPath) ||
            File.Exists(
                GpuJournalPath))
        {
            throw new InvalidOperationException(
                "Step 6J disposable owner refuses to start with a persistent journal.");
        }

        var source =
            new WindowsPerformancePowerSourceReader()
                .Read();

        if (!source.Succeeded ||
            source.Source !=
                PerformancePowerSourceKind.Ac)
        {
            throw new InvalidOperationException(
                "Step 6J disposable owner requires directly confirmed AC.");
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
            "VictusFanControl.PerformanceGuardian.6J.ParentDeath." +
            Guid.NewGuid().ToString("N");

        using var guardian =
            Process.Start(
                NewGuardianStart(
                    pipeName,
                    nonce,
                    owner.Id,
                    ownerStartTicks,
                    options.GuardianReportPath,
                    options.GuardianReadyPath,
                    options.ModulePath)) ??
            throw new IOException(
                "Step 6J disposable owner could not start detached Guardian.");

        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(30));

        await WaitForFileAsync(
            options.GuardianReadyPath,
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
            "Step 6J disposable owner HELLO failed.");

        var enable =
            await RoundTripAsync(
                pipe,
                NewRequest(
                    nonce,
                    PerformanceGuardianProtocol.EnableSession,
                    cpuEnabled:
                        true,
                    gpuEnabled:
                        true),
                timeout.Token).ConfigureAwait(false);

        Require(
            enable.Ok &&
            enable.SessionEnabled &&
            enable.CpuEnabled &&
            enable.GpuEnabled,
            "Step 6J disposable owner combined ENABLE_SESSION failed.");

        var cpuJournal =
            await WaitForCpuJournalAsync(
                timeout.Token).ConfigureAwait(false);

        var gpuJournal =
            await WaitForGpuJournalAsync(
                timeout.Token).ConfigureAwait(false);

        var ready =
            new ParentDeathOwnerReady(
                SchemaVersion:
                    1,
                CapturedAtUtc:
                    DateTimeOffset.UtcNow,
                OwnerPid:
                    owner.Id,
                OwnerStartUtcTicks:
                    ownerStartTicks,
                GuardianPid:
                    guardian.Id,
                Source:
                    source,
                EnableResponse:
                    enable,
                CpuJournal:
                    cpuJournal,
                GpuJournal:
                    gpuJournal);

        DurableJson(
            options.OwnerReadyPath,
            ready);

        // Intentional infinite owner lifetime. The controller terminates this
        // process with Process.Kill(false). No Disable/Shutdown is sent here.
        await Task.Delay(
            Timeout.InfiniteTimeSpan);

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

            Require(
                TryParseOuter(
                    new[]
                    {
                        "--parent-death-gate-6j",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--confirm-exclusive-gpu-controller",
                        "--confirm-owner-process-kill",
                        "--module",
                        module,
                        "--timeout-seconds",
                        "90"
                    },
                    out var outer,
                    out _),
                "Step 6J exact outer contract parses");

            Require(
                outer.CpuHardwareWritesConfirmed &&
                outer.ExclusiveGpuControllerConfirmed &&
                outer.OwnerProcessKillConfirmed &&
                outer.TimeoutSeconds == 90,
                "Step 6J requires all destructive qualifications");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--parent-death-gate-6j",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--confirm-exclusive-gpu-controller",
                        "--module",
                        module
                    },
                    out _,
                    out _),
                "Step 6J refuses missing owner-kill confirmation");

            output.WriteLine(
                "Step 6J parent-death harness self-test: PASS (exact target + CPU writes + exclusive GPU + destructive owner-kill confirmation; zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6J parent-death harness self-test: FAIL - " +
                ex);

            return 1;
        }
    }

    private static ParentDeathPreflight CaptureReadOnlyPreflight(
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
                "Step 6J preflight requires directly confirmed AC.");
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
                    "Step 6J target mutex is already owned.");
            }

            mutex.ReleaseMutex();
        }

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
            baseline.Pl1Watts !=
                45 ||
            baseline.Pl2Watts !=
                115)
        {
            throw new InvalidOperationException(
                "Step 6J CPU preflight requires qualified PawnIO/14-core/unlocked 45/115 baseline.");
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
                "Step 6J could not prove CPU write gate closed.");
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

        var gpuObservation =
            gpu.ReadObservation();

        var closedProbe =
            gpu.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1850));

        if (!gpu.Capabilities.HasCompleteCommandSurface ||
            !gpuObservation.Succeeded ||
            closedProbe.Succeeded ||
            closedProbe.FailureKind !=
                GpuClockBackendFailureKind.WriteGateClosed)
        {
            throw new InvalidOperationException(
                "Step 6J GPU read-only preflight failed.");
        }

        return new ParentDeathPreflight(
            SchemaVersion:
                1,
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
                gpuObservation,
            GpuWriteGateClosed:
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

    private static ProcessStartInfo NewOwnerStart(
        string modulePath,
        string ownerReadyPath,
        string guardianReadyPath,
        string guardianReportPath)
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
            "--run-parent-death-owner-6j");

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
            "--confirm-owner-process-kill",
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
            "--owner-ready",
            ownerReadyPath);

        Add(
            start,
            "--guardian-ready",
            guardianReadyPath);

        Add(
            start,
            "--guardian-report",
            guardianReportPath);

        return start;
    }

    private static ProcessStartInfo NewGuardianStart(
        string pipeName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks,
        string guardianReportPath,
        string guardianReadyPath,
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

        // Reuse the exact already-qualified Step 6H combined Guardian child.
        start.ArgumentList.Add(
            "--run-combined-gate-6h");

        Add(start, "--confirm-target", TargetProfileId);
        Add(start, "--confirm-cpu-hardware-writes", "true");
        Add(start, "--confirm-exclusive-gpu-controller", "true");
        Add(start, "--module", modulePath);
        Add(start, "--module-sha256", ExpectedIntelMsrSha256);
        Add(start, "--cpu-journal", CpuJournalPath);
        Add(start, "--gpu-journal", GpuJournalPath);
        Add(start, "--target", TargetProfileId);
        Add(start, "--pipe", pipeName);
        Add(
            start,
            "--mutex",
            PerformanceGuardianHost.ProductionMutexName(
                TargetProfileId));
        Add(start, "--nonce", nonce.ToString("D"));
        Add(start, "--owner-pid", ownerPid.ToString());
        Add(start, "--owner-start-ticks", ownerStartTicks.ToString());
        Add(start, "--report", guardianReportPath);
        Add(start, "--ready", guardianReadyPath);

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
            "Invalid Step 6J parent-death qualification arguments.";

        if (args.Length < 9 ||
            args[0] !=
                "--parent-death-gate-6j")
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

        var kill =
            false;

        var timeoutSeconds =
            90;

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

                case "--confirm-owner-process-kill":
                    kill =
                        true;
                    break;

                case "--module"
                    when index + 1 < args.Length:
                    module =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                case "--timeout-seconds"
                    when index + 1 < args.Length &&
                         int.TryParse(
                             args[index + 1],
                             out var parsed):
                    index++;
                    timeoutSeconds =
                        parsed;
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
            !kill ||
            string.IsNullOrWhiteSpace(
                module) ||
            timeoutSeconds is < 30 or > 300)
        {
            return false;
        }

        options =
            new OuterOptions(
                module!,
                output,
                timeoutSeconds,
                cpu,
                gpu,
                kill);

        return true;
    }

    private static bool TryParseOwner(
        string[] args,
        out OwnerOptions options,
        out string error)
    {
        options =
            default;

        error =
            "Invalid Step 6J disposable-owner arguments.";

        if (args.Length < 3 ||
            args[0] !=
                "--run-parent-death-owner-6j")
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
            string key) =>
            values.TryGetValue(
                key,
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
                Get("--confirm-owner-process-kill"),
                "true",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Get("--module-sha256"),
                ExpectedIntelMsrSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var module =
            Get("--module");

        var ownerReady =
            Get("--owner-ready");

        var guardianReady =
            Get("--guardian-ready");

        var guardianReport =
            Get("--guardian-report");

        if (string.IsNullOrWhiteSpace(module) ||
            string.IsNullOrWhiteSpace(ownerReady) ||
            string.IsNullOrWhiteSpace(guardianReady) ||
            string.IsNullOrWhiteSpace(guardianReport))
        {
            return false;
        }

        options =
            new OwnerOptions(
                Path.GetFullPath(
                    module),
                Path.GetFullPath(
                    ownerReady),
                Path.GetFullPath(
                    guardianReady),
                Path.GetFullPath(
                    guardianReport));

        return true;
    }

    private static async Task<CpuPowerSessionJournalRecord>
        WaitForCpuJournalAsync(
            CancellationToken cancellationToken)
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
                        new CpuPowerLimitRequest(
                            35,
                            60) &&
                    !record.PendingRaw.HasValue)
                {
                    return record;
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

    private static async Task<GpuClockSessionJournalRecord>
        WaitForGpuJournalAsync(
            CancellationToken cancellationToken)
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
                        new GpuClockLimitRequest(
                            210,
                            1850) &&
                    !record.PendingRequest.HasValue)
                {
                    return record;
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

    private static async Task WaitForJournalsAbsentAsync(
        CancellationToken cancellationToken)
    {
        while (File.Exists(
                   CpuJournalPath) ||
               File.Exists(
                   GpuJournalPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(
                50,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process =
                Process.GetProcessById(
                    processId);

            await process.WaitForExitAsync(
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<bool> WaitForMutexAvailableAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var mutex =
                new Mutex(
                    initiallyOwned:
                        true,
                    PerformanceGuardianHost.ProductionMutexName(
                        TargetProfileId),
                    out var createdNew);

            if (createdNew)
            {
                mutex.ReleaseMutex();
                return true;
            }

            await Task.Delay(
                50,
                cancellationToken).ConfigureAwait(false);
        }
    }

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
                "Step 6J Guardian pipe closed before response.");
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

    private static void RequireExactTargetAndElevation()
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.Is64BitProcess)
        {
            throw new InvalidOperationException(
                "Step 6J requires Windows x64.");
        }

        using var identity =
            WindowsIdentity.GetCurrent();

        if (!new WindowsPrincipal(
                identity)
            .IsInRole(
                WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "Step 6J requires elevation.");
        }

        var hardware =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var reason))
        {
            throw new InvalidOperationException(
                "Step 6J exact target mismatch: " +
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
                "Step 6J requires the exact i7-13700H.");
        }
    }

    private static void RequireQualifiedModule(
        string modulePath)
    {
        if (!File.Exists(
                modulePath))
        {
            throw new FileNotFoundException(
                "Step 6J IntelMSR.bin is missing.",
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
                "Step 6J IntelMSR.bin SHA-256 mismatch.");
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
            "VictusFanControl.PerformanceGuardian --parent-death-gate-6j --confirm-target HP-8C40-9D0R1LA-F18 --confirm-cpu-hardware-writes --confirm-exclusive-gpu-controller --confirm-owner-process-kill --module <IntelMSR.bin> [--timeout-seconds 30..300] [--output-directory <path>]");
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
        int TimeoutSeconds,
        bool CpuHardwareWritesConfirmed,
        bool ExclusiveGpuControllerConfirmed,
        bool OwnerProcessKillConfirmed);

    private readonly record struct OwnerOptions(
        string ModulePath,
        string OwnerReadyPath,
        string GuardianReadyPath,
        string GuardianReportPath);

    private sealed record ParentDeathPreflight(
        int SchemaVersion,
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

    private sealed record ParentDeathOwnerReady(
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        int OwnerPid,
        long OwnerStartUtcTicks,
        int GuardianPid,
        PerformancePowerSourceObservation Source,
        PerformanceGuardianResponse EnableResponse,
        CpuPowerSessionJournalRecord CpuJournal,
        GpuClockSessionJournalRecord GpuJournal);

    private sealed record ParentDeathPrekillEvidence(
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        PerformancePowerSourceObservation Source,
        int OwnerPid,
        long OwnerStartUtcTicks,
        int GuardianPid,
        CpuPowerLimitSnapshot CpuSnapshot,
        GpuClockBackendObservation GpuObservation,
        CpuPowerSessionJournalRecord CpuJournal,
        GpuClockSessionJournalRecord GpuJournal,
        bool HardwareWritesPerformedByController);

    private sealed record QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        ParentDeathPreflight ReadOnlyPreflight,
        ParentDeathOwnerReady? OwnerReady,
        ParentDeathPrekillEvidence? PreKill,
        int? OwnerExitCode,
        bool GuardianExited,
        GuardianReportView? GuardianReport,
        CpuPowerLimitSnapshot? FinalCpuSnapshot,
        GpuClockBackendObservation? FinalGpuObservation,
        bool FinalCpuOwnedFieldsRestored,
        bool CpuJournalPresent,
        bool GpuJournalPresent,
        bool ProductionMutexAvailableAfterCleanup,
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
