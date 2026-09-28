using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Qualification-only HP 8C40 M4 lease service.
///
/// M3 physically qualified LocalSystem restore-only authority. M4 adds the
/// durable lease/journal + target-bound named-pipe path while keeping ordinary
/// service-side fan writes impossible. Production 8C40 watchdog construction
/// remains blocked until later qualification gates are complete.
/// </summary>
internal sealed class M4Hp8C40LeaseWorker :
    BackgroundService
{
    private static readonly TimeSpan DeadlinePollInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly WatchdogOptions _options;

    public M4Hp8C40LeaseWorker(
        WatchdogOptions options)
    {
        _options = options;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var log =
            new WatchdogFileLog(
                _options.LogDirectory,
                "watchdog-m4-8c40");

        using var process =
            Process.GetCurrentProcess();

        using var windowsIdentity =
            WindowsIdentity.GetCurrent();

        var accountName =
            windowsIdentity?.Name ??
            Environment.UserName;

        WatchdogLeaseManager? manager = null;

        var journalPath =
            Path.Combine(
                Path.GetDirectoryName(
                    _options.ResultPath) ??
                throw new InvalidOperationException(
                    "M4 result path has no parent directory."),
                "lease.json");

        HardwareIdentity? hardwareIdentity = null;
        var targetPolicy =
            WatchdogTargetPolicies.Hp8C40;

        try
        {
            log.Write(
                $"M4 START pid={process.Id}; session={process.SessionId}; " +
                $"account={accountName}; target={targetPolicy.TargetProfileId}; " +
                $"journal={journalPath}; pipe={FanControlWatchdogLeaseContract.Hp8C40M4PipeName}");

            EnsureServiceContext(
                process,
                windowsIdentity);

            hardwareIdentity =
                HardwareIdentityReader.ReadCurrent();

            if (!Hp8C40TargetProfile.Matches(
                    hardwareIdentity,
                    out var reason))
            {
                throw new InvalidOperationException(
                    $"Exact HP 8C40 target fingerprint refused: {reason}");
            }

            var modulePath =
                Path.Combine(
                    _options.ModulesDirectory,
                    "LpcACPIEC.bin");

            if (!File.Exists(modulePath))
            {
                throw new FileNotFoundException(
                    "M4 requires the signed LpcACPIEC.bin module.",
                    modulePath);
            }

            manager =
                new WatchdogLeaseManager(
                    new JsonLeaseJournal(
                        journalPath,
                        targetPolicy),
                    new M4Hp8C40LeaseHardware(
                        _options.ModulesDirectory),
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
                $"M4 STARTUP RECOVERY disposition={startupRecovery.Disposition}; " +
                $"observed={startupRecovery.Observed}; restoreAttempted={startupRecovery.RestoreAttempted}; " +
                $"journalRetained={startupRecovery.JournalRetained}; detail={startupRecovery.Detail}");

            if (!startupReady)
            {
                throw new InvalidOperationException(
                    $"M4 startup is blocked: {startupRecovery.Detail}");
            }

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

                await completed.ConfigureAwait(false);

                throw new InvalidOperationException(
                    "M4 service loop ended unexpectedly without a stop request.");
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
            log.Write(
                "M4 cancellation requested.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 160;

            try
            {
                log.Write(
                    $"M4 FATAL: {ex.GetType().Name}: {ex.Message}");
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
                            "M4 watchdog service stop/update",
                            CancellationToken.None)
                            .ConfigureAwait(false);

                    if (stopRecovery is not null)
                    {
                        log.Write(
                            $"M4 STOP RECOVERY disposition={stopRecovery.Disposition}; " +
                            $"observed={stopRecovery.Observed}; restoreAttempted={stopRecovery.RestoreAttempted}; " +
                            $"journalRetained={stopRecovery.JournalRetained}; detail={stopRecovery.Detail}");
                    }
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 161;

                    log.Write(
                        $"M4 STOP RECOVERY FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }

            log.Write(
                $"M4 STOP exitCode={Environment.ExitCode}.");
        }
    }

    private static async Task RunAcceptLoopAsync(
        WatchdogLeaseManager manager,
        WatchdogFileLog log,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe =
                GateDPipeFactory.Create(
                    FanControlWatchdogLeaseContract.Hp8C40M4PipeName);

            log.Write(
                "M4 PIPE waiting for one elevated exact-target 8C40 controller.");

            await GateCPipeServerSession.RunAsync(
                pipe,
                manager,
                stoppingToken,
                log.Write,
                monitorControllerProcess: true)
                .ConfigureAwait(false);
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
                                        $"M4 OWNER MONITOR armed PID={activeController.ProcessId}; " +
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
                        $"M4 MONITOR FAIL: {ex.GetType().Name}: {ex.Message}");

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
                    $"M4 RECOVERY disposition={recovery.Disposition}; " +
                    $"observed={recovery.Observed}; restoreAttempted={recovery.RestoreAttempted}; " +
                    $"journalRetained={recovery.JournalRetained}; detail={recovery.Detail}");

                if (recovery.JournalRetained)
                {
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
                TargetProfileId:
                    Hp8C40TargetProfile.Instance.Id,
                RecoveryDisposition:
                    recoveryDisposition,
                Detail:
                    detail,
                JournalPath:
                    journalPath,
                PipeName:
                    FanControlWatchdogLeaseContract.Hp8C40M4PipeName));
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
                $"M4 requires Windows Session 0; observed SessionId={process.SessionId}.");
        }

        var sid =
            identity?.User?.Value;

        if (!string.Equals(
                sid,
                "S-1-5-18",
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"M4 requires LocalSystem (S-1-5-18); observed SID='{sid ?? "unknown"}'.");
        }
    }
}
