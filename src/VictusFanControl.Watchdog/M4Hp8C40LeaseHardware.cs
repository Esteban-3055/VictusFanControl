using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

/// <summary>
/// HP 8C40 M4 lease-recovery adapter. It exposes only the narrow EC ownership
/// read and the M3-qualified firmware restore primitive. It has no ordinary
/// SetFanLevel authority.
/// </summary>
internal sealed class M4Hp8C40LeaseHardware :
    ILeaseRecoveryHardware
{
    internal const int EcMutexReadAttempts = 4;
    internal static readonly TimeSpan EcMutexRetryDelay =
        TimeSpan.FromMilliseconds(75);

    private readonly string _modulesDirectory;
    private readonly Hp8C40BiosFanControl _bios = new();

    public M4Hp8C40LeaseHardware(
        string modulesDirectory)
    {
        _modulesDirectory = modulesDirectory;
    }

    public async ValueTask<FanSetpoint> ReadSetpointAsync(
        CancellationToken cancellationToken)
    {
        Exception? lastContention = null;

        for (var attempt = 1;
             attempt <= EcMutexReadAttempts;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var setpoint =
                    new Hp8C40EcControlStateProbe(
                        _modulesDirectory).ReadSetpoint();

                return new FanSetpoint(
                    setpoint.CpuSetpoint,
                    setpoint.GpuSetpoint);
            }
            catch (TimeoutException ex)
                when (IsRetryableEcMutexContention(ex))
            {
                lastContention = ex;

                if (attempt >= EcMutexReadAttempts)
                {
                    break;
                }

                await Task.Delay(
                        EcMutexRetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            $"Timed out waiting for Global\\Access_EC after {EcMutexReadAttempts} bounded ownership-read attempts.",
            lastContention);
    }

    internal static bool IsRetryableEcMutexContention(
        Exception exception) =>
        exception.Message.Contains(
            @"Global\Access_EC",
            StringComparison.Ordinal);

    public ValueTask RestoreFirmwareAutoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _bios.RestoreFirmwareAuto();
        return ValueTask.CompletedTask;
    }
}
