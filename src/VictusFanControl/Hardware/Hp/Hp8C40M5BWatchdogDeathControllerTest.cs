using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// M5B qualification-only controller that proves the still-live 8C40
/// controller restores HP firmware locally when the independent watchdog
/// process dies.
///
/// READY is published only after the real target-bound transaction reaches
/// durable OWNED 30/30. The parent then hard-kills the watchdog service
/// process. The controller must detect WATCHDOG_IPC_LOSS through the ordinary
/// safety-supervision dependency probe, complete the local validated
/// FF/FF -> LegacyDefault restore, publish causal restore evidence and remain
/// alive while the parent proves service absence and retained durable journal.
/// </summary>
public static class Hp8C40M5BWatchdogDeathControllerTest
{
    public const string RequiredToken = "8C40-M5B-WATCHDOG-DEATH30";
    public const int QualificationLevel = 30;

    private const byte MinimumBatteryPercent = 20;
    private static readonly TimeSpan CompletionWaitTimeout =
        TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string readyPath,
        string localRestorePath,
        string completionPath,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            "HP 8C40 M5B - live-controller local recovery after watchdog death");
        Console.WriteLine(
            "The controller holds watchdog-owned 30/30, detects WATCHDOG_IPC_LOSS, restores locally, then waits alive for parent verification.");
        Console.WriteLine();

        if (!IsAdministrator())
        {
            Console.Error.WriteLine(
                "M5B requires an elevated Administrator process.");
            return 190;
        }

        var hardwareIdentity =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardwareIdentity,
                out var targetReason))
        {
            Console.Error.WriteLine(
                $"M5B exact-target refusal: {targetReason}");
            return 191;
        }

        var conflict =
            FindKnownConflictingControllerProcess();

        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"M5B refused while '{conflict}' is running.");
            return 192;
        }

        EnsurePowerStatus(
            SystemPowerStatusReader.Read());

        var ecProbe =
            new Hp8C40EcControlStateProbe(
                modulesDirectory);

        var initial =
            ecProbe.ReadControlEvidence();

        Console.WriteLine(
            $"Initial EC: {Format(initial)}");

        if (initial.CpuSetpoint != byte.MaxValue ||
            initial.GpuSetpoint != byte.MaxValue)
        {
            Console.Error.WriteLine(
                "M5B requires a clean firmware-owned FF/FF baseline.");
            return 193;
        }

        if (initial.MaxFan != 0 ||
            initial.FanSwitch != 0)
        {
            Console.Error.WriteLine(
                $"M5B guard refusal: MaxFan=0x{initial.MaxFan:X2}, " +
                $"FanSwitch=0x{initial.FanSwitch:X2}.");
            return 194;
        }

        using var telemetry =
            new HardwareTelemetryReader(
                modulesDirectory);

        if (!telemetry.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "M5B telemetry backends are not fully initialized.");
            return 195;
        }

        _ = telemetry.ReadSnapshot();

        await Task.Delay(
                TimeSpan.FromSeconds(1),
                cancellationToken)
            .ConfigureAwait(false);

        var snapshot =
            telemetry.ReadSnapshot();

        var safety =
            EvaluateSafety(
                hardwareIdentity,
                snapshot);

        if (!safety.CustomControlPermitted)
        {
            Console.Error.WriteLine(
                "M5B SafetyGate refused Custom:");

            foreach (var reason in safety.Reasons)
            {
                Console.Error.WriteLine(
                    $"  - {reason}");
            }

            return 196;
        }

        EnsureLightLoadEnvelope(
            snapshot);

        var lease =
            new NamedPipeFanControlWatchdogLeaseClient(
                Hp8C40TargetProfile.Instance.Id,
                FanControlWatchdogLeaseContract.Hp8C40M4PipeName);

        var realHardware =
            new Hp8C40FanHardware(
                modulesDirectory);

        var backend =
            new Hp8C40FanControlBackend(
                realHardware,
                targetSupported: true,
                supportDetail:
                    "HP 8C40 M5B exact-target watchdog-death qualification path.",
                watchdogLease:
                    lease);

        await using var coordinator =
            new FanControlCoordinator(
                backend);

        var customWasOwned = false;

        try
        {
            var admitted =
                await coordinator.TryEnterCustomAsync(
                    safety,
                    cancellationToken)
                    .ConfigureAwait(false);

            if (!admitted ||
                coordinator.Authority !=
                    FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "M5B coordinator did not acquire Custom authority.");
            }

            await coordinator.ApplyAsync(
                    new FanCommand(
                        QualificationLevel,
                        QualificationLevel,
                        "HP 8C40 M5B watchdog-death qualification"),
                    safety,
                    cancellationToken)
                .ConfigureAwait(false);

            customWasOwned = true;

            var owned =
                ecProbe.ReadControlEvidence();

            if (owned.CpuSetpoint != QualificationLevel ||
                owned.GpuSetpoint != QualificationLevel ||
                owned.MaxFan != 0 ||
                owned.FanSwitch != 0 ||
                owned.CpuRpm == 0 ||
                owned.GpuRpm == 0)
            {
                throw new InvalidOperationException(
                    $"M5B READY evidence is not healthy OWNED 30/30: {Format(owned)}");
            }

            using var process =
                Process.GetCurrentProcess();

            var processStartTicks =
                process.StartTime
                    .ToUniversalTime()
                    .Ticks;

            WriteJsonMarker(
                readyPath,
                new M5BReadyMarker(
                    SchemaVersion: 1,
                    Gate: "M5B",
                    TargetProfileId:
                        Hp8C40TargetProfile.Instance.Id,
                    ProcessId:
                        process.Id,
                    ProcessStartUtcTicks:
                        processStartTicks,
                    Authority:
                        coordinator.Authority.ToString(),
                    CpuSetpoint:
                        owned.CpuSetpoint,
                    GpuSetpoint:
                        owned.GpuSetpoint,
                    CpuRpm:
                        owned.CpuRpm,
                    GpuRpm:
                        owned.GpuRpm,
                    MaxFan:
                        owned.MaxFan,
                    FanSwitch:
                        owned.FanSwitch,
                    Ack:
                        "backend-ec+tachs+watchdog-owned",
                    TimestampUtc:
                        DateTimeOffset.UtcNow));

            Console.WriteLine(
                $"M5B READY: PID={process.Id}; startTicks={processStartTicks}; " +
                $"{Format(owned)}");
            Console.WriteLine(
                "Waiting for watchdog death through the ordinary dependency probe.");

            Exception? watchdogLoss = null;

            while (watchdogLoss is null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EnsurePowerStatus(
                    SystemPowerStatusReader.Read());

                var fresh =
                    telemetry.ReadSnapshot();

                EnsureLightLoadEnvelope(
                    fresh);

                var freshSafety =
                    EvaluateSafety(
                        hardwareIdentity,
                        fresh);

                try
                {
                    var healthy =
                        await coordinator.EnforceSafetyAsync(
                                freshSafety,
                                "M5B pre-kill controller supervision",
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (!healthy ||
                        coordinator.Authority !=
                            FanAuthority.Custom)
                    {
                        throw new InvalidOperationException(
                            "M5B lost Custom authority without a watchdog IPC failure.");
                    }
                }
                catch (Exception ex)
                    when (ContainsWatchdogIpcLoss(ex))
                {
                    watchdogLoss = ex;
                    break;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(250),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (coordinator.Authority !=
                FanAuthority.Firmware)
            {
                throw new InvalidOperationException(
                    $"M5B detected watchdog IPC loss but coordinator authority is {coordinator.Authority}, expected Firmware after local fallback.");
            }

            var restored =
                ecProbe.ReadControlEvidence();

            if (restored.CpuSetpoint != byte.MaxValue ||
                restored.GpuSetpoint != byte.MaxValue ||
                restored.MaxFan != 0 ||
                restored.FanSwitch != 0)
            {
                throw new InvalidOperationException(
                    $"M5B watchdog-loss local fallback did not verify clean FF/FF: {Format(restored)}");
            }

            var restoreEvidence =
                backend.LastRestoreEvidence;

            if (!restoreEvidence.LocalFirmwareAckVerified ||
                !restoreEvidence.WatchdogLeaseRequired ||
                restoreEvidence.WatchdogReleaseVerified)
            {
                throw new InvalidOperationException(
                    "M5B restore evidence did not prove local FF/FF with required-but-unreleased watchdog lease. " +
                    restoreEvidence.Detail);
            }

            WriteJsonMarker(
                localRestorePath,
                new M5BLocalRestoreMarker(
                    SchemaVersion: 1,
                    Gate: "M5B",
                    TargetProfileId:
                        Hp8C40TargetProfile.Instance.Id,
                    ProcessId:
                        process.Id,
                    ProcessStartUtcTicks:
                        processStartTicks,
                    Authority:
                        coordinator.Authority.ToString(),
                    Reason:
                        FlattenExceptionMessages(
                            watchdogLoss!),
                    CpuSetpoint:
                        restored.CpuSetpoint,
                    GpuSetpoint:
                        restored.GpuSetpoint,
                    CpuRpm:
                        restored.CpuRpm,
                    GpuRpm:
                        restored.GpuRpm,
                    MaxFan:
                        restored.MaxFan,
                    FanSwitch:
                        restored.FanSwitch,
                    LocalFirmwareAckVerified:
                        restoreEvidence.LocalFirmwareAckVerified,
                    WatchdogLeaseRequired:
                        restoreEvidence.WatchdogLeaseRequired,
                    WatchdogReleaseVerified:
                        restoreEvidence.WatchdogReleaseVerified,
                    RestoreDetail:
                        restoreEvidence.Detail,
                    TimestampUtc:
                        DateTimeOffset.UtcNow));

            customWasOwned = false;

            Console.WriteLine(
                $"M5B LOCAL-RESTORE: {Format(restored)}");
            Console.WriteLine(
                "Controller remains alive while parent proves service absence, retained journal and replacement-service startup recovery.");

            var waitStarted =
                Stopwatch.StartNew();

            while (!File.Exists(completionPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (waitStarted.Elapsed >
                    CompletionWaitTimeout)
                {
                    throw new TimeoutException(
                        $"M5B parent completion marker was not observed within {CompletionWaitTimeout.TotalSeconds:0} s.");
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Console.WriteLine(
                "M5B parent completion marker observed; controller exits after firmware/restart recovery proof.");
            return 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "M5B controller cancelled; managed cleanup will restore firmware if still required.");
            return 197;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"M5B controller failed: {ex.GetType().Name}: {ex.Message}");
            return 198;
        }
        finally
        {
            if (customWasOwned)
            {
                try
                {
                    await coordinator.RestoreFirmwareAsync(
                            "M5B managed-exit fallback",
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    Console.Error.WriteLine(
                        "M5B managed-exit fallback verified firmware restore.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"M5B managed-exit fallback failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static SafetyGateResult EvaluateSafety(
        HardwareIdentity hardwareIdentity,
        TelemetrySnapshot snapshot) =>
        SafetyGate.Evaluate(
            hardwareIdentity,
            SystemState.Healthy,
            snapshot,
            DateTimeOffset.UtcNow,
            fanWritePathPresent: true);

    private static void EnsurePowerStatus(
        SystemPowerStatusSample status)
    {
        if (!status.AcOnline ||
            !status.BatteryPresent ||
            status.BatteryPercent > 100 ||
            status.BatteryPercent <
                MinimumBatteryPercent)
        {
            throw new InvalidOperationException(
                $"M5B AC/battery sanity gate refused: {status}");
        }
    }

    private static void EnsureLightLoadEnvelope(
        TelemetrySnapshot snapshot)
    {
        if (snapshot.CpuControlTemperatureC > 80 ||
            snapshot.GpuTemperatureC > 75 ||
            snapshot.CpuPackagePowerW > 50 ||
            snapshot.GpuPowerW > 70)
        {
            throw new InvalidOperationException(
                "M5B light-load envelope exceeded.");
        }
    }

    private static bool ContainsWatchdogIpcLoss(
        Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current.Message.Contains(
                    "WATCHDOG_IPC_LOSS",
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    if (ContainsWatchdogIpcLoss(inner))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string FlattenExceptionMessages(
        Exception exception)
    {
        var messages =
            new List<string>();

        void Add(
            Exception current)
        {
            messages.Add(
                $"{current.GetType().Name}: {current.Message}");

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    Add(inner);
                }

                return;
            }

            if (current.InnerException is not null)
            {
                Add(
                    current.InnerException);
            }
        }

        Add(exception);

        return string.Join(
            " | ",
            messages.Distinct(
                StringComparer.Ordinal));
    }

    private static bool IsAdministrator()
    {
        using var identity =
            WindowsIdentity.GetCurrent();

        var principal =
            new WindowsPrincipal(
                identity);

        return principal.IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static string? FindKnownConflictingControllerProcess()
    {
        foreach (var name in new[]
                 {
                     "OmenMon",
                     "OmenMon-Reborn",
                     "VictusFanControl.App"
                 })
        {
            Process[] processes;

            try
            {
                processes =
                    Process.GetProcessesByName(
                        name);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return name;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }

    private static void WriteJsonMarker<T>(
        string path,
        T marker)
    {
        var fullPath =
            Path.GetFullPath(path);

        var directory =
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "M5B marker path has no parent directory.");

        Directory.CreateDirectory(
            directory);

        var temp =
            fullPath +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream =
                new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    options:
                        FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    marker,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                temp,
                fullPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string Format(
        Hp8C40EcControlState state) =>
        $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}; " +
        $"MaxFan=0x{state.MaxFan:X2}; FanSwitch=0x{state.FanSwitch:X2}; " +
        $"RPM={state.CpuRpm}/{state.GpuRpm}";

    private sealed record M5BReadyMarker(
        int SchemaVersion,
        string Gate,
        string TargetProfileId,
        int ProcessId,
        long ProcessStartUtcTicks,
        string Authority,
        int CpuSetpoint,
        int GpuSetpoint,
        int CpuRpm,
        int GpuRpm,
        int MaxFan,
        int FanSwitch,
        string Ack,
        DateTimeOffset TimestampUtc);

    private sealed record M5BLocalRestoreMarker(
        int SchemaVersion,
        string Gate,
        string TargetProfileId,
        int ProcessId,
        long ProcessStartUtcTicks,
        string Authority,
        string Reason,
        int CpuSetpoint,
        int GpuSetpoint,
        int CpuRpm,
        int GpuRpm,
        int MaxFan,
        int FanSwitch,
        bool LocalFirmwareAckVerified,
        bool WatchdogLeaseRequired,
        bool WatchdogReleaseVerified,
        string RestoreDetail,
        DateTimeOffset TimestampUtc);
}
