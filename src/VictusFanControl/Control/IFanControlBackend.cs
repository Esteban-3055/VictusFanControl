namespace VictusFanControl.Control;

public readonly record struct FanCommand(
    int CpuLevel,
    int GpuLevel,
    string Reason);

public readonly record struct FanBackendStatus(
    string Name,
    bool CanWrite,
    bool CustomModeActive,
    string Detail);

/// <summary>
/// Narrow fan-control boundary. Production controller code cannot perform
/// arbitrary EC writes through this interface.
/// </summary>
public interface IFanControlBackend : IAsyncDisposable
{
    string Name { get; }
    bool CanWrite { get; }

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

    public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new FanBackendStatus(
            Name,
            CanWrite: false,
            CustomModeActive: false,
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
