using System.Diagnostics;
using System.Security.Principal;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// M4A/M4B/M4C qualification-only real lease path for the exact HP 8C40 target.
///
/// It uses the real coordinator + real 8C40 backend logic + target-bound
/// named-pipe watchdog lease, but bypasses the production factory prohibition
/// deliberately inside these bounded 30/10/50 hardware gates. Production remains blocked.
/// </summary>
public static class Hp8C40M4LeaseQualificationTest
{
    public const string RequiredToken10 = "8C40-M4-LEASE10";
    public const string RequiredToken30 = "8C40-M4-LEASE30";
    public const string RequiredToken50 = "8C40-M4-LEASE50";
    public const string RequiredToken = RequiredToken30;

    private const byte MinimumBatteryPercent = 20;

    public static string GetRequiredToken(
        int qualificationLevel) =>
        qualificationLevel switch
        {
            10 => RequiredToken10,
            30 => RequiredToken30,
            50 => RequiredToken50,
            _ => throw new ArgumentOutOfRangeException(
                nameof(qualificationLevel),
                qualificationLevel,
                "M4 qualification level must be exactly 10, 30 or 50.")
        };

    public static string GetGateName(
        int qualificationLevel) =>
        qualificationLevel switch
        {
            10 => "M4B",
            30 => "M4A",
            50 => "M4C",
            _ => throw new ArgumentOutOfRangeException(
                nameof(qualificationLevel),
                qualificationLevel,
                "M4 qualification level must be exactly 10, 30 or 50.")
        };

    public static async Task<int> RunAsync(
        string modulesDirectory,
        int qualificationLevel,
        CancellationToken cancellationToken)
    {
        var gate =
            GetGateName(qualificationLevel);

        Console.WriteLine(
            $"HP 8C40 {gate} - target-bound real watchdog lease at {qualificationLevel}/{qualificationLevel}");
        Console.WriteLine(
            "Production watchdog construction and automatic policy remain OFF.");
        Console.WriteLine();

        if (!IsAdministrator())
        {
            Console.Error.WriteLine(
                "M4 requires an elevated Administrator process.");
            return 170;
        }

        var hardwareIdentity =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardwareIdentity,
                out var targetReason))
        {
            Console.Error.WriteLine(
                $"M4 exact-target refusal: {targetReason}");
            return 171;
        }

        var conflict =
            FindKnownConflictingControllerProcess();

        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"M4 refused while '{conflict}' is running.");
            return 172;
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
                "M4 requires a clean firmware-owned FF/FF baseline.");
            return 173;
        }

        if (initial.MaxFan != 0 ||
            initial.FanSwitch != 0)
        {
            Console.Error.WriteLine(
                $"M4 guard refusal: MaxFan=0x{initial.MaxFan:X2}, " +
                $"FanSwitch=0x{initial.FanSwitch:X2}.");
            return 174;
        }

        using var telemetry =
            new HardwareTelemetryReader(
                modulesDirectory);

        if (!telemetry.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "M4 telemetry backends are not fully initialized.");
            return 175;
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
                "M4 SafetyGate refused Custom:");
            foreach (var reason in safety.Reasons)
            {
                Console.Error.WriteLine(
                    $"  - {reason}");
            }

            return 176;
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
                    $"HP 8C40 {gate} exact-target qualification-only real hardware path.",
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
                    "M4 coordinator did not acquire Custom authority.");
            }

            Console.WriteLine(
                "Lease PREPARED; coordinator Custom authority acquired.");

            await coordinator.ApplyAsync(
                    new FanCommand(
                        qualificationLevel,
                        qualificationLevel,
                        $"HP 8C40 {gate} target-bound lease qualification"),
                    safety,
                    cancellationToken)
                .ConfigureAwait(false);

            customWasOwned = true;

            var owned =
                ecProbe.ReadControlEvidence();

            Console.WriteLine(
                $"Owned EC after Commit: {Format(owned)}");

            if (owned.CpuSetpoint != qualificationLevel ||
                owned.GpuSetpoint != qualificationLevel)
            {
                throw new InvalidOperationException(
                    $"M4 expected EC {qualificationLevel}/{qualificationLevel}, " +
                    $"observed {owned.CpuSetpoint}/{owned.GpuSetpoint}.");
            }

            for (var sample = 1;
                 sample <= 4;
                 sample++)
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
                            $"M4 awake lease supervision sample {sample}/4",
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!healthy ||
                    coordinator.Authority !=
                        FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        $"M4 lease supervision sample {sample} did not retain Custom authority.");
                }

                var evidence =
                    ecProbe.ReadControlEvidence();

                if (evidence.CpuSetpoint !=
                        qualificationLevel ||
                    evidence.GpuSetpoint !=
                        qualificationLevel ||
                    evidence.MaxFan != 0 ||
                    evidence.FanSwitch != 0)
                {
                    throw new InvalidOperationException(
                        $"M4 ownership/guard mismatch during sample {sample}: {Format(evidence)}");
                }

                Console.WriteLine(
                    $"Lease supervision {sample}/4 PASS: {Format(evidence)}");

                await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await coordinator.RestoreFirmwareAsync(
                    "M4 normal target-bound lease release",
                    CancellationToken.None)
                .ConfigureAwait(false);

            var final =
                await WaitForFirmwareOwnedAsync(
                        ecProbe,
                        TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

            customWasOwned = false;

            Console.WriteLine(
                $"Final EC after lease Release: {Format(final)}");

            var restoreEvidence =
                coordinator.LastRestoreEvidence;

            if (restoreEvidence is null)
            {
                throw new InvalidOperationException(
                    "M4 coordinator/backend did not expose restore evidence.");
            }

            var verifiedRestore =
                restoreEvidence.Value;

            if (coordinator.Authority !=
                    FanAuthority.Firmware ||
                !verifiedRestore.LocalFirmwareAckVerified ||
                !verifiedRestore.WatchdogLeaseRequired ||
                !verifiedRestore.WatchdogReleaseVerified)
            {
                throw new InvalidOperationException(
                    "M4 coordinator/backend restore evidence did not prove both local firmware ACK and watchdog Release.");
            }

            Console.WriteLine(
                $"PASS: target-bound {gate} lease completed PREPARE -> WRITE_INTENT -> " +
                $"{qualificationLevel}/{qualificationLevel} ACK -> COMMIT -> Probe/Heartbeat supervision -> " +
                "RESTORE_BEGIN -> local FF/FF -> watchdog Release.");

            return 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "M4 qualification cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"M4 qualification failed: {ex.GetType().Name}: {ex.Message}");
            return 177;
        }
        finally
        {
            if (customWasOwned)
            {
                try
                {
                    await coordinator.RestoreFirmwareAsync(
                            "M4 qualification finally fallback",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"M4 coordinator fallback restore failed: {ex.GetType().Name}: {ex.Message}");

                    await EmergencyFallbackIfStillOwnedAsync(
                            ecProbe,
                            qualificationLevel)
                        .ConfigureAwait(false);
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

    private static async Task<Hp8C40EcControlState>
        WaitForFirmwareOwnedAsync(
            Hp8C40EcControlStateProbe probe,
            TimeSpan timeout)
    {
        var started =
            Stopwatch.GetTimestamp();

        Hp8C40EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) <
               timeout)
        {
            last =
                probe.ReadControlEvidence();

            if (last.CpuSetpoint == byte.MaxValue &&
                last.GpuSetpoint == byte.MaxValue)
            {
                return last;
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"M4 did not independently observe FF/FF; last={Format(last)}");
    }

    private static async Task EmergencyFallbackIfStillOwnedAsync(
        Hp8C40EcControlStateProbe probe,
        int qualificationLevel)
    {
        try
        {
            var current =
                probe.ReadControlEvidence();

            if (current.CpuSetpoint == byte.MaxValue &&
                current.GpuSetpoint == byte.MaxValue)
            {
                return;
            }

            if (current.CpuSetpoint != qualificationLevel ||
                current.GpuSetpoint != qualificationLevel)
            {
                Console.Error.WriteLine(
                    $"M4 emergency local restore REFUSED because EC is " +
                    $"{current.CpuSetpoint}/{current.GpuSetpoint}, not the exact VFC-owned " +
                    $"{qualificationLevel}/{qualificationLevel}.");
                return;
            }

            Console.Error.WriteLine(
                $"M4 emergency local restore: exact VFC-owned {qualificationLevel}/{qualificationLevel} remains.");

            new Hp8C40BiosFanControl()
                .RestoreFirmwareAuto();

            _ =
                await WaitForFirmwareOwnedAsync(
                        probe,
                        TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

            Console.Error.WriteLine(
                "M4 emergency local restore verified FF/FF.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"CRITICAL: M4 emergency local restore failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
                $"M4 AC/battery sanity gate refused: {status}");
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
                "M4 light-load envelope exceeded.");
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

    private static string Format(
        Hp8C40EcControlState? state) =>
        state is null
            ? "n/a"
            : $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}; " +
              $"MaxFan=0x{state.MaxFan:X2}; FanSwitch=0x{state.FanSwitch:X2}; " +
              $"RPM={state.CpuRpm}/{state.GpuRpm}";
}
