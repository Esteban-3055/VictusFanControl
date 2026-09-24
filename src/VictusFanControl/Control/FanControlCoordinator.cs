using VictusFanControl.Safety;

namespace VictusFanControl.Control;

public enum FanAuthority
{
    Firmware,
    Custom,
    Restoring,
    Faulted
}

public sealed record FanAuthorityChangedEventArgs(
    FanAuthority Previous,
    FanAuthority Current,
    string Reason,
    DateTimeOffset Timestamp);

/// <summary>
/// Owns fan-control authority transitions. The adaptive policy never talks
/// directly to a hardware backend; all commands pass through this coordinator.
/// </summary>
public sealed class FanControlCoordinator : IAsyncDisposable
{
    private readonly IFanControlBackend _backend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FanAuthority _authority = FanAuthority.Firmware;
    private bool _disposed;

    public FanControlCoordinator(IFanControlBackend backend)
    {
        _backend = backend;
    }

    public event EventHandler<FanAuthorityChangedEventArgs>? AuthorityChanged;

    public FanAuthority Authority => _authority;
    public string BackendName => _backend.Name;
    public bool BackendCanWrite => _backend.CanWrite;

    public async ValueTask<bool> TryEnterCustomAsync(
        SafetyGateResult safety,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_authority == FanAuthority.Custom)
            {
                return true;
            }

            if (!safety.PreconditionsReady)
            {
                return false;
            }

            if (!_backend.CanWrite)
            {
                return false;
            }

            try
            {
                await _backend.EnterCustomModeAsync(cancellationToken).ConfigureAwait(false);
                Transition(FanAuthority.Custom, "Custom fan authority acquired.");
                return true;
            }
            catch
            {
                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ApplyAsync(
        FanCommand command,
        SafetyGateResult safety,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "Fan command refused because custom authority is not active.");
            }

            if (!safety.PreconditionsReady)
            {
                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Fan command refused because the safety gate is no longer ready.");
            }

            try
            {
                await _backend.ApplyAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RestoreFirmwareAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await RestoreLockedAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask RestoreLockedAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_authority == FanAuthority.Firmware)
        {
            return;
        }

        Transition(FanAuthority.Restoring, reason);

        try
        {
            await _backend.RestoreFirmwareAutoAsync(cancellationToken).ConfigureAwait(false);
            Transition(FanAuthority.Firmware, "HP firmware authority restored.");
        }
        catch
        {
            Transition(FanAuthority.Faulted, "Firmware restore failed.");
            throw;
        }
    }

    private async ValueTask BestEffortRestoreLockedAsync(CancellationToken cancellationToken)
    {
        if (_authority == FanAuthority.Firmware)
        {
            return;
        }

        try
        {
            await RestoreLockedAsync(
                "Fail-safe restore requested.",
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Authority is left Faulted. The caller's original failure remains
            // authoritative, while production logging records restore failure.
        }
    }

    private void Transition(FanAuthority next, string reason)
    {
        var previous = _authority;
        _authority = next;

        AuthorityChanged?.Invoke(
            this,
            new FanAuthorityChangedEventArgs(
                previous,
                next,
                reason,
                DateTimeOffset.UtcNow));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
