namespace VictusFanControl.Watchdog;

internal enum WatchdogLeasePhase
{
    Prepared,
    WriteArmed,
    Owned,
    Restoring
}

internal readonly record struct FanSetpoint(byte Cpu, byte Gpu)
{
    public bool IsFirmwareOwned =>
        Cpu == byte.MaxValue &&
        Gpu == byte.MaxValue;

    public override string ToString() => $"{Cpu}/{Gpu}";
}

internal sealed record ControllerIdentity(
    int ProcessId,
    long ProcessStartUtcTicks);

internal sealed record WatchdogLeaseRecord(
    int SchemaVersion,
    string? TargetProfileId,
    Guid SessionId,
    ControllerIdentity Controller,
    WatchdogLeasePhase Phase,
    long Generation,
    FanSetpoint? PreviousOwned,
    FanSetpoint? Pending,
    FanSetpoint? Owned,
    DateTimeOffset CreatedAtUtc)
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;
}

internal sealed record LeaseOperationResult(
    Guid SessionId,
    long Generation,
    WatchdogLeasePhase Phase);

internal enum LeaseRecoveryDisposition
{
    Ready,
    ClearedPrepared,
    RestoredFirmware,
    ExternalOverrideBlocked,
    OwnershipAmbiguous,
    JournalInvalid,
    RestoreFailed
}

internal sealed record LeaseRecoveryResult(
    LeaseRecoveryDisposition Disposition,
    FanSetpoint Observed,
    bool RestoreAttempted,
    bool JournalRetained,
    string Detail);
