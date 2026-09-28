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
    private readonly string _modulesDirectory;
    private readonly Hp8C40BiosFanControl _bios = new();

    public M4Hp8C40LeaseHardware(
        string modulesDirectory)
    {
        _modulesDirectory = modulesDirectory;
    }

    public ValueTask<FanSetpoint> ReadSetpointAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var setpoint =
            new Hp8C40EcControlStateProbe(
                _modulesDirectory).ReadSetpoint();

        return ValueTask.FromResult(
            new FanSetpoint(
                setpoint.CpuSetpoint,
                setpoint.GpuSetpoint));
    }

    public ValueTask RestoreFirmwareAutoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _bios.RestoreFirmwareAuto();
        return ValueTask.CompletedTask;
    }
}
