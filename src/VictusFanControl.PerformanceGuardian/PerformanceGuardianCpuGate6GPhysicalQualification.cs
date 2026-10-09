using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class PerformanceGuardianCpuGate6GPhysicalQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const string ExpectedCpuToken =
        "i7-13700H";

    private const string ExpectedIntelMsrSha256 =
        "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";

    private static string Executable =>
        Path.Combine(
            AppContext.BaseDirectory,
            "VictusFanControl.PerformanceGuardian.exe");

    private static string ActiveJournalPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl",
            "Performance",
            TargetProfileId,
            "cpu-power-session.json");

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
                    ActiveJournalPath))
            {
                throw new InvalidOperationException(
                    "A stale Step 6G CPU journal exists. Preserve and review it before another write qualification: " +
                    ActiveJournalPath);
            }

            var initial =
                new WindowsPerformancePowerSourceReader()
                    .Read();

            if (!initial.Succeeded ||
                initial.Source !=
                    PerformancePowerSourceKind.Ac)
            {
                throw new InvalidOperationException(
                    "Step 6G CPU physical qualification must start with AC directly confirmed.");
            }

            var preflight =
                CaptureReadOnlyPreflight(
                    options.ModulePath);

            var root =
                options.OutputDirectory ??
                Path.Combine(
                    Environment.CurrentDirectory,
                    "logs",
                    "performance-guardian-6g-cpu_" +
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

            using var owner =
                Process.GetCurrentProcess();

            var ownerStartTicks =
                owner.StartTime
                    .ToUniversalTime()
                    .Ticks;

            var nonce =
                Guid.NewGuid();

            var pipeName =
                "VictusFanControl.PerformanceGuardian.6G.Cpu." +
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
                        ActiveJournalPath,
                        options.ModulePath)) ??
                throw new IOException(
                    "Step 6G detached CPU Guardian did not start.");

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

            CpuPowerSessionJournalRecord? initialCpuJournal =
                null;

            CpuPowerSessionJournalRecord? batteryCpuJournal =
                null;

            CpuPowerSessionJournalRecord? acReturnCpuJournal =
                null;

            CpuPowerLimitSnapshot? finalSnapshot =
                null;

            Exception? failure =
                null;

            try
            {
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(
                            options.TimeoutSeconds * 3 +
                            45));

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
                    "Step 6G HELLO was not accepted.");

                enableResponse =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.EnableSession,
                            cpuEnabled: true,
                            gpuEnabled: false),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    enableResponse.Ok &&
                    enableResponse.SessionEnabled &&
                    enableResponse.CpuEnabled &&
                    !enableResponse.GpuEnabled,
                    "Step 6G CPU-only ENABLE_SESSION failed.");

                initialCpuJournal =
                    await WaitForCpuJournalRequestAsync(
                        ActiveJournalPath,
                        new CpuPowerLimitRequest(
                            CpuPowerProductDefaults.DefaultAcPl1Watts,
                            CpuPowerProductDefaults.DefaultAcPl2Watts),
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                RequireAppliedRequest(
                    initialCpuJournal,
                    preflight.PowerUnitWatts,
                    CpuPowerProductDefaults.DefaultAcPl1Watts,
                    CpuPowerProductDefaults.DefaultAcPl2Watts,
                    "initial AC");

                Console.WriteLine();
                Console.WriteLine(
                    "Step 6G CPU: AC 35/60 W reached durable Owned after exact 0x610 readback.");
                Console.WriteLine(
                    $"Disconnect the charger now. Waiting up to {options.TimeoutSeconds} seconds for Battery confirmation...");

                batterySeen =
                    await WaitForSourceAsync(
                        PerformancePowerSourceKind.Battery,
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                batteryCpuJournal =
                    await WaitForCpuJournalRequestAsync(
                        ActiveJournalPath,
                        new CpuPowerLimitRequest(
                            CpuPowerProductDefaults.DefaultBatteryPl1Watts,
                            CpuPowerProductDefaults.DefaultBatteryPl2Watts),
                        options.TimeoutSeconds,
                        timeout.Token,
                        minimumGenerationExclusive:
                            initialCpuJournal.Generation).ConfigureAwait(false);

                RequireAppliedRequest(
                    batteryCpuJournal,
                    preflight.PowerUnitWatts,
                    CpuPowerProductDefaults.DefaultBatteryPl1Watts,
                    CpuPowerProductDefaults.DefaultBatteryPl2Watts,
                    "Battery");

                Console.WriteLine(
                    "Battery confirmed and CPU journal reached durable Owned 8/15 W.");
                Console.WriteLine(
                    $"Reconnect the charger now. Waiting up to {options.TimeoutSeconds} seconds for AC confirmation...");

                acReturnSeen =
                    await WaitForSourceAsync(
                        PerformancePowerSourceKind.Ac,
                        options.TimeoutSeconds,
                        timeout.Token).ConfigureAwait(false);

                acReturnCpuJournal =
                    await WaitForCpuJournalRequestAsync(
                        ActiveJournalPath,
                        new CpuPowerLimitRequest(
                            CpuPowerProductDefaults.DefaultAcPl1Watts,
                            CpuPowerProductDefaults.DefaultAcPl2Watts),
                        options.TimeoutSeconds,
                        timeout.Token,
                        minimumGenerationExclusive:
                            batteryCpuJournal.Generation).ConfigureAwait(false);

                RequireAppliedRequest(
                    acReturnCpuJournal,
                    preflight.PowerUnitWatts,
                    CpuPowerProductDefaults.DefaultAcPl1Watts,
                    CpuPowerProductDefaults.DefaultAcPl2Watts,
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
                    "Step 6G DISABLE_SESSION did not complete normal CPU release.");

                shutdownResponse =
                    await RoundTripAsync(
                        pipe,
                        NewRequest(
                            nonce,
                            PerformanceGuardianProtocol.Shutdown),
                        timeout.Token).ConfigureAwait(false);

                Require(
                    shutdownResponse.Ok,
                    "Step 6G SHUTDOWN was not accepted.");

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

                try
                {
                    using var cleanupTimeout =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(10));

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

            var journalPresent =
                File.Exists(
                    ActiveJournalPath);

            if (guardian.HasExited)
            {
                try
                {
                    using var finalBackend =
                        new PawnIoCpuPowerLimitBackend(
                            options.ModulePath,
                            hardwareWritesAuthorized:
                                false);

                    finalSnapshot =
                        finalBackend.Read();
                }
                catch (Exception ex)
                {
                    failure ??=
                        new InvalidOperationException(
                            "Final read-only CPU baseline verification failed.",
                            ex);
                }
            }

            var sameSession =
                initialCpuJournal is not null &&
                batteryCpuJournal is not null &&
                acReturnCpuJournal is not null &&
                initialCpuJournal.SessionId ==
                    batteryCpuJournal.SessionId &&
                initialCpuJournal.SessionId ==
                    acReturnCpuJournal.SessionId;

            var increasingGenerations =
                initialCpuJournal is not null &&
                batteryCpuJournal is not null &&
                acReturnCpuJournal is not null &&
                initialCpuJournal.Generation <
                    batteryCpuJournal.Generation &&
                batteryCpuJournal.Generation <
                    acReturnCpuJournal.Generation;

            var immutableBaseline =
                initialCpuJournal is not null &&
                batteryCpuJournal is not null &&
                acReturnCpuJournal is not null &&
                initialCpuJournal.OriginalBaseline ==
                    batteryCpuJournal.OriginalBaseline &&
                initialCpuJournal.OriginalBaseline ==
                    acReturnCpuJournal.OriginalBaseline;

            var finalOwnedFieldsRestored =
                initialCpuJournal is not null &&
                finalSnapshot.HasValue &&
                CpuRaplPowerLimitCodec.OwnedFieldsMatch(
                    initialCpuJournal.OriginalBaseline.Raw,
                    finalSnapshot.Value.Raw);

            var passed =
                failure is null &&
                guardian.HasExited &&
                guardian.ExitCode == 0 &&
                enableResponse?.Ok == true &&
                disableResponse?.Ok == true &&
                shutdownResponse?.Ok == true &&
                string.Equals(
                    shutdownResponse.Code,
                    "SHUTDOWN_ACCEPTED",
                    StringComparison.Ordinal) &&
                string.Equals(
                    shutdownResponse.Phase,
                    PerformanceGuardianAuthorityPhase.Stopped.ToString(),
                    StringComparison.Ordinal) &&
                sameSession &&
                increasingGenerations &&
                immutableBaseline &&
                finalOwnedFieldsRestored &&
                guardianReport is not null &&
                guardianReport.EnableCalls == 1 &&
                guardianReport.ReleaseCalls == 1 &&
                guardianReport.AcceptedRequests == 4 &&
                guardianReport.RejectedRequests == 0 &&
                guardianReport.CpuHardwareWriteAttempts == 4 &&
                guardianReport.GpuHardwareWriteAttempts == 0 &&
                guardianReport.SourceCpuDispatchAttempts == 2 &&
                guardianReport.SourceGpuDispatchAttempts == 0 &&
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
                !journalPresent;

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
                    Initial:
                        initial,
                    ReadOnlyPreflight:
                        preflight,
                    BatterySeen:
                        batterySeen,
                    AcReturnSeen:
                        acReturnSeen,
                    InitialCpuJournal:
                        initialCpuJournal,
                    BatteryCpuJournal:
                        batteryCpuJournal,
                    AcReturnCpuJournal:
                        acReturnCpuJournal,
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
                    FinalSnapshot:
                        finalSnapshot,
                    SameSession:
                        sameSession,
                    IncreasingGenerations:
                        increasingGenerations,
                    ImmutableOriginalBaseline:
                        immutableBaseline,
                    FinalOwnedFieldsRestored:
                        finalOwnedFieldsRestored,
                    ActiveJournalPath,
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
                "Step 6G CPU qualification report: " +
                qualificationReportPath);

            if (!passed)
            {
                Console.Error.WriteLine(
                    "Step 6G CPU qualification did not close. Preserve qualification.json, guardian-report.json and any remaining CPU journal; do not rerun blindly.");

                return 6;
            }

            Console.WriteLine(
                "Step 6G CPU qualification: PASS. Detached Guardian performed AC 35/60 -> Battery 8/15 -> AC 35/60 and one final conditional restore; GPU remained disabled.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Step 6G CPU qualification: FAIL - " +
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
                out var modulePath,
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
                "Exact Step 6G target confirmation token is required.");

            return 2;
        }

        RequireExactTargetAndElevation();
        RequireQualifiedModule(
            modulePath);

        var source =
            new WindowsPerformancePowerSourceReader()
                .Read();

        if (!source.Succeeded ||
            source.Source !=
                PerformancePowerSourceKind.Ac)
        {
            throw new InvalidOperationException(
                "Step 6G child refuses hardware-write authority unless AC is directly confirmed before host startup.");
        }

        using var backend =
            new PawnIoCpuPowerLimitBackend(
                modulePath,
                hardwareWritesAuthorized:
                    true);

        if (backend.PawnIoVersion <
                new Version(
                    2,
                    2,
                    0) ||
            backend.PhysicalCoreCount !=
                14)
        {
            throw new InvalidOperationException(
                "Step 6G child requires PawnIO 2.2+ and 14 physical cores.");
        }

        if (backend.PowerInfo.MinimumWatts >
                0 &&
            backend.PowerInfo.MinimumWatts >
                CpuPowerProductDefaults.DefaultBatteryPl1Watts)
        {
            throw new InvalidOperationException(
                "Live RAPL minimum now exceeds the qualified Battery PL1; no CPU write authority is granted.");
        }

        var journal =
            new JsonCpuPowerSessionJournal(
                journalPath,
                TargetProfileId);

        using var domains =
            new QualifiedCpuGuardianDomainLifecycle(
                backend,
                journal,
                CpuPowerProductDefaults.CreateDefaultPresetSet(),
                requiredInitialSource:
                    PerformancePowerSourceKind.Ac);

        var gpuRecording =
            new RecordingGuardianGpuSourceTransitionSink();

        using var sourceRuntime =
            new GuardianPerformancePowerSourceRuntime(
                new WindowsPerformancePowerSourceReader(),
                domains,
                gpuRecording,
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
            var module =
                Path.Combine(
                    Path.GetTempPath(),
                    "IntelMSR.bin");

            var temp =
                Path.Combine(
                    Path.GetTempPath(),
                    "vfc-6g-cpu-physical-selftest");

            Require(
                TryParseOuter(
                    new[]
                    {
                        "--cpu-gate-6g",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--module",
                        module,
                        "--timeout-seconds",
                        "45",
                        "--output-directory",
                        temp
                    },
                    out var outer,
                    out _),
                "Step 6G physical outer arguments parse");

            Require(
                outer.TimeoutSeconds == 45 &&
                string.Equals(
                    outer.ModulePath,
                    Path.GetFullPath(
                        module),
                    StringComparison.OrdinalIgnoreCase),
                "Step 6G physical outer parse values");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--cpu-gate-6g",
                        "--confirm-target",
                        TargetProfileId,
                        "--module",
                        module
                    },
                    out _,
                    out _),
                "explicit CPU hardware-write confirmation is required");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--cpu-gate-6g",
                        "--confirm-target",
                        "WRONG",
                        "--confirm-cpu-hardware-writes",
                        "--module",
                        module
                    },
                    out _,
                    out _),
                "wrong target rejected");

            Require(
                !TryParseOuter(
                    new[]
                    {
                        "--cpu-gate-6g",
                        "--confirm-target",
                        TargetProfileId,
                        "--confirm-cpu-hardware-writes",
                        "--module",
                        module,
                        "--timeout-seconds",
                        "301"
                    },
                    out _,
                    out _),
                "excessive timeout rejected");

            output.WriteLine(
                "Step 6G CPU physical qualification harness self-test: PASS (argument/target/write-confirmation gates only, no PawnIO load and zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6G CPU physical qualification harness self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static CpuReadOnlyPreflight CaptureReadOnlyPreflight(
        string modulePath)
    {
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
                "Another PerformanceGuardian target writer owns the production mutex.");
        }

        mutex.ReleaseMutex();

        using var backend =
            new PawnIoCpuPowerLimitBackend(
                modulePath,
                hardwareWritesAuthorized:
                    false);

        if (backend.PawnIoVersion <
                new Version(
                    2,
                    2,
                    0) ||
            backend.PhysicalCoreCount !=
                14)
        {
            throw new InvalidOperationException(
                "Step 6G physical preflight requires PawnIO 2.2+ and 14 physical cores.");
        }

        var samples =
            new List<CpuPowerLimitSnapshot>();

        for (var index = 0;
             index < 3;
             index++)
        {
            samples.Add(
                backend.Read());

            if (index !=
                2)
            {
                Thread.Sleep(
                    100);
            }
        }

        if (samples
                .Select(
                    sample =>
                        sample.Raw)
                .Distinct()
                .Count() !=
            1)
        {
            throw new InvalidOperationException(
                "MSR 0x610 is not stable before Step 6G hardware qualification.");
        }

        var baseline =
            samples[0];

        if (baseline.Locked)
        {
            throw new InvalidOperationException(
                "MSR 0x610 lock bit is set.");
        }

        if (Math.Abs(
                baseline.Pl1Watts -
                45) >
                1e-9 ||
            Math.Abs(
                baseline.Pl2Watts -
                115) >
                1e-9)
        {
            throw new InvalidOperationException(
                $"Step 6G bounded gate requires the qualified 45/115 W baseline fields; observed {baseline.Pl1Watts:0.###}/{baseline.Pl2Watts:0.###} W.");
        }

        if (backend.PowerInfo.MinimumWatts >
                0 &&
            backend.PowerInfo.MinimumWatts >
                CpuPowerProductDefaults.DefaultBatteryPl1Watts)
        {
            throw new InvalidOperationException(
                "Live MSR 0x614 minimum is above the qualified Battery PL1.");
        }

        var presets =
            CpuPowerProductDefaults
                .CreateDefaultPresetSet();

        var acPlan =
            backend.BuildApplyPlan(
                baseline,
                presets.Ac.ToRequest());

        var batteryPlan =
            backend.BuildApplyPlan(
                baseline,
                presets.Battery.ToRequest());

        try
        {
            backend.Write(
                acPlan.RequestedRaw);

            throw new InvalidOperationException(
                "Closed CPU hardware-write gate unexpectedly returned.");
        }
        catch (InvalidOperationException ex)
            when (string.Equals(
                ex.Message,
                "CPU_POWER_BACKEND_HARDWARE_WRITE_GATE_CLOSED",
                StringComparison.Ordinal))
        {
        }

        return new CpuReadOnlyPreflight(
            PawnIoVersion:
                backend.PawnIoVersion.ToString(),
            PhysicalCoreCount:
                backend.PhysicalCoreCount,
            UnitsRaw:
                backend.UnitsRaw,
            PowerUnitWatts:
                backend.PowerUnitWatts,
            PowerInfoRaw:
                backend.PowerInfoRaw,
            PowerInfo:
                backend.PowerInfo,
            BaselineSamples:
                samples.ToArray(),
            AcPlan:
                acPlan,
            BatteryPlan:
                batteryPlan,
            HardwareWriteGateClosedProved:
                true,
            HardwareWritesPerformed:
                false);
    }

    private static void RequireAppliedRequest(
        CpuPowerSessionJournalRecord record,
        double powerUnitWatts,
        double expectedPl1,
        double expectedPl2,
        string label)
    {
        var applied =
            CpuRaplPowerLimitCodec.DecodeSnapshot(
                record.AppliedRaw,
                powerUnitWatts);

        Require(
            record.Phase ==
                CpuPowerJournalPhase.Owned &&
            !record.PendingRaw.HasValue &&
            Math.Abs(
                record.Request.Pl1Watts -
                expectedPl1) <
                1e-9 &&
            Math.Abs(
                record.Request.Pl2Watts -
                expectedPl2) <
                1e-9 &&
            Math.Abs(
                applied.Pl1Watts -
                expectedPl1) <
                1e-9 &&
            Math.Abs(
                applied.Pl2Watts -
                expectedPl2) <
                1e-9,
            "Step 6G " +
            label +
            " journal/applied PL fields do not match the expected request.");
    }

    private static async Task<CpuPowerSessionJournalRecord> WaitForCpuJournalRequestAsync(
        string journalPath,
        CpuPowerLimitRequest expected,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        long minimumGenerationExclusive = 0)
    {
        var journal =
            new JsonCpuPowerSessionJournal(
                journalPath,
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
                    "Step 6G CPU session left clean Owned state while waiting for " +
                    expected +
                    ": " +
                    last.Phase);
            }

            await Task.Delay(
                50,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Timed out waiting for durable CPU Owned request " +
            expected.Pl1Watts +
            "/" +
            expected.Pl2Watts +
            " W. Last phase=" +
            (last?.Phase.ToString() ?? "none") +
            ", generation=" +
            (last?.Generation.ToString() ?? "none") +
            ".");
    }

    private static ProcessStartInfo NewGuardianStart(
        string pipeName,
        string mutexName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks,
        string reportPath,
        string readyPath,
        string journalPath,
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
            "--run-cpu-gate-6g");

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
            "--module",
            modulePath);

        Add(
            start,
            "--module-sha256",
            ExpectedIntelMsrSha256);

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

    private static bool TryParseOuter(
        string[] args,
        out OuterOptions options,
        out string error)
    {
        options =
            default;

        error =
            "Invalid Step 6G CPU physical qualification arguments.";

        if (args.Length < 5 ||
            args[0] !=
                "--cpu-gate-6g")
        {
            return false;
        }

        string? confirm =
            null;

        string? module =
            null;

        var writesConfirmed =
            false;

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

                case "--confirm-cpu-hardware-writes":
                    writesConfirmed =
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
                    outputDirectory =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    error =
                        "Unknown or incomplete Step 6G CPU argument: " +
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

        if (!writesConfirmed)
        {
            error =
                "--confirm-cpu-hardware-writes is required for the bounded physical CPU qualification.";

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
                "TimeoutSeconds must be between 15 and 300.";

            return false;
        }

        options =
            new OuterOptions(
                timeoutSeconds,
                module!,
                outputDirectory);

        return true;
    }

    private static bool TryParseGuardian(
        string[] args,
        out GuardianHostOptions options,
        out string journalPath,
        out string modulePath,
        out string confirmTarget,
        out string error)
    {
        options =
            default;

        journalPath =
            string.Empty;

        modulePath =
            string.Empty;

        confirmTarget =
            string.Empty;

        error =
            "Invalid Step 6G detached CPU Guardian arguments.";

        if (args.Length < 3 ||
            args[0] !=
                "--run-cpu-gate-6g")
        {
            return false;
        }

        string? confirm =
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

        string? journal =
            null;

        string? module =
            null;

        string? moduleSha =
            null;

        Guid nonce =
            Guid.Empty;

        int ownerPid =
            0;

        long ownerStartTicks =
            0;

        var writesConfirmed =
            false;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            if (index + 1 >=
                args.Length)
            {
                error =
                    "Incomplete Step 6G detached CPU Guardian argument.";

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

                case "--confirm-cpu-hardware-writes":
                    writesConfirmed =
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
                    error =
                        "Unknown Step 6G detached CPU Guardian argument: " +
                        name;

                    return false;
            }
        }

        if (!writesConfirmed ||
            !string.Equals(
                confirm,
                TargetProfileId,
                StringComparison.Ordinal) ||
            !string.Equals(
                target,
                TargetProfileId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                pipe) ||
            string.IsNullOrWhiteSpace(
                mutex) ||
            nonce ==
                Guid.Empty ||
            ownerPid <= 0 ||
            ownerStartTicks <= 0 ||
            string.IsNullOrWhiteSpace(
                report) ||
            string.IsNullOrWhiteSpace(
                journal) ||
            string.IsNullOrWhiteSpace(
                module) ||
            !string.Equals(
                moduleSha,
                ExpectedIntelMsrSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                Path.GetFullPath(
                    journal!),
                Path.GetFullPath(
                    ActiveJournalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Detached Step 6G CPU Guardian requires the target-scoped active journal path.";

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

        modulePath =
            module!;

        confirmTarget =
            confirm!;

        return true;
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
                "Step 6G Guardian pipe closed before response.");
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
                "Step 6G hardware qualification requires an elevated Administrator process.");
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

        if (!string.Equals(
                Path.GetFileName(
                    modulePath),
                "IntelMSR.bin",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Step 6G requires the qualified IntelMSR.bin module.");
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
                "IntelMSR.bin SHA-256 does not match the physically preflighted module.");
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
            "VictusFanControl.PerformanceGuardian --cpu-gate-6g --confirm-target HP-8C40-9D0R1LA-F18 --confirm-cpu-hardware-writes --module <IntelMSR.bin> [--timeout-seconds 15..300] [--output-directory <path>]");
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
        string? OutputDirectory);

    private sealed record CpuReadOnlyPreflight(
        string PawnIoVersion,
        int PhysicalCoreCount,
        ulong UnitsRaw,
        double PowerUnitWatts,
        ulong PowerInfoRaw,
        CpuRaplPowerInfo PowerInfo,
        CpuPowerLimitSnapshot[] BaselineSamples,
        CpuPowerLimitApplyPlan AcPlan,
        CpuPowerLimitApplyPlan BatteryPlan,
        bool HardwareWriteGateClosedProved,
        bool HardwareWritesPerformed);

    private sealed record QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        string ModulePath,
        string ModuleSha256,
        PerformancePowerSourceObservation Initial,
        CpuReadOnlyPreflight ReadOnlyPreflight,
        PerformancePowerSourceObservation? BatterySeen,
        PerformancePowerSourceObservation? AcReturnSeen,
        CpuPowerSessionJournalRecord? InitialCpuJournal,
        CpuPowerSessionJournalRecord? BatteryCpuJournal,
        CpuPowerSessionJournalRecord? AcReturnCpuJournal,
        PerformanceGuardianResponse? EnableResponse,
        PerformanceGuardianResponse? DisableResponse,
        PerformanceGuardianResponse? ShutdownResponse,
        int? GuardianExitCode,
        GuardianReportView? GuardianReport,
        CpuPowerLimitSnapshot? FinalSnapshot,
        bool SameSession,
        bool IncreasingGenerations,
        bool ImmutableOriginalBaseline,
        bool FinalOwnedFieldsRestored,
        string ActiveJournalPath,
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
