using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Historical HP 88F8 Gate D recovery adapter. The lease manager itself is
/// target-neutral; this concrete adapter intentionally exposes only read
/// setpoint and the fixed HP-auto restore primitive physically qualified on
/// 88F8. HP 8C40 receives no service-side restore authority in M1.
/// </summary>
internal sealed class GateDLeaseHardware : ILeaseRecoveryHardware
{
    private readonly string _modulesDirectory;
    private readonly Hp88F8BiosFanControl _bios = new();

    public GateDLeaseHardware(string modulesDirectory)
    {
        _modulesDirectory = modulesDirectory;
    }

    public ValueTask<FanSetpoint> ReadSetpointAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var setpoint =
            new Hp88F8EcControlStateProbe(
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
