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
/// Qualification-only M5D controller. It deliberately pauses after a real
/// 30/30 WMI command has been acknowledged by EC setpoints and both physical
/// tachometers, but before the watchdog Commit request can be dispatched.
///
/// A parent harness must prove the durable lease is still WRITE_ARMED and then
/// force-kill this exact process. Any managed cancellation/error returns
/// through FanControlCoordinator cleanup and restores firmware locally.
/// </summary>
public static class Hp8C40M5DWriteArmedCrashTest
{
    public const string RequiredToken = "8C40-M5D-WRITE-ARMED-CRASH30";
    public const int QualificationLevel = 30;

    private const byte MinimumBatteryPercent = 20;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string readyPath,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            "HP 8C40 M5D - pause after real WMI+EC+tach ACK while watchdog remains WRITE_ARMED");
        Console.WriteLine(
            "Parent must force-kill this exact process before Commit; managed cancellation restores firmware locally.");
        Console.WriteLine();

        if (!IsAdministrator())
        {
            Console.Error.WriteLine(
                "M5D requires an elevated Administrator process.");
            return 190;
        }

        var hardwareIdentity =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardwareIdentity,
                out var targetReason))
        {
            Console.Error.WriteLine(
                $"M5D exact-target refusal: {targetReason}");
            return 191;
        }

        var conflict =
            FindKnownConflictingControllerProcess();

        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"M5D refused while '{conflict}' is running.");
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
                "M5D requires a clean firmware-owned FF/FF baseline.");
            return 193;
        }

        if (initial.MaxFan != 0 ||
            initial.FanSwitch != 0)
        {
            Console.Error.WriteLine(
                $"M5D guard refusal: MaxFan=0x{initial.MaxFan:X2}, " +
                $"FanSwitch=0x{initial.FanSwitch:X2}.");
            return 194;
        }

        using var telemetry =
            new HardwareTelemetryReader(
                modulesDirectory);

        if (!telemetry.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "M5D telemetry backends are not fully initialized.");
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
                "M5D SafetyGate refused Custom:");

            foreach (var reason in safety.Reasons)
            {
                Console.Error.WriteLine(
                    $"  - {reason}");
            }

            return 196;
        }

        EnsureLightLoadEnvelope(snapshot);

        using var process =
            Process.GetCurrentProcess();

        var processStartTicks =
            process.StartTime
                .ToUniversalTime()
                .Ticks;

        var qualificationHook =
            new WriteArmedPauseHook(
                readyPath,
                process.Id,
                processStartTicks);

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
                    "HP 8C40 M5D post-WMI/pre-Commit WRITE_ARMED qualification path.",
                watchdogLease:
                    lease,
                qualificationHook:
                    qualificationHook);

        await using var coordinator =
            new FanControlCoordinator(
                backend);

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
                    "M5D coordinator did not acquire Custom authority.");
            }

            await coordinator.ApplyAsync(
                    new FanCommand(
                        QualificationLevel,
                        QualificationLevel,
                        "HP 8C40 M5D WRITE_ARMED post-WMI/pre-Commit crash qualification"),
                    safety,
                    cancellationToken)
                .ConfigureAwait(false);

            throw new InvalidOperationException(
                "M5D qualification hook returned and allowed watchdog Commit; the required crash window was not held.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "M5D controller cancelled; managed coordinator cleanup will restore firmware.");
            return 197;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"M5D controller failed: {ex.GetType().Name}: {ex.Message}");
            return 198;
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
                $"M5D AC/battery sanity gate refused: {status}");
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
                "M5D light-load envelope exceeded.");
        }
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
                foreach (var candidate in processes)
                {
                    candidate.Dispose();
                }
            }
        }

        return null;
    }

    private static string Format(
        Hp8C40EcControlState state) =>
        $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}; " +
        $"MaxFan=0x{state.MaxFan:X2}; FanSwitch=0x{state.FanSwitch:X2}; " +
        $"RPM={state.CpuRpm}/{state.GpuRpm}";

    private sealed class WriteArmedPauseHook :
        IHp8C40FanWriteQualificationHook
    {
        private readonly string _readyPath;
        private readonly int _processId;
        private readonly long _processStartTicks;
        private int _entered;

        public WriteArmedPauseHook(
            string readyPath,
            int processId,
            long processStartTicks)
        {
            _readyPath = readyPath;
            _processId = processId;
            _processStartTicks = processStartTicks;
        }

        public async ValueTask AfterHardwareAcknowledgedBeforeWatchdogCommitAsync(
            byte cpuTarget,
            byte gpuTarget,
            Hp8C40EcControlState setpointAck,
            Hp8C40EcControlState tachAck,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(
                    ref _entered,
                    1) != 0)
            {
                throw new InvalidOperationException(
                    "M5D qualification hook was entered more than once.");
            }

            if (cpuTarget != QualificationLevel ||
                gpuTarget != QualificationLevel ||
                setpointAck.CpuSetpoint != QualificationLevel ||
                setpointAck.GpuSetpoint != QualificationLevel ||
                tachAck.CpuSetpoint != QualificationLevel ||
                tachAck.GpuSetpoint != QualificationLevel ||
                tachAck.MaxFan != 0 ||
                tachAck.FanSwitch != 0 ||
                tachAck.CpuRpm == 0 ||
                tachAck.GpuRpm == 0)
            {
                throw new InvalidOperationException(
                    $"M5D pre-Commit hook received unhealthy hardware acknowledgement: {Format(tachAck)}");
            }

            WriteReadyMarker(
                _readyPath,
                new M5DReadyMarker(
                    SchemaVersion: 1,
                    Gate: "M5D",
                    Stage:
                        "WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT",
                    TargetProfileId:
                        Hp8C40TargetProfile.Instance.Id,
                    ProcessId:
                        _processId,
                    ProcessStartUtcTicks:
                        _processStartTicks,
                    CpuSetpoint:
                        tachAck.CpuSetpoint,
                    GpuSetpoint:
                        tachAck.GpuSetpoint,
                    CpuRpm:
                        tachAck.CpuRpm,
                    GpuRpm:
                        tachAck.GpuRpm,
                    MaxFan:
                        tachAck.MaxFan,
                    FanSwitch:
                        tachAck.FanSwitch,
                    Ack:
                        "real-wmi+ec+tachs;watchdog-commit-not-dispatched",
                    TimestampUtc:
                        DateTimeOffset.UtcNow));

            Console.WriteLine(
                $"M5D READY: PID={_processId}; startTicks={_processStartTicks}; " +
                $"{Format(tachAck)}");
            Console.WriteLine(
                "Hardware ACK is complete while backend is paused before watchdog Commit. Waiting for parent force-kill.");

            await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static void WriteReadyMarker(
            string path,
            M5DReadyMarker marker)
        {
            var fullPath =
                Path.GetFullPath(path);

            var directory =
                Path.GetDirectoryName(fullPath) ??
                throw new InvalidOperationException(
                    "M5D READY marker path has no parent directory.");

            Directory.CreateDirectory(directory);

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
    }

    private sealed record M5DReadyMarker(
        int SchemaVersion,
        string Gate,
        string Stage,
        string TargetProfileId,
        int ProcessId,
        long ProcessStartUtcTicks,
        int CpuSetpoint,
        int GpuSetpoint,
        int CpuRpm,
        int GpuRpm,
        int MaxFan,
        int FanSwitch,
        string Ack,
        DateTimeOffset TimestampUtc);
}
