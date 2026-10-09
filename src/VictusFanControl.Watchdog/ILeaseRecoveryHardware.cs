namespace VictusFanControl.Watchdog;

internal interface ILeaseRecoveryHardware
{
    ValueTask<FanSetpoint> ReadSetpointAsync(
        CancellationToken cancellationToken);

    ValueTask RestoreFirmwareAutoAsync(
        CancellationToken cancellationToken);
}
