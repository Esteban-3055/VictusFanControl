using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Gate D hardware adapter. It intentionally exposes only read setpoint and
/// the fixed HP-auto restore primitive required by the lease manager.
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

        var state =
            new Hp88F8EcControlStateProbe(
                _modulesDirectory).Read();

        return ValueTask.FromResult(
            new FanSetpoint(
                state.CpuSetpoint,
                state.GpuSetpoint));
    }

    public ValueTask RestoreFirmwareAutoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _bios.RestoreFirmwareAuto();
        return ValueTask.CompletedTask;
    }
}
