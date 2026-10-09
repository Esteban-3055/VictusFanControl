using System.Diagnostics;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

internal interface IM3Hp8C40RestoreHardware
{
    Hp8C40EcControlState ReadControlEvidence();
    void RestoreFirmwareAuto();
    (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels();
}

internal sealed class M3Hp8C40RestoreHardware :
    IM3Hp8C40RestoreHardware
{
    private readonly Hp8C40EcControlStateProbe _probe;
    private readonly Hp8C40BiosFanControl _bios = new();

    public M3Hp8C40RestoreHardware(
        string modulesDirectory)
    {
        _probe =
            new Hp8C40EcControlStateProbe(
                modulesDirectory);
    }

    public Hp8C40EcControlState ReadControlEvidence() =>
        _probe.ReadControlEvidence();

    public void RestoreFirmwareAuto() =>
        _bios.RestoreFirmwareAuto();

    public (byte CpuLevel, byte GpuLevel)
        GetCurrentFanLevels() =>
        _bios.GetCurrentFanLevels();
}

internal interface IM3ArmProcessValidator
{
    bool Matches(
        int processId,
        long processStartUtcTicks,
        out string detail);
}

internal sealed class M3ArmProcessValidator :
    IM3ArmProcessValidator
{
    public bool Matches(
        int processId,
        long processStartUtcTicks,
        out string detail)
    {
        try
        {
            using var process =
                Process.GetProcessById(
                    processId);

            if (process.HasExited)
            {
                detail =
                    $"Armer process {processId} has exited.";
                return false;
            }

            var observedTicks =
                process.StartTime
                    .ToUniversalTime()
                    .Ticks;

            if (observedTicks !=
                processStartUtcTicks)
            {
                detail =
                    $"Armer PID {processId} start-time mismatch: " +
                    $"expected {processStartUtcTicks}, observed {observedTicks}.";
                return false;
            }

            detail =
                $"Armer PID {processId} is alive with matching start time.";
            return true;
        }
        catch (Exception ex)
        {
            detail =
                $"Armer process validation failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}

internal sealed record M3Hp8C40RestoreExecution(
    bool Success,
    bool HandoffValidated,
    bool ArmerIdentityValidated,
    Hp8C40EcControlState? Before,
    Hp8C40EcControlState? After,
    bool RestoreCallSucceeded,
    bool VerifiedFfFf,
    int? BiosCpuCurrentLevelAfter,
    int? BiosGpuCurrentLevelAfter,
    double ElapsedMilliseconds,
    string? Failure);

internal static class M3Hp8C40RestoreExecutor
{
    private const byte RequiredLevel =
        Hp8C40M3HandoffRecord.RequiredQualificationLevel;

    private const ushort MinimumFanRpm = 750;
    private const int RequiredPreRestoreSamples = 2;

    private static readonly TimeSpan MaximumHandoffAge =
        TimeSpan.FromSeconds(15);

    private static readonly TimeSpan MaximumFutureSkew =
        TimeSpan.FromSeconds(2);

    public static async Task<M3Hp8C40RestoreExecution> ExecuteAsync(
        Hp8C40M3HandoffRecord handoff,
        IM3Hp8C40RestoreHardware hardware,
        IM3ArmProcessValidator processValidator,
        DateTimeOffset nowUtc,
        TimeSpan verifyTimeout,
        TimeSpan pollInterval,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var started =
            Stopwatch.GetTimestamp();

        Hp8C40EcControlState? before = null;
        Hp8C40EcControlState? after = null;
        var restoreCallSucceeded = false;
        var verifiedFfFf = false;
        var handoffValidated = false;
        var armerIdentityValidated = false;
        int? biosCpuAfter = null;
        int? biosGpuAfter = null;
        Exception? restoreFailure = null;
        Exception? lastReadFailure = null;

        try
        {
            ValidateHandoff(
                handoff,
                nowUtc);

            handoffValidated = true;

            if (!processValidator.Matches(
                    handoff.ArmProcessId,
                    handoff.ArmProcessStartUtcTicks,
                    out var processDetail))
            {
                return Fail(
                    $"M3 restore refused because live armer identity was not validated: {processDetail}");
            }

            armerIdentityValidated = true;
            log?.Invoke(
                $"M3 armer identity PASS: {processDetail}");

            var consecutive = 0;

            while (consecutive <
                   RequiredPreRestoreSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();

                before =
                    hardware.ReadControlEvidence();

                log?.Invoke(
                    $"M3 pre-restore EC sample {consecutive + 1}/{RequiredPreRestoreSamples}: {before}");

                if (before.CpuSetpoint != RequiredLevel ||
                    before.GpuSetpoint != RequiredLevel)
                {
                    return Fail(
                        "M3 restore refused because EC no longer matches the " +
                        $"known VFC-owned {RequiredLevel}/{RequiredLevel} handoff; " +
                        $"read {before.CpuSetpoint}/{before.GpuSetpoint}. " +
                        "No restore write was issued.");
                }

                if (before.MaxFan != 0 ||
                    before.FanSwitch != 0)
                {
                    return Fail(
                        $"M3 restore refused because control guards changed: " +
                        $"MaxFan=0x{before.MaxFan:X2}, FanSwitch=0x{before.FanSwitch:X2}. " +
                        "No restore write was issued.");
                }

                if (before.CpuRpm < MinimumFanRpm ||
                    before.GpuRpm < MinimumFanRpm)
                {
                    return Fail(
                        $"M3 restore refused because dual-fan feedback is implausible for " +
                        $"{RequiredLevel}/{RequiredLevel}: CPU={before.CpuRpm}, GPU={before.GpuRpm} RPM. " +
                        "No restore write was issued.");
                }

                consecutive++;

                if (consecutive <
                    RequiredPreRestoreSamples)
                {
                    await Task.Delay(
                            pollInterval,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                hardware.RestoreFirmwareAuto();
                restoreCallSucceeded = true;

                log?.Invoke(
                    "M3 service issued exactly one HP restore transaction: FF/FF release -> LegacyDefault.");
            }
            catch (Exception ex)
            {
                restoreFailure = ex;
                log?.Invoke(
                    $"M3 HP restore transaction reported failure: {ex.GetType().Name}: {ex.Message}");
            }

            var verifyStarted =
                Stopwatch.GetTimestamp();

            while (Stopwatch.GetElapsedTime(
                       verifyStarted) <
                   verifyTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    after =
                        hardware.ReadControlEvidence();
                    lastReadFailure = null;

                    if (after.CpuSetpoint ==
                            byte.MaxValue &&
                        after.GpuSetpoint ==
                            byte.MaxValue)
                    {
                        verifiedFfFf = true;
                        break;
                    }

                    if (after.CpuSetpoint !=
                            RequiredLevel ||
                        after.GpuSetpoint !=
                            RequiredLevel)
                    {
                        return Fail(
                            "M3 post-restore verification observed an unexpected fixed " +
                            $"setpoint {after.CpuSetpoint}/{after.GpuSetpoint}. " +
                            "Possible external ownership is preserved; no second restore is issued.");
                    }
                }
                catch (Exception ex)
                {
                    lastReadFailure = ex;
                }

                await Task.Delay(
                        pollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (verifiedFfFf)
            {
                log?.Invoke(
                    $"M3 EC FF/FF verification PASS: {after}");

                try
                {
                    var levels =
                        hardware.GetCurrentFanLevels();

                    biosCpuAfter =
                        levels.CpuLevel;
                    biosGpuAfter =
                        levels.GpuLevel;

                    log?.Invoke(
                        $"M3 informational GetFanLevel after restore: " +
                        $"CPU={levels.CpuLevel} GPU={levels.GpuLevel}");
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        $"M3 informational GetFanLevel read failed: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (restoreFailure is not null)
            {
                return Fail(
                    verifiedFfFf
                        ? "EC reached FF/FF, but the single HP restore transaction " +
                          $"reported failure: {restoreFailure.Message}"
                        : "HP restore transaction failed and FF/FF was not verified: " +
                          restoreFailure.Message);
            }

            if (!verifiedFfFf)
            {
                var detail =
                    lastReadFailure is null
                        ? after is null
                            ? "no post-restore EC sample was captured"
                            : $"last EC setpoint={after.CpuSetpoint}/{after.GpuSetpoint}"
                        : $"last EC read failed: {lastReadFailure.Message}";

                return Fail(
                    $"M3 HP restore returned success but FF/FF was not verified " +
                    $"within {verifyTimeout.TotalSeconds:0.0} s ({detail}).");
            }

            return new M3Hp8C40RestoreExecution(
                Success: true,
                HandoffValidated:
                    handoffValidated,
                ArmerIdentityValidated:
                    armerIdentityValidated,
                Before:
                    before,
                After:
                    after,
                RestoreCallSucceeded:
                    restoreCallSucceeded,
                VerifiedFfFf:
                    true,
                BiosCpuCurrentLevelAfter:
                    biosCpuAfter,
                BiosGpuCurrentLevelAfter:
                    biosGpuAfter,
                ElapsedMilliseconds:
                    Stopwatch.GetElapsedTime(
                        started).TotalMilliseconds,
                Failure:
                    null);
        }
        catch (Exception ex)
        {
            return Fail(
                $"{ex.GetType().Name}: {ex.Message}");
        }

        M3Hp8C40RestoreExecution Fail(
            string failure) =>
            new(
                Success: false,
                HandoffValidated:
                    handoffValidated,
                ArmerIdentityValidated:
                    armerIdentityValidated,
                Before:
                    before,
                After:
                    after,
                RestoreCallSucceeded:
                    restoreCallSucceeded,
                VerifiedFfFf:
                    verifiedFfFf,
                BiosCpuCurrentLevelAfter:
                    biosCpuAfter,
                BiosGpuCurrentLevelAfter:
                    biosGpuAfter,
                ElapsedMilliseconds:
                    Stopwatch.GetElapsedTime(
                        started).TotalMilliseconds,
                Failure:
                    failure);
    }

    private static void ValidateHandoff(
        Hp8C40M3HandoffRecord handoff,
        DateTimeOffset nowUtc)
    {
        if (handoff.SchemaVersion !=
            Hp8C40M3HandoffRecord.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported M3 handoff schema {handoff.SchemaVersion}.");
        }

        if (!string.Equals(
                handoff.TargetProfileId,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"M3 handoff target '{handoff.TargetProfileId}' does not match " +
                $"'{Hp8C40TargetProfile.Instance.Id}'.");
        }

        if (handoff.ArmRunId == Guid.Empty ||
            handoff.Nonce == Guid.Empty ||
            handoff.ArmProcessId <= 0 ||
            handoff.ArmProcessStartUtcTicks <= 0)
        {
            throw new InvalidDataException(
                "M3 handoff has invalid run/process identity.");
        }

        if (handoff.ExpectedCpuSetpoint !=
                RequiredLevel ||
            handoff.ExpectedGpuSetpoint !=
                RequiredLevel)
        {
            throw new InvalidDataException(
                "M3 handoff is not armed for the only qualified restore source " +
                $"{RequiredLevel}/{RequiredLevel}.");
        }

        if (!handoff.BaselineWasFirmwareOwned)
        {
            throw new InvalidDataException(
                "M3 handoff does not prove a clean FF/FF baseline before VFC ownership.");
        }

        if (handoff.MaxFanAtArm != 0 ||
            handoff.FanSwitchAtArm != 0 ||
            handoff.CpuRpmAtArm <
                MinimumFanRpm ||
            handoff.GpuRpmAtArm <
                MinimumFanRpm)
        {
            throw new InvalidDataException(
                "M3 handoff arm evidence is outside the qualified control envelope.");
        }

        var age =
            nowUtc -
            handoff.CreatedAtUtc;

        if (age < -MaximumFutureSkew ||
            age > MaximumHandoffAge)
        {
            throw new InvalidDataException(
                $"M3 handoff age {age.TotalSeconds:0.000} s is outside the " +
                $"allowed window (-{MaximumFutureSkew.TotalSeconds:0}..{MaximumHandoffAge.TotalSeconds:0} s).");
        }
    }
}
