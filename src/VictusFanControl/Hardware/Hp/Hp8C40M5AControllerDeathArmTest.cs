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
/// M5A qualification-only controller process used to prove that the independent
/// LocalSystem 8C40 watchdog restores firmware after the exact controller dies.
///
/// The process deliberately holds an ordinary watchdog-protected 30/30 OWNED
/// lease until it is force-terminated by the parent hardware harness. A normal
/// cancellation/error path restores firmware locally; only an OS-level process
/// kill can bypass that managed cleanup and exercise watchdog owner-loss
/// recovery.
/// </summary>
public static class Hp8C40M5AControllerDeathArmTest
{
    public const string RequiredToken = "8C40-M5A-CONTROLLER-DEATH30";
    public const int QualificationLevel = 30;

    private const byte MinimumBatteryPercent = 20;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string readyPath,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            "HP 8C40 M5A - arm real OWNED 30/30 lease for forced controller death");
        Console.WriteLine(
            "Normal cancellation restores locally; M5A PASS requires parent force-kill and watchdog-only recovery.");
        Console.WriteLine();

        if (!IsAdministrator())
        {
            Console.Error.WriteLine(
                "M5A requires an elevated Administrator process.");
            return 180;
        }

        var hardwareIdentity =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardwareIdentity,
                out var targetReason))
        {
            Console.Error.WriteLine(
                $"M5A exact-target refusal: {targetReason}");
            return 181;
        }

        var conflict =
            FindKnownConflictingControllerProcess();

        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"M5A refused while '{conflict}' is running.");
            return 182;
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
                "M5A requires a clean firmware-owned FF/FF baseline.");
            return 183;
        }

        if (initial.MaxFan != 0 ||
            initial.FanSwitch != 0)
        {
            Console.Error.WriteLine(
                $"M5A guard refusal: MaxFan=0x{initial.MaxFan:X2}, " +
                $"FanSwitch=0x{initial.FanSwitch:X2}.");
            return 184;
        }

        using var telemetry =
            new HardwareTelemetryReader(
                modulesDirectory);

        if (!telemetry.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "M5A telemetry backends are not fully initialized.");
            return 185;
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
                "M5A SafetyGate refused Custom:");
            foreach (var reason in safety.Reasons)
            {
                Console.Error.WriteLine(
                    $"  - {reason}");
            }

            return 186;
        }

        EnsureLightLoadEnvelope(snapshot);

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
                    "HP 8C40 M5A exact-target controller-death qualification path.",
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
                    "M5A coordinator did not acquire Custom authority.");
            }

            await coordinator.ApplyAsync(
                    new FanCommand(
                        QualificationLevel,
                        QualificationLevel,
                        "HP 8C40 M5A controller-death qualification"),
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
                    $"M5A READY evidence is not healthy OWNED 30/30: {Format(owned)}");
            }

            using var process =
                Process.GetCurrentProcess();

            var processStartTicks =
                process.StartTime
                    .ToUniversalTime()
                    .Ticks;

            WriteReadyMarker(
                readyPath,
                new M5AReadyMarker(
                    SchemaVersion: 1,
                    Gate: "M5A",
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
                $"M5A READY: PID={process.Id}; startTicks={processStartTicks}; " +
                $"{Format(owned)}");
            Console.WriteLine(
                "Waiting for parent force-kill. Ctrl+C is a cancellation path and will restore firmware locally.");

            while (true)
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

                var healthy =
                    await coordinator.EnforceSafetyAsync(
                            freshSafety,
                            "M5A pre-kill controller supervision",
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!healthy ||
                    coordinator.Authority !=
                        FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        "M5A lost Custom authority before the parent kill boundary.");
                }

                var evidence =
                    ecProbe.ReadControlEvidence();

                if (evidence.CpuSetpoint != QualificationLevel ||
                    evidence.GpuSetpoint != QualificationLevel ||
                    evidence.MaxFan != 0 ||
                    evidence.FanSwitch != 0 ||
                    evidence.CpuRpm == 0 ||
                    evidence.GpuRpm == 0)
                {
                    throw new InvalidOperationException(
                        $"M5A ownership/feedback changed before parent kill: {Format(evidence)}");
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(500),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "M5A controller arm cancelled; managed cleanup will restore firmware.");
            return 187;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"M5A controller arm failed: {ex.GetType().Name}: {ex.Message}");
            return 188;
        }
        finally
        {
            // This path is deliberately bypassed by Process.Kill(). Any normal
            // exit/cancellation must restore locally so an abandoned test cannot
            // leave a fixed setpoint merely because the fault was not injected.
            if (customWasOwned)
            {
                try
                {
                    await coordinator.RestoreFirmwareAsync(
                            "M5A managed-exit fallback",
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    Console.Error.WriteLine(
                        "M5A managed-exit fallback verified firmware restore.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"M5A managed-exit fallback failed: {ex.GetType().Name}: {ex.Message}");
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
                $"M5A AC/battery sanity gate refused: {status}");
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
                "M5A light-load envelope exceeded.");
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
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }

    private static void WriteReadyMarker(
        string path,
        M5AReadyMarker marker)
    {
        var fullPath =
            Path.GetFullPath(path);

        var directory =
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "M5A READY marker path has no parent directory.");

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

                stream.Flush(flushToDisk: true);
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

    private sealed record M5AReadyMarker(
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
}
