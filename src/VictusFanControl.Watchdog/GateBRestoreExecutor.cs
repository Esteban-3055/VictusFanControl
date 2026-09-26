using System.Diagnostics;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

internal interface IGateBRestoreHardware
{
    Hp88F8EcControlState ReadEcState();
    void RestoreFirmwareAuto();
    (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels();
}

internal sealed class GateBRestoreHardware : IGateBRestoreHardware
{
    private readonly string _modulesDirectory;
    private readonly Hp88F8BiosFanControl _bios = new();

    public GateBRestoreHardware(string modulesDirectory)
    {
        _modulesDirectory = modulesDirectory;
    }

    public Hp88F8EcControlState ReadEcState() =>
        new Hp88F8EcControlStateProbe(_modulesDirectory).Read();

    public void RestoreFirmwareAuto() =>
        _bios.RestoreFirmwareAuto();

    public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
        _bios.GetCurrentFanLevels();
}

internal sealed record GateBRestoreExecution(
    bool Success,
    Hp88F8EcControlState? Before,
    Hp88F8EcControlState? After,
    bool RestoreCallSucceeded,
    bool VerifiedFfFf,
    int? BiosCpuCurrentLevelAfter,
    int? BiosGpuCurrentLevelAfter,
    double ElapsedMilliseconds,
    string? Failure);

internal static class GateBRestoreExecutor
{
    public const byte RequiredTestLevel = 30;

    public static async Task<GateBRestoreExecution> ExecuteAsync(
        IGateBRestoreHardware hardware,
        TimeSpan verifyTimeout,
        TimeSpan pollInterval,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Hp88F8EcControlState? before = null;
        Hp88F8EcControlState? after = null;
        var restoreCallSucceeded = false;
        var verifiedFfFf = false;
        Exception? restoreFailure = null;
        Exception? lastReadFailure = null;
        int? biosCpuAfter = null;
        int? biosGpuAfter = null;

        try
        {
            before = hardware.ReadEcState();
            log?.Invoke($"Gate B pre-restore EC: {before}");

            if (before.CpuSetpoint != RequiredTestLevel ||
                before.GpuSetpoint != RequiredTestLevel)
            {
                return Fail(
                    before,
                    after,
                    restoreCallSucceeded,
                    verifiedFfFf,
                    biosCpuAfter,
                    biosGpuAfter,
                    started,
                    $"Gate B restore refused because EC is not the explicitly armed " +
                    $"{RequiredTestLevel}/{RequiredTestLevel} test setpoint; read " +
                    $"{before.CpuSetpoint}/{before.GpuSetpoint}.");
            }

            if (before.MaxFan != 0 || before.FanSwitch != 0)
            {
                return Fail(
                    before,
                    after,
                    restoreCallSucceeded,
                    verifiedFfFf,
                    biosCpuAfter,
                    biosGpuAfter,
                    started,
                    $"Gate B restore refused because active control-state is outside the " +
                    $"validated envelope: max=0x{before.MaxFan:X2}, switch=0x{before.FanSwitch:X2}.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                hardware.RestoreFirmwareAuto();
                restoreCallSucceeded = true;
                log?.Invoke(
                    "Gate B HP BIOS/WMI restore call returned success for FF,FF -> LegacyDefault.");
            }
            catch (Exception ex)
            {
                // HP commands can take effect even when their call reports an
                // error. Continue read-only EC verification before classifying.
                restoreFailure = ex;
                log?.Invoke(
                    $"Gate B HP BIOS/WMI restore call reported failure: {ex.GetType().Name}: {ex.Message}");
            }

            var verifyStarted = Stopwatch.GetTimestamp();

            while (Stopwatch.GetElapsedTime(verifyStarted) < verifyTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    after = hardware.ReadEcState();
                    lastReadFailure = null;

                    if (after.CpuSetpoint == byte.MaxValue &&
                        after.GpuSetpoint == byte.MaxValue)
                    {
                        verifiedFfFf = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lastReadFailure = ex;
                }

                await Task.Delay(pollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (verifiedFfFf)
            {
                log?.Invoke($"Gate B EC FF/FF verification PASS: {after}");

                try
                {
                    var levels = hardware.GetCurrentFanLevels();
                    biosCpuAfter = levels.CpuLevel;
                    biosGpuAfter = levels.GpuLevel;
                    log?.Invoke(
                        $"Gate B informational GetFanLevel after restore: " +
                        $"CPU={levels.CpuLevel} GPU={levels.GpuLevel}");
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        $"Gate B post-restore GetFanLevel informational read failed: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (restoreFailure is not null)
            {
                return Fail(
                    before,
                    after,
                    restoreCallSucceeded,
                    verifiedFfFf,
                    biosCpuAfter,
                    biosGpuAfter,
                    started,
                    verifiedFfFf
                        ? $"Hardware reached FF/FF, but the HP restore call reported failure: {restoreFailure.Message}"
                        : $"HP restore call failed and FF/FF was not verified: {restoreFailure.Message}");
            }

            if (!verifiedFfFf)
            {
                var detail = lastReadFailure is null
                    ? after is null
                        ? "no EC snapshot was captured"
                        : $"last EC setpoint={after.CpuSetpoint}/{after.GpuSetpoint}"
                    : $"last EC read failed: {lastReadFailure.Message}";

                return Fail(
                    before,
                    after,
                    restoreCallSucceeded,
                    verifiedFfFf,
                    biosCpuAfter,
                    biosGpuAfter,
                    started,
                    $"HP restore call returned success but EC FF/FF was not verified " +
                    $"within {verifyTimeout.TotalSeconds:0.0} s ({detail}).");
            }

            return new GateBRestoreExecution(
                Success: true,
                Before: before,
                After: after,
                RestoreCallSucceeded: true,
                VerifiedFfFf: true,
                BiosCpuCurrentLevelAfter: biosCpuAfter,
                BiosGpuCurrentLevelAfter: biosGpuAfter,
                ElapsedMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Failure: null);
        }
        catch (Exception ex)
        {
            return Fail(
                before,
                after,
                restoreCallSucceeded,
                verifiedFfFf,
                biosCpuAfter,
                biosGpuAfter,
                started,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static GateBRestoreExecution Fail(
        Hp88F8EcControlState? before,
        Hp88F8EcControlState? after,
        bool restoreCallSucceeded,
        bool verifiedFfFf,
        int? biosCpuAfter,
        int? biosGpuAfter,
        long started,
        string failure) =>
        new(
            Success: false,
            Before: before,
            After: after,
            RestoreCallSucceeded: restoreCallSucceeded,
            VerifiedFfFf: verifiedFfFf,
            BiosCpuCurrentLevelAfter: biosCpuAfter,
            BiosGpuCurrentLevelAfter: biosGpuAfter,
            ElapsedMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Failure: failure);
}
