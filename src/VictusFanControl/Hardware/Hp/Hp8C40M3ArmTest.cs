using System.Diagnostics;
using System.Security.Principal;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Qualification-only M3 armer. It creates one known VFC-owned equal 30/30
/// state, publishes a short-lived durable handoff record, then waits for the
/// LocalSystem M3 restore-only service to return ownership to firmware.
///
/// This is not production fan policy and never grants watchdog lease authority.
/// </summary>
public static class Hp8C40M3ArmTest
{
    public const string RequiredToken = "8C40-M3-RESTORE30";

    private const byte RequiredLevel =
        Hp8C40M3HandoffRecord.RequiredQualificationLevel;

    private const byte MinimumBatteryPercent = 20;
    private const ushort MinimumFanRpmForHandoff = 750;
    private const int RequiredStableSamples = 2;

    private static readonly TimeSpan AckTimeout =
        TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ResultTimeout =
        TimeSpan.FromSeconds(20);

    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string handoffPath,
        string resultPath,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            "HP 8C40 M3 armer - bounded 30/30 -> LocalSystem restore handoff");
        Console.WriteLine(
            "Automatic/adaptive policy and watchdog lease remain OFF.");
        Console.WriteLine();

        if (!IsAdministrator())
        {
            Console.Error.WriteLine(
                "M3 armer requires an elevated Administrator process.");
            return 141;
        }

        var hardware =
            HardwareIdentityReader.ReadCurrent();

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var targetReason))
        {
            Console.Error.WriteLine(
                $"M3 armer exact-target refusal: {targetReason}");
            return 142;
        }

        var conflict =
            FindKnownConflictingControllerProcess();

        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"M3 armer refused while '{conflict}' is running.");
            return 143;
        }

        var handoffFullPath =
            Path.GetFullPath(handoffPath);
        var resultFullPath =
            Path.GetFullPath(resultPath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(handoffFullPath)!);
        Directory.CreateDirectory(
            Path.GetDirectoryName(resultFullPath)!);

        File.Delete(handoffFullPath);
        File.Delete(resultFullPath);

        EnsurePowerStatus(
            SystemPowerStatusReader.Read());

        using var telemetry =
            new HardwareTelemetryReader(
                modulesDirectory);

        if (!telemetry.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "M3 armer telemetry backends are not fully initialized.");
            return 144;
        }

        _ = telemetry.ReadSnapshot();
        await Task.Delay(
                TimeSpan.FromSeconds(1),
                cancellationToken)
            .ConfigureAwait(false);

        var baseline =
            telemetry.ReadSnapshot();

        var baselineSafety =
            SafetyGate.Evaluate(
                hardware,
                SystemState.Healthy,
                baseline,
                DateTimeOffset.UtcNow,
                fanWritePathPresent: true);

        if (!baselineSafety.CustomControlPermitted)
        {
            Console.Error.WriteLine(
                "M3 armer SafetyGate refused custom control:");
            foreach (var reason in baselineSafety.Reasons)
            {
                Console.Error.WriteLine(
                    $"  - {reason}");
            }

            return 145;
        }

        EnsureLightLoadEnvelope(baseline);

        var probe =
            new Hp8C40EcControlStateProbe(
                modulesDirectory);

        var before =
            probe.ReadControlEvidence();

        Console.WriteLine(
            $"EC baseline: {Format(before)}");

        if (before.CpuSetpoint != byte.MaxValue ||
            before.GpuSetpoint != byte.MaxValue)
        {
            Console.Error.WriteLine(
                "M3 armer requires clean firmware ownership FF/FF. " +
                $"Observed {before.CpuSetpoint}/{before.GpuSetpoint}.");
            return 146;
        }

        if (before.MaxFan != 0 ||
            before.FanSwitch != 0)
        {
            Console.Error.WriteLine(
                $"M3 armer guard refusal: MaxFan=0x{before.MaxFan:X2}, " +
                $"FanSwitch=0x{before.FanSwitch:X2}.");
            return 147;
        }

        var bios =
            new Hp8C40BiosFanControl();

        var writeMayHaveOccurred = false;
        var serviceRestoreVerified = false;
        var armRunId = Guid.NewGuid();
        var nonce = Guid.NewGuid();

        using var process =
            Process.GetCurrentProcess();

        try
        {
            Console.WriteLine(
                $"Applying one qualification command {RequiredLevel}/{RequiredLevel}...");

            writeMayHaveOccurred = true;
            bios.SetFanLevel(
                RequiredLevel,
                RequiredLevel);

            var armedState =
                await WaitForStableArmedStateAsync(
                    probe,
                    cancellationToken)
                .ConfigureAwait(false);

            EnsurePowerStatus(
                SystemPowerStatusReader.Read());

            var handoff =
                new Hp8C40M3HandoffRecord(
                    SchemaVersion:
                        Hp8C40M3HandoffRecord.CurrentSchemaVersion,
                    TargetProfileId:
                        Hp8C40TargetProfile.Instance.Id,
                    ArmRunId:
                        armRunId,
                    Nonce:
                        nonce,
                    ArmProcessId:
                        process.Id,
                    ArmProcessStartUtcTicks:
                        process.StartTime
                            .ToUniversalTime()
                            .Ticks,
                    CreatedAtUtc:
                        DateTimeOffset.UtcNow,
                    ExpectedCpuSetpoint:
                        RequiredLevel,
                    ExpectedGpuSetpoint:
                        RequiredLevel,
                    BaselineWasFirmwareOwned:
                        true,
                    MaxFanAtArm:
                        armedState.MaxFan,
                    FanSwitchAtArm:
                        armedState.FanSwitch,
                    CpuRpmAtArm:
                        armedState.CpuRpm,
                    GpuRpmAtArm:
                        armedState.GpuRpm);

            Hp8C40M3JsonFile.WriteHandoff(
                handoffFullPath,
                handoff);

            Console.WriteLine(
                $"M3 handoff published: run={armRunId}; nonce={nonce}");
            Console.WriteLine(
                $"Armed EC: {Format(armedState)}");
            Console.WriteLine(
                "Waiting for LocalSystem M3 restore-only result...");

            var result =
                await WaitForResultAsync(
                    resultFullPath,
                    armRunId,
                    nonce,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success ||
                !result.VerifiedFfFf)
            {
                throw new InvalidOperationException(
                    "M3 service did not report a verified firmware restore: " +
                    (result.Failure ?? "unknown failure"));
            }

            var finalState =
                await WaitForFirmwareOwnedAsync(
                    probe,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None)
                .ConfigureAwait(false);

            serviceRestoreVerified = true;

            Console.WriteLine(
                $"M3 service PASS. Final EC: {Format(finalState)}");
            Console.WriteLine(
                "PASS: one VFC-owned 30/30 state was restored by the " +
                "LocalSystem M3 service to verified FF/FF.");

            return 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "M3 armer cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"M3 armer failed: {ex.GetType().Name}: {ex.Message}");
            return 148;
        }
        finally
        {
            if (writeMayHaveOccurred &&
                !serviceRestoreVerified)
            {
                await EmergencyFallbackRestoreIfStillOwnedAsync(
                    probe,
                    bios)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<Hp8C40EcControlState>
        WaitForStableArmedStateAsync(
            Hp8C40EcControlStateProbe probe,
            CancellationToken cancellationToken)
    {
        var started =
            Stopwatch.GetTimestamp();

        var consecutive = 0;
        Hp8C40EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) <
               AckTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePowerStatus(
                SystemPowerStatusReader.Read());

            last =
                probe.ReadControlEvidence();

            if (last.CpuSetpoint == RequiredLevel &&
                last.GpuSetpoint == RequiredLevel &&
                last.MaxFan == 0 &&
                last.FanSwitch == 0 &&
                last.CpuRpm >= MinimumFanRpmForHandoff &&
                last.GpuRpm >= MinimumFanRpmForHandoff)
            {
                consecutive++;
                if (consecutive >=
                    RequiredStableSamples)
                {
                    return last;
                }
            }
            else
            {
                consecutive = 0;
            }

            await Task.Delay(
                    PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"M3 armer did not obtain stable {RequiredLevel}/{RequiredLevel} " +
            $"EC + dual-tach acknowledgement within {AckTimeout.TotalSeconds:0} s. " +
            $"Last={Format(last)}");
    }

    private static async Task<Hp8C40M3ServiceResult>
        WaitForResultAsync(
            string resultPath,
            Guid armRunId,
            Guid nonce,
            CancellationToken cancellationToken)
    {
        var started =
            Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) <
               ResultTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(resultPath))
            {
                try
                {
                    var result =
                        Hp8C40M3JsonFile.ReadResult(
                            resultPath);

                    if (result.ArmRunId != armRunId ||
                        result.Nonce != nonce)
                    {
                        throw new InvalidDataException(
                            "M3 result does not match the active handoff run/nonce.");
                    }

                    return result;
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(
                    PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"M3 service result was not observed within {ResultTimeout.TotalSeconds:0} s.");
    }

    private static async Task<Hp8C40EcControlState>
        WaitForFirmwareOwnedAsync(
            Hp8C40EcControlStateProbe probe,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        var started =
            Stopwatch.GetTimestamp();
        Hp8C40EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) <
               timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            last =
                probe.ReadControlEvidence();

            if (last.CpuSetpoint == byte.MaxValue &&
                last.GpuSetpoint == byte.MaxValue)
            {
                return last;
            }

            await Task.Delay(
                    PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "M3 final firmware ownership was not independently verified. " +
            $"Last={Format(last)}");
    }

    private static async Task EmergencyFallbackRestoreIfStillOwnedAsync(
        Hp8C40EcControlStateProbe probe,
        Hp8C40BiosFanControl bios)
    {
        try
        {
            var current =
                probe.ReadControlEvidence();

            if (current.CpuSetpoint == byte.MaxValue &&
                current.GpuSetpoint == byte.MaxValue)
            {
                Console.WriteLine(
                    "M3 fallback: firmware already owns FF/FF; no local restore write issued.");
                return;
            }

            if (current.CpuSetpoint != RequiredLevel ||
                current.GpuSetpoint != RequiredLevel)
            {
                Console.Error.WriteLine(
                    "M3 fallback REFUSED: ownership changed to an unexpected " +
                    $"{current.CpuSetpoint}/{current.GpuSetpoint}; preserving possible external owner.");
                return;
            }

            Console.Error.WriteLine(
                "M3 fallback: service did not complete while VFC-owned 30/30 " +
                "remains. Restoring locally to firmware.");

            bios.RestoreFirmwareAuto();

            _ =
                await WaitForFirmwareOwnedAsync(
                    probe,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Console.Error.WriteLine(
                "M3 fallback local restore verified FF/FF.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "CRITICAL: M3 emergency fallback could not verify firmware " +
                $"ownership: {ex.GetType().Name}: {ex.Message}");
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
                $"M3 AC/battery sanity gate refused: {status}");
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
                "M3 light-load envelope exceeded.");
        }
    }

    private static bool IsAdministrator()
    {
        using var identity =
            WindowsIdentity.GetCurrent();

        var principal =
            new WindowsPrincipal(identity);

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
