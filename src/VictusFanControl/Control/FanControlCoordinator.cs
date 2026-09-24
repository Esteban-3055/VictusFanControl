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
    private readonly object _activeOperationGate = new();
    private FanAuthority _authority = FanAuthority.Firmware;
    private bool _admissionBlocked;
    private DateTimeOffset _minimumSafetySnapshotTimestamp = DateTimeOffset.MinValue;
    private CancellationTokenSource? _activeCommandCts;
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
                if (SafetyAllowsCustomLocked(safety))
                {
                    return true;
                }

                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            if (!SafetyAllowsCustomLocked(safety))
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
            catch (FanControlOwnershipConflictException)
            {
                // The backend explicitly guarantees no fan write occurred.
                // Do not clear another controller's pre-existing override.
                throw;
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

            if (!SafetyAllowsCustomLocked(safety))
            {
                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Fan command refused because custom control is no longer permitted by the current safety/lifecycle gate.");
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

            using var commandCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            SetActiveCommand(commandCts);
            try
            {
                await _backend.ApplyAsync(command, commandCts.Token).ConfigureAwait(false);
            }
            catch
            {
                await BestEffortRestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            finally
            {
                ClearActiveCommand(commandCts);
            }
        }
        finally
        {
            _gate.Release();
        }
    }


    /// <summary>
    /// Closes custom-control admission at a power/lifecycle boundary and restores
    /// firmware authority before returning. The timestamp becomes a freshness
    /// fence: pre-boundary SafetyGate results can never reacquire authority.
    /// </summary>
    public async ValueTask BlockCustomAdmissionAndRestoreAsync(
        string reason,
        DateTimeOffset boundaryTimestamp,
        CancellationToken cancellationToken)
    {
        // Do this before waiting for the coordinator gate: an in-flight backend
        // acknowledgement must not delay a suspend/emergency handoff for its
        // full timeout.
        CancelActiveCommand();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            _admissionBlocked = true;
            if (boundaryTimestamp > _minimumSafetySnapshotTimestamp)
            {
                _minimumSafetySnapshotTimestamp = boundaryTimestamp;
            }

            if (_authority != FanAuthority.Firmware)
            {
                await RestoreLockedAsync(reason, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reopens admission only after recovery has produced a post-boundary
    /// telemetry sample. The caller still needs a fresh SafetyGate result for
    /// TryEnterCustomAsync.
    /// </summary>
    public async ValueTask<bool> AllowCustomAdmissionAfterRecoveryAsync(
        DateTimeOffset validatedSnapshotTimestamp,
        string reason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_authority != FanAuthority.Firmware)
            {
                return false;
            }

            if (validatedSnapshotTimestamp < _minimumSafetySnapshotTimestamp)
            {
                return false;
            }

            _admissionBlocked = false;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }


    /// <summary>
    /// Called continuously by the runtime safety supervisor. If custom
    /// authority is active and the latest safety result no longer permits it,
    /// any in-flight command is cancelled and HP firmware is restored.
    /// </summary>
    public async ValueTask<bool> EnforceSafetyAsync(
        SafetyGateResult safety,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!safety.CustomControlPermitted)
        {
            CancelActiveCommand();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_authority != FanAuthority.Custom)
            {
                return true;
            }

            if (!SafetyAllowsCustomLocked(safety))
            {
                await RestoreLockedAsync(
                    $"Safety supervisor handoff: {reason}",
                    CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            FanBackendStatus backendStatus;
            try
            {
                backendStatus = await _backend.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception statusFailure)
            {
                try
                {
                    await RestoreLockedAsync(
                        $"Backend health/ownership probe failed during custom authority: {statusFailure.Message}",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // RestoreLockedAsync already transitions authority to Faulted.
                }

                throw;
            }

            if (!backendStatus.CanWrite ||
                !backendStatus.CustomModeActive ||
                !backendStatus.OwnershipValid)
            {
                await RestoreLockedAsync(
                    $"Backend ownership validation failed: {backendStatus.Detail}",
                    CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            return true;
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

        CancelActiveCommand();
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



    private bool SafetyAllowsCustomLocked(SafetyGateResult safety) =>
        safety.CustomControlPermitted &&
        !_admissionBlocked &&
        safety.SnapshotTimestamp.HasValue &&
        safety.SnapshotTimestamp.Value >= _minimumSafetySnapshotTimestamp;

    private void SetActiveCommand(CancellationTokenSource source)
    {
        lock (_activeOperationGate)
        {
            _activeCommandCts = source;
        }
    }

    private void ClearActiveCommand(CancellationTokenSource source)
    {
        lock (_activeOperationGate)
        {
            if (ReferenceEquals(_activeCommandCts, source))
            {
                _activeCommandCts = null;
            }
        }
    }

    private void CancelActiveCommand()
    {
        lock (_activeOperationGate)
        {
            try
            {
                _activeCommandCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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
