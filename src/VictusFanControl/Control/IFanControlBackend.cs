namespace VictusFanControl.Control;

public readonly record struct FanCommand(
    int CpuLevel,
    int GpuLevel,
    string Reason);

public readonly record struct FanBackendCapabilities(
    string BoardProduct,
    int MinimumLevel,
    int MaximumLevel,
    bool SupportsIndependentLevels);

public readonly record struct FanBackendStatus(
    string Name,
    bool CanWrite,
    bool CustomModeActive,
    bool OwnershipValid,
    bool FeedbackHealthy,
    string Detail);

/// <summary>
/// Immutable evidence captured by a backend when a firmware restore finishes.
/// The Gate G suspend path reads this in-memory record instead of opening any
/// extra EC/WMI/journal reader after the critical restore has already completed.
/// </summary>
public readonly record struct FanFirmwareRestoreEvidence(
    bool LocalFirmwareAckVerified,
    bool WatchdogLeaseRequired,
    bool WatchdogReleaseVerified,
    DateTimeOffset CompletedAtUtc,
    string Detail);

/// <summary>
/// Optional capability implemented only by backends that can expose causal
/// restore evidence without performing another hardware or IPC transaction.
/// </summary>
public interface IFanControlRestoreEvidenceSource
{
    FanFirmwareRestoreEvidence LastRestoreEvidence { get; }
}

/// <summary>
/// Signals that custom authority admission failed before the backend performed
/// any fan write. The coordinator must not issue a compensating FF,FF restore,
/// because doing so could clear a state owned by another controller.
/// </summary>
public class FanControlAdmissionException : InvalidOperationException
{
    public FanControlAdmissionException(string message) : base(message)
    {
    }

    public FanControlAdmissionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A no-write admission failure specifically caused by another hardware or
/// firmware state already owning the fan path.
/// </summary>
public sealed class FanControlOwnershipConflictException : FanControlAdmissionException
{
    public FanControlOwnershipConflictException(string message) : base(message)
    {
    }
}

/// <summary>
/// Narrow fan-control boundary. Production controller code cannot perform
/// arbitrary EC writes through this interface.
/// </summary>
public interface IFanControlBackend : IAsyncDisposable
{
    string Name { get; }
    bool CanWrite { get; }
    FanBackendCapabilities Capabilities { get; }

    /// <summary>
    /// Probes control dependencies that must remain alive while Custom
    /// authority is active. The normal successful path must not access local EC,
    /// issue ordinary fan commands, or renew watchdog heartbeat. An already
    /// expired watchdog lease may still invoke the watchdog's existing
    /// fail-safe recovery before refusing the probe.
    /// </summary>
    ValueTask ProbeControlDependencyAsync(CancellationToken cancellationToken);

    ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken cancellationToken);
    ValueTask EnterCustomModeAsync(CancellationToken cancellationToken);
    ValueTask ApplyAsync(FanCommand command, CancellationToken cancellationToken);
    ValueTask RestoreFirmwareAutoAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fail-closed fallback used whenever the validated HP backend cannot be
/// constructed. Every write operation fails closed.
/// </summary>
public sealed class DisabledFanControlBackend : IFanControlBackend
{
    public string Name => "Disabled / read-only";
    public bool CanWrite => false;
    public FanBackendCapabilities Capabilities =>
        new("88F8", 14, 50, SupportsIndependentLevels: true);

    public ValueTask ProbeControlDependencyAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new FanBackendStatus(
            Name,
            CanWrite: false,
            CustomModeActive: false,
            OwnershipValid: true,
            FeedbackHealthy: true,
            Detail: "No fan write/restore implementation is present."));

    public ValueTask EnterCustomModeAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new InvalidOperationException(
            "Fan control is hard-disabled in this build."));

    public ValueTask ApplyAsync(FanCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromException(new InvalidOperationException(
            "Fan control is hard-disabled in this build."));

    public ValueTask RestoreFirmwareAutoAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
