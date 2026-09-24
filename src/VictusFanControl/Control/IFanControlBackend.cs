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
    string Detail);

/// <summary>
/// Signals that custom authority could not be acquired because another
/// hardware/firmware state already owns the fan path. The backend guarantees
/// this exception is raised before it performs a fan write, so the coordinator
/// must not clear the competing state as part of failed admission.
/// </summary>
public sealed class FanControlOwnershipConflictException : InvalidOperationException
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

    ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken cancellationToken);
    ValueTask EnterCustomModeAsync(CancellationToken cancellationToken);
    ValueTask ApplyAsync(FanCommand command, CancellationToken cancellationToken);
    ValueTask RestoreFirmwareAutoAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Default production backend until the HP 88F8 write/restore path is
/// separately implemented and validated. Every write operation fails closed.
/// </summary>
public sealed class DisabledFanControlBackend : IFanControlBackend
{
    public string Name => "Disabled / read-only";
    public bool CanWrite => false;
    public FanBackendCapabilities Capabilities =>
        new("88F8", 14, 50, SupportsIndependentLevels: true);

    public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new FanBackendStatus(
            Name,
            CanWrite: false,
            CustomModeActive: false,
            OwnershipValid: true,
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
