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

    public bool IsValidatedCustom =>
        Cpu is >= 14 and <= 50 &&
        Gpu is >= 14 and <= 50;

    public override string ToString() => $"{Cpu}/{Gpu}";
}

internal sealed record ControllerIdentity(
    int ProcessId,
    long ProcessStartUtcTicks);

internal sealed record WatchdogLeaseRecord(
    int SchemaVersion,
    Guid SessionId,
    ControllerIdentity Controller,
    WatchdogLeasePhase Phase,
    long Generation,
    FanSetpoint? PreviousOwned,
    FanSetpoint? Pending,
    FanSetpoint? Owned,
    DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

internal sealed record LeaseOperationResult(
    Guid SessionId,
    long Generation,
    WatchdogLeasePhase Phase);

internal enum LeaseRecoveryDisposition
{
    Ready,
    ClearedPrepared,
    ClearedAlreadyFirmware,
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
