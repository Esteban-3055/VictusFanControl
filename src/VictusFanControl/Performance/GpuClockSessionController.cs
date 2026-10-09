namespace VictusFanControl.Performance;

internal enum GpuClockSessionState
{
    Unsupported,
    Disabled,
    Applying,
    ActiveUnverified,
    SwitchingPreset,
    Releasing,
    RecoveryRequired,
    Failed
}

/// <summary>
/// Session controller for GPU locked graphics clocks.
///
/// NVML does not expose the exact installed min/max locked range on this
/// target. Therefore a successful Set enters ActiveUnverified rather than an
/// "Owned" state. This controller never polls/re-enforces, never performs the
/// CPU-style 5x/30s reacquire policy and never issues an automatic recovery
/// reset after process restart.
///
/// Normal in-process release is allowed under an explicit exclusive-controller
/// contract: while a VFC GPU clock session is active, other locked-clock tools
/// must not be used concurrently.
/// </summary>
internal sealed class GpuClockSessionController :
    IDisposable
{
    private readonly IGpuClockLimitBackend _backend;
    private readonly IGpuClockSessionJournal _journal;

    private Guid _sessionId;
    private long _generation;
    private DateTimeOffset _createdAtUtc;
    private GpuClockLimitRequest? _committedRequest;
    private GpuClockLimitRequest? _pendingRequest;
    private bool _disposed;

    internal GpuClockSessionController(
        IGpuClockLimitBackend backend,
        IGpuClockSessionJournal journal)
    {
        _backend =
            backend ??
            throw new ArgumentNullException(
                nameof(backend));

        _journal =
            journal ??
            throw new ArgumentNullException(
                nameof(journal));

        var existing =
            _journal.Load();

        if (existing is not null)
        {
            RestoreEvidence(
                existing);

            State =
                GpuClockSessionState.RecoveryRequired;

            LastStatus =
                "GPU_CLOCK_STALE_JOURNAL__RECOVERY_REQUIRED_NO_AUTOMATIC_RESET";
        }
        else if (!_backend.Capabilities
                     .HasCompleteCommandSurface)
        {
            State =
                GpuClockSessionState.Unsupported;

            LastStatus =
                "GPU_CLOCK_COMMAND_SURFACE_UNSUPPORTED";
        }
        else
        {
            State =
                GpuClockSessionState.Disabled;
        }
    }

    internal GpuClockSessionState State { get; private set; }

    internal string? LastStatus { get; private set; }

    internal GpuClockLimitRequest? CurrentRequest =>
        _committedRequest;

    internal GpuClockLimitRequest? PendingRequest =>
        _pendingRequest;

    internal bool Apply(
        GpuClockLimitRequest request)
    {
        ThrowIfDisposed();

        if (State !=
            GpuClockSessionState.Disabled)
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_APPLY_REQUIRES_DISABLED_STATE");
        }

        if (!ValidRequest(request))
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_APPLY_INVALID_REQUEST");
        }

        if (!CanMutateHardware())
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_APPLY_WRITE_GATE_OR_COMMAND_SURFACE_CLOSED");
        }

        BeginSession();

        _pendingRequest =
            request;

        State =
            GpuClockSessionState.Applying;

        if (!TryStore(
                GpuClockJournalPhase.ApplyWriteArmed,
                committedRequest: null,
                pendingRequest: request,
                recoveryReason: null,
                "GPU_CLOCK_APPLY_WRITE_ARM_JOURNAL_ERROR"))
        {
            AbortUnwrittenSession();
            return false;
        }

        var result =
            _backend.SetLockedGraphicsClocks(
                request);

        if (!result.Succeeded)
        {
            return EnterRecoveryRequired(
                "GPU_CLOCK_APPLY_RESULT_UNCONFIRMED__" +
                result.Status,
                persistNewEvidence: false);
        }

        _committedRequest =
            request;

        _pendingRequest =
            null;

        if (!TryStore(
                GpuClockJournalPhase.ActiveUnverified,
                _committedRequest,
                pendingRequest: null,
                recoveryReason: null,
                "GPU_CLOCK_APPLY_COMMIT_JOURNAL_ERROR"))
        {
            State =
                GpuClockSessionState.RecoveryRequired;

            LastStatus =
                "GPU_CLOCK_APPLY_ACCEPTED_BUT_COMMIT_JOURNAL_FAILED__RECOVERY_REQUIRED";

            return false;
        }

        State =
            GpuClockSessionState.ActiveUnverified;

        LastStatus =
            "GPU_CLOCK_ACTIVE_UNVERIFIED__EXCLUSIVE_CONTROLLER_CONTRACT";

        return true;
    }

    internal bool SwitchPreset(
        GpuClockLimitRequest request)
    {
        ThrowIfDisposed();

        if (State !=
            GpuClockSessionState.ActiveUnverified ||
            !_committedRequest.HasValue)
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_SWITCH_REQUIRES_ACTIVE_UNVERIFIED_STATE");
        }

        if (!ValidRequest(request))
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_SWITCH_INVALID_REQUEST");
        }

        if (_committedRequest.Value ==
            request)
        {
            LastStatus =
                "GPU_CLOCK_SWITCH_NOOP_SAME_REQUEST";

            return true;
        }

        if (!CanMutateHardware())
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_SWITCH_WRITE_GATE_OR_COMMAND_SURFACE_CLOSED");
        }

        var oldRequest =
            _committedRequest.Value;

        _pendingRequest =
            request;

        State =
            GpuClockSessionState.SwitchingPreset;

        if (!TryStore(
                GpuClockJournalPhase.PresetSwitchWriteArmed,
                oldRequest,
                request,
                recoveryReason: null,
                "GPU_CLOCK_SWITCH_WRITE_ARM_JOURNAL_ERROR"))
        {
            _pendingRequest =
                null;

            State =
                GpuClockSessionState.ActiveUnverified;

            return false;
        }

        var result =
            _backend.SetLockedGraphicsClocks(
                request);

        if (!result.Succeeded)
        {
            return EnterRecoveryRequired(
                "GPU_CLOCK_SWITCH_RESULT_UNCONFIRMED__" +
                result.Status,
                persistNewEvidence: false);
        }

        _committedRequest =
            request;

        _pendingRequest =
            null;

        if (!TryStore(
                GpuClockJournalPhase.ActiveUnverified,
                request,
                pendingRequest: null,
                recoveryReason: null,
                "GPU_CLOCK_SWITCH_COMMIT_JOURNAL_ERROR"))
        {
            State =
                GpuClockSessionState.RecoveryRequired;

            LastStatus =
                "GPU_CLOCK_SWITCH_ACCEPTED_BUT_COMMIT_JOURNAL_FAILED__RECOVERY_REQUIRED";

            return false;
        }

        State =
            GpuClockSessionState.ActiveUnverified;

        LastStatus =
            "GPU_CLOCK_PRESET_SWITCHED_ACTIVE_UNVERIFIED";

        return true;
    }

    internal bool Release()
    {
        ThrowIfDisposed();

        if (State !=
            GpuClockSessionState.ActiveUnverified ||
            !_committedRequest.HasValue)
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_RELEASE_REQUIRES_ACTIVE_UNVERIFIED_STATE");
        }

        if (!CanMutateHardware())
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_RELEASE_WRITE_GATE_OR_COMMAND_SURFACE_CLOSED");
        }

        var request =
            _committedRequest.Value;

        State =
            GpuClockSessionState.Releasing;

        if (!TryStore(
                GpuClockJournalPhase.ReleaseWriteArmed,
                request,
                pendingRequest: null,
                recoveryReason: null,
                "GPU_CLOCK_RELEASE_WRITE_ARM_JOURNAL_ERROR"))
        {
            State =
                GpuClockSessionState.ActiveUnverified;

            return false;
        }

        var result =
            _backend.ResetLockedGraphicsClocks();

        if (!result.Succeeded)
        {
            return EnterRecoveryRequired(
                "GPU_CLOCK_RELEASE_RESULT_UNCONFIRMED__" +
                result.Status,
                persistNewEvidence: false);
        }

        try
        {
            _journal.Delete();
        }
        catch (Exception ex)
        {
            State =
                GpuClockSessionState.RecoveryRequired;

            LastStatus =
                "GPU_CLOCK_RELEASE_ACCEPTED_BUT_JOURNAL_DELETE_FAILED__RECOVERY_REQUIRED: " +
                ex.Message;

            return false;
        }

        ClearSession();

        State =
            GpuClockSessionState.Disabled;

        LastStatus =
            "GPU_CLOCK_RELEASED_TO_NVIDIA_DEFAULT";

        return true;
    }

    /// <summary>
    /// Used by the future guardian for suspend/resume, driver-reset or device
    /// loss. It invalidates authority without issuing Set or Reset.
    /// </summary>
    internal bool MarkAuthorityUnknown(
        string reason)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(
                reason))
        {
            throw new ArgumentException(
                "GPU clock authority invalidation requires a reason.",
                nameof(reason));
        }

        if (State !=
                GpuClockSessionState.ActiveUnverified &&
            State !=
                GpuClockSessionState.SwitchingPreset &&
            State !=
                GpuClockSessionState.Applying &&
            State !=
                GpuClockSessionState.Releasing)
        {
            return FailWithoutStateChange(
                "GPU_CLOCK_AUTHORITY_INVALIDATION_REQUIRES_ACTIVE_SESSION");
        }

        return EnterRecoveryRequired(
            reason,
            persistNewEvidence: true);
    }

    private bool EnterRecoveryRequired(
        string reason,
        bool persistNewEvidence)
    {
        State =
            GpuClockSessionState.RecoveryRequired;

        if (persistNewEvidence)
        {
            var committed =
                _committedRequest;

            var pending =
                _pendingRequest;

            if (!TryStore(
                    GpuClockJournalPhase.RecoveryRequired,
                    committed,
                    pending,
                    reason,
                    "GPU_CLOCK_RECOVERY_REQUIRED_JOURNAL_ERROR"))
            {
                LastStatus =
                    reason +
                    " | GPU_CLOCK_RECOVERY_REQUIRED_JOURNAL_NOT_UPDATED";

                return false;
            }
        }

        LastStatus =
            reason;

        return false;
    }

    private void BeginSession()
    {
        _sessionId =
            Guid.NewGuid();

        _generation = 0;

        _createdAtUtc =
            DateTimeOffset.UtcNow;

        _committedRequest =
            null;

        _pendingRequest =
            null;
    }

    private void RestoreEvidence(
        GpuClockSessionJournalRecord record)
    {
        _sessionId =
            record.SessionId;

        _generation =
            record.Generation;

        _createdAtUtc =
            record.CreatedAtUtc;

        _committedRequest =
            record.CommittedRequest;

        _pendingRequest =
            record.PendingRequest;
    }

    private bool TryStore(
        GpuClockJournalPhase phase,
        GpuClockLimitRequest? committedRequest,
        GpuClockLimitRequest? pendingRequest,
        string? recoveryReason,
        string errorCode)
    {
        try
        {
            if (_sessionId ==
                Guid.Empty)
            {
                throw new InvalidOperationException(
                    "GPU clock session is incomplete.");
            }

            var now =
                DateTimeOffset.UtcNow;

            var record =
                new GpuClockSessionJournalRecord(
                    GpuClockSessionJournalRecord.CurrentSchemaVersion,
                    _journal.TargetProfileId,
                    _sessionId,
                    checked(_generation + 1),
                    phase,
                    committedRequest,
                    pendingRequest,
                    recoveryReason,
                    _createdAtUtc,
                    now);

            _journal.Store(record);

            _generation =
                record.Generation;

            return true;
        }
        catch (Exception ex)
        {
            LastStatus =
                errorCode +
                ": " +
                ex.Message;

            return false;
        }
    }

    private bool CanMutateHardware() =>
        _backend.Capabilities
            .HasCompleteCommandSurface &&
        _backend.Capabilities
            .HardwareWritesAuthorized;

    private static bool ValidRequest(
        GpuClockLimitRequest request) =>
        request.MinGraphicsClockMHz > 0 &&
        request.MaxGraphicsClockMHz >=
            request.MinGraphicsClockMHz;

    private void AbortUnwrittenSession()
    {
        ClearSession();

        State =
            GpuClockSessionState.Failed;
    }

    private void ClearSession()
    {
        _sessionId =
            Guid.Empty;

        _generation =
            0;

        _createdAtUtc =
            default;

        _committedRequest =
            null;

        _pendingRequest =
            null;
    }

    private bool FailWithoutStateChange(
        string status)
    {
        LastStatus =
            status;

        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Deliberately no implicit Reset here. Unexpected teardown must never
        // convert Dispose into a blind recovery write.
        _disposed = true;
    }
}
