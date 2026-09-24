using VictusFanControl.Hardware.Hp;
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

            if (!safety.CustomControlPermitted)
            {
                return false;
            }

            if (!_backend.CanWrite)
            {
                return false;
            }

            if (!BackendCapabilitiesMatchTarget())
            {
                return false;
            }

            var entryAttempted = false;
            try
            {
                // Mark the attempt before entering the backend. A hardware write may
                // take effect even when the backend subsequently reports an error.
                entryAttempted = true;
                await _backend.EnterCustomModeAsync(cancellationToken).ConfigureAwait(false);
                Transition(FanAuthority.Custom, "Custom fan authority acquired.");
                return true;
            }
            catch
            {
                if (entryAttempted)
                {
                    await BestEffortForceRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }

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

            if (!safety.CustomControlPermitted)
            {
                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Fan command refused because custom control is no longer permitted by the safety gate.");
            }

            var commandError = ValidateCommand(command);
            if (commandError is not null)
            {
                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ArgumentOutOfRangeException(
                    nameof(command),
                    command,
                    commandError);
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


    private bool BackendCapabilitiesMatchTarget()
    {
        var capabilities = _backend.Capabilities;

        return
            string.Equals(
                capabilities.BoardProduct,
                Hp88F8TargetProfile.BoardProduct,
                StringComparison.OrdinalIgnoreCase) &&
            capabilities.MinimumLevel >= Hp88F8TargetProfile.MinimumValidatedFanLevel &&
            capabilities.MaximumLevel <= Hp88F8TargetProfile.MaximumValidatedFanLevel &&
            capabilities.MinimumLevel <= capabilities.MaximumLevel;
    }

    private string? ValidateCommand(FanCommand command)
    {
        if (command.CpuLevel < Hp88F8TargetProfile.MinimumValidatedFanLevel ||
            command.CpuLevel > Hp88F8TargetProfile.MaximumValidatedFanLevel ||
            command.GpuLevel < Hp88F8TargetProfile.MinimumValidatedFanLevel ||
            command.GpuLevel > Hp88F8TargetProfile.MaximumValidatedFanLevel)
        {
            return $"Command is outside the central 88F8 validated range " +
                   $"{Hp88F8TargetProfile.MinimumValidatedFanLevel}-" +
                   $"{Hp88F8TargetProfile.MaximumValidatedFanLevel}.";
        }

        var capabilities = _backend.Capabilities;

        if (command.CpuLevel < capabilities.MinimumLevel ||
            command.CpuLevel > capabilities.MaximumLevel)
        {
            return $"CPU fan level {command.CpuLevel} is outside validated range " +
                   $"{capabilities.MinimumLevel}-{capabilities.MaximumLevel}.";
        }

        if (command.GpuLevel < capabilities.MinimumLevel ||
            command.GpuLevel > capabilities.MaximumLevel)
        {
            return $"GPU fan level {command.GpuLevel} is outside validated range " +
                   $"{capabilities.MinimumLevel}-{capabilities.MaximumLevel}.";
        }

        if (!capabilities.SupportsIndependentLevels &&
            command.CpuLevel != command.GpuLevel)
        {
            return "Backend requires equal CPU/GPU fan levels.";
        }

        return null;
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


    private async ValueTask BestEffortForceRestoreLockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            Transition(
                FanAuthority.Restoring,
                "Fail-safe restore requested after an uncertain authority transition.");

            await _backend.RestoreFirmwareAutoAsync(cancellationToken).ConfigureAwait(false);
            Transition(FanAuthority.Firmware, "HP firmware authority restored.");
        }
        catch
        {
            Transition(FanAuthority.Faulted, "Firmware restore failed after uncertain authority transition.");
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
