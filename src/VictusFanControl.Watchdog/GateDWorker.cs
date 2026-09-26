using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Persistent Gate D watchdog service. It never issues ordinary 14..50 fan
/// targets. Its only hardware write authority is the validated
/// FF,FF -> LegacyDefault restore primitive owned by WatchdogLeaseManager.
/// </summary>
internal sealed class GateDWorker : BackgroundService
{
    private static readonly TimeSpan DeadlinePollInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly WatchdogOptions _options;

    public GateDWorker(WatchdogOptions options)
    {
        _options = options;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var log = new WatchdogFileLog(
            _options.LogDirectory,
            "watchdog-gate-d");

        var process = Process.GetCurrentProcess();
        var windowsIdentity = WindowsIdentity.GetCurrent();
        var accountName =
            windowsIdentity?.Name ??
            Environment.UserName;

        WatchdogLeaseManager? manager = null;
        var journalPath =
            Path.Combine(
                Path.GetDirectoryName(_options.ResultPath) ??
                throw new InvalidOperationException(
                    "Gate D result path has no parent directory."),
                "lease.json");

        try
        {
            log.Write(
                $"GATE D START pid={process.Id}; session={process.SessionId}; " +
                $"account={accountName}; modules={_options.ModulesDirectory}; " +
                $"journal={journalPath}; pipe={FanControlWatchdogLeaseContract.PipeName}");

            EnsureServiceContext(
                process,
                windowsIdentity);

            var hardwareIdentity =
                HardwareIdentityReader.ReadCurrent();

            if (!Hp88F8TargetProfile.Matches(
                    hardwareIdentity,
                    out var reason))
            {
                throw new InvalidOperationException(
                    $"Exact HP 88F8 target fingerprint refused: {reason}");
            }

            var modulePath =
                Path.Combine(
                    _options.ModulesDirectory,
                    "LpcACPIEC.bin");

            if (!File.Exists(modulePath))
            {
                throw new FileNotFoundException(
                    "Gate D requires the signed LpcACPIEC.bin module.",
                    modulePath);
            }

            var leaseHardware =
                new GateDLeaseHardware(
                    _options.ModulesDirectory);

            manager =
                new WatchdogLeaseManager(
                    new JsonLeaseJournal(journalPath),
                    leaseHardware,
                    new WindowsMonotonicClock());

            var startupRecovery =
                await manager.RecoverOnStartupAsync(
                    stoppingToken).ConfigureAwait(false);

            var startupReady =
                IsReadyDisposition(
                    startupRecovery.Disposition);

            WriteStatus(
                hardwareIdentity,
                process,
                accountName,
                journalPath,
                ready: startupReady,
                blocked: !startupReady,
                recoveryDisposition:
                    startupRecovery.Disposition.ToString(),
                detail: startupRecovery.Detail);

            log.Write(
                $"GATE D STARTUP RECOVERY disposition={startupRecovery.Disposition}; " +
                $"observed={startupRecovery.Observed}; restoreAttempted={startupRecovery.RestoreAttempted}; " +
                $"journalRetained={startupRecovery.JournalRetained}; detail={startupRecovery.Detail}");

            using var serviceLoopCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);

            var acceptTask =
                RunAcceptLoopAsync(
                    manager,
                    log,
                    serviceLoopCts.Token);

            var deadlineTask =
                RunDeadlineLoopAsync(
                    manager,
                    hardwareIdentity,
                    process,
                    accountName,
                    journalPath,
                    log,
                    serviceLoopCts.Token);

            var completed =
                await Task.WhenAny(
                    acceptTask,
                    deadlineTask).ConfigureAwait(false);

            if (!stoppingToken.IsCancellationRequested)
            {
                // Either loop ending unexpectedly is a service failure. Cancel
                // its sibling before propagating the completed task result so
                // the Windows Service host can exit non-zero and SCM recovery
                // can restart it.
                serviceLoopCts.Cancel();

                try
                {
                    await Task.WhenAll(
                        acceptTask,
                        deadlineTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The sibling loop is expected to observe our cancellation.
                    // Await the original completed task below for the real fault.
                }

                await completed.ConfigureAwait(false);

                throw new InvalidOperationException(
                    "Gate D service loop ended unexpectedly without a stop request.");
            }

            serviceLoopCts.Cancel();

            try
            {
                await Task.WhenAll(
                    acceptTask,
                    deadlineTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            log.Write("GATE D cancellation requested.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 96;

            try
            {
                log.Write(
                    $"GATE D FATAL: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            if (manager is not null)
            {
                try
                {
                    var stopRecovery =
                        await manager.RecoverForServiceStopAsync(
                            "watchdog service stop/update",
                            CancellationToken.None).ConfigureAwait(false);

                    if (stopRecovery is not null)
                    {
                        log.Write(
                            $"GATE D STOP RECOVERY disposition={stopRecovery.Disposition}; " +
                            $"observed={stopRecovery.Observed}; restoreAttempted={stopRecovery.RestoreAttempted}; " +
                            $"journalRetained={stopRecovery.JournalRetained}; detail={stopRecovery.Detail}");
                    }
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 97;
                    log.Write(
                        $"GATE D STOP RECOVERY FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }

            log.Write(
                $"GATE D STOP exitCode={Environment.ExitCode}.");
        }
    }

    private async Task RunAcceptLoopAsync(
        WatchdogLeaseManager manager,
        WatchdogFileLog log,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe =
                GateDPipeFactory.Create();

            log.Write(
                "GATE D PIPE waiting for one elevated local controller.");

            await GateCPipeServerSession.RunAsync(
                pipe,
                manager,
                stoppingToken,
                log.Write,
                monitorControllerProcess: true).ConfigureAwait(false);
        }
    }

    private async Task RunDeadlineLoopAsync(
        WatchdogLeaseManager manager,
        HardwareIdentity hardwareIdentity,
        Process process,
        string accountName,
        string journalPath,
        WatchdogFileLog log,
        CancellationToken stoppingToken)
    {
        Process? monitoredProcess = null;
        ControllerIdentity? monitoredIdentity = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(
                    DeadlinePollInterval,
                    stoppingToken).ConfigureAwait(false);

                LeaseRecoveryResult? recovery = null;

                try
                {
                    var activeController =
                        await manager.GetActiveControllerAsync(
                            stoppingToken).ConfigureAwait(false);

                    if (activeController is null)
                    {
                        monitoredProcess?.Dispose();
                        monitoredProcess = null;
                        monitoredIdentity = null;
                    }
                    else
                    {
                        if (monitoredIdentity != activeController ||
                            monitoredProcess is null)
                        {
                            monitoredProcess?.Dispose();
                            monitoredProcess = null;
                            monitoredIdentity = activeController;

                            try
                            {
                                var candidate =
                                    Process.GetProcessById(
                                        activeController.ProcessId);

                                var startTicks =
                                    candidate.StartTime
                                        .ToUniversalTime()
                                        .Ticks;

                                if (startTicks !=
                                    activeController.ProcessStartUtcTicks)
                                {
                                    candidate.Dispose();

                                    recovery =
                                        await manager.HandleOwnerLossAsync(
                                            activeController,
                                            "controller PID was reused / creation time changed",
                                            stoppingToken).ConfigureAwait(false);
                                }
                                else if (candidate.HasExited)
                                {
                                    candidate.Dispose();

                                    recovery =
                                        await manager.HandleOwnerLossAsync(
                                            activeController,
                                            "controller process exited",
                                            stoppingToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    monitoredProcess = candidate;

                                    log.Write(
                                        $"GATE D OWNER MONITOR armed PID={activeController.ProcessId}; " +
                                        $"startTicks={activeController.ProcessStartUtcTicks}.");
                                }
                            }
                            catch (ArgumentException)
                            {
                                recovery =
                                    await manager.HandleOwnerLossAsync(
                                        activeController,
                                        "controller process no longer exists",
                                        stoppingToken).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException)
                            {
                                recovery =
                                    await manager.HandleOwnerLossAsync(
                                        activeController,
                                        "controller process became unavailable",
                                        stoppingToken).ConfigureAwait(false);
                            }
                        }
                        else if (monitoredProcess.HasExited)
                        {
                            monitoredProcess.Dispose();
                            monitoredProcess = null;

                            recovery =
                                await manager.HandleOwnerLossAsync(
                                    activeController,
                                    "controller process exited",
                                    stoppingToken).ConfigureAwait(false);
                        }
                    }

                    recovery ??=
                        await manager.CheckDeadlinesAsync(
                            stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    WriteStatus(
                        hardwareIdentity,
                        process,
                        accountName,
                        journalPath,
                        ready: false,
                        blocked: true,
                        recoveryDisposition:
                            "DeadlineOrProcessMonitorFailure",
                        detail:
                            $"{ex.GetType().Name}: {ex.Message}");

                    log.Write(
                        $"GATE D MONITOR FAIL: {ex.GetType().Name}: {ex.Message}");

                    throw;
                }

                if (recovery is null)
                {
                    continue;
                }

                var ready =
                    IsReadyDisposition(
                        recovery.Disposition);

                WriteStatus(
                    hardwareIdentity,
                    process,
                    accountName,
                    journalPath,
                    ready,
                    blocked: !ready,
                    recoveryDisposition:
                        recovery.Disposition.ToString(),
                    detail: recovery.Detail);

                log.Write(
                    $"GATE D RECOVERY disposition={recovery.Disposition}; " +
                    $"observed={recovery.Observed}; restoreAttempted={recovery.RestoreAttempted}; " +
                    $"journalRetained={recovery.JournalRetained}; detail={recovery.Detail}");

                if (recovery.JournalRetained)
                {
                    // Retained evidence means the condition is unresolved.
                    // Avoid tight-loop EC/WMI retries while preserving the
                    // durable ownership record for the next bounded attempt.
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            monitoredProcess?.Dispose();
        }
    }

    private void WriteStatus(
        HardwareIdentity hardware,
        Process process,
        string accountName,
        string journalPath,
        bool ready,
        bool blocked,
        string? recoveryDisposition,
        string detail)
    {
        AtomicJsonFile.Write(
            _options.ResultPath,
            new GateDServiceStatus(
                Ready: ready,
                Blocked: blocked,
                Timestamp: DateTimeOffset.Now,
                ProcessId: process.Id,
                SessionId: process.SessionId,
                AccountName: accountName,
                Hardware: hardware,
                RecoveryDisposition: recoveryDisposition,
                Detail: detail,
                JournalPath: journalPath,
                PipeName:
                    FanControlWatchdogLeaseContract.PipeName));
    }

    private static bool IsReadyDisposition(
        LeaseRecoveryDisposition disposition) =>
        disposition is
            LeaseRecoveryDisposition.Ready or
            LeaseRecoveryDisposition.ClearedPrepared or
            LeaseRecoveryDisposition.RestoredFirmware;

    private static void EnsureServiceContext(
        Process process,
        WindowsIdentity? identity)
    {
        if (process.SessionId != 0)
        {
            throw new InvalidOperationException(
                $"Gate D requires Windows Session 0; observed SessionId={process.SessionId}.");
        }

        var sid = identity?.User?.Value;

        if (!string.Equals(
                sid,
                "S-1-5-18",
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"Gate D requires LocalSystem (S-1-5-18); observed SID='{sid ?? "unknown"}'.");
        }
    }
}
