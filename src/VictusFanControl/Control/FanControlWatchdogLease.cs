namespace VictusFanControl.Control;

/// <summary>
/// Shared wire-contract constants for the local watchdog lease protocol.
/// The watchdog service remains unable to issue ordinary fan targets; these
/// messages only fence controller writes and hand firmware authority back.
/// </summary>
public static class FanControlWatchdogLeaseContract
{
    public const int ProtocolVersion = 1;
    public const int MaximumFrameBytes = 16 * 1024;
    public const string PipeName = "VictusFanControl.Watchdog.Control.v1";

    public const string Hello = "Hello";
    public const string Prepare = "Prepare";
    public const string CancelPrepared = "CancelPrepared";
    public const string WriteIntent = "WriteIntent";
    public const string AbortWriteIntent = "AbortWriteIntent";
    public const string Commit = "Commit";
    public const string Heartbeat = "Heartbeat";
    public const string RestoreBegin = "RestoreBegin";
    public const string Release = "Release";
}

/// <summary>
/// Controller-side lease boundary consumed by the HP backend. Implementations
/// may use IPC, but the backend is deliberately unaware of protocol/session
/// details. A production backend with this dependency cannot dispatch a new
/// SetFanLevel write until WriteIntentAsync has returned successfully.
/// </summary>
public interface IFanControlWatchdogLeaseClient : IAsyncDisposable
{
    ValueTask PrepareAsync(CancellationToken cancellationToken);

    ValueTask CancelPreparedAsync(CancellationToken cancellationToken);

    ValueTask WriteIntentAsync(
        int cpuLevel,
        int gpuLevel,
        CancellationToken cancellationToken);

    ValueTask AbortWriteIntentAsync(
        CancellationToken cancellationToken);

    ValueTask CommitAsync(
        int cpuLevel,
        int gpuLevel,
        CancellationToken cancellationToken);

    ValueTask HeartbeatAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Arms watchdog takeover for the controller's normal firmware handoff.
    /// Implementations must treat PREPARED as a no-write cancellation and may
    /// no-op when no lease exists.
    /// </summary>
    ValueTask RestoreBeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Called only after the controller's local HP restore was verified. The
    /// service must independently normalize/verify firmware authority before
    /// deleting its durable lease.
    /// </summary>
    ValueTask ReleaseAsync(CancellationToken cancellationToken);
}
