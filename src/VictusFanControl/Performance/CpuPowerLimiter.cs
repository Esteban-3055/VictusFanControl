using VictusFanControl.Runtime;

namespace VictusFanControl.Performance;

internal enum CpuPowerLimiterState
{
    Unsupported,
    Disabled,
    Applying,
    Active,
    Contested,
    ReacquiredPendingStability,
    Yielded,
    Recovering,
    Failed
}

internal readonly record struct CpuPowerLimitRequest(
    double Pl1Watts,
    double Pl2Watts);

internal readonly record struct CpuPowerLimitSnapshot(
    ulong Raw,
    double Pl1Watts,
    double Pl2Watts,
    bool Locked);

internal readonly record struct CpuPowerLimitApplyPlan(
    ulong RequestedRaw,
    double AppliedPl1Watts,
    double AppliedPl2Watts);

internal readonly record struct CpuPowerLimitRestorePlan(
    ulong Value,
    string Status);

internal interface ICpuPowerLimitBackend
{
    bool IsSupported { get; }

    CpuPowerLimitSnapshot Read();

    CpuPowerLimitApplyPlan BuildApplyPlan(
        CpuPowerLimitSnapshot baseline,
        CpuPowerLimitRequest request);

    bool OwnedFieldsMatch(
        ulong expectedRaw,
        CpuPowerLimitSnapshot current);

    CpuPowerLimitApplyPlan BuildReacquirePlan(
        CpuPowerLimitSnapshot originalBaseline,
        CpuPowerLimitRequest request,
        CpuPowerLimitSnapshot current);

    CpuPowerLimitRestorePlan PlanRestore(
        CpuPowerLimitSnapshot restoreTarget,
        ulong appliedRaw,
        CpuPowerLimitSnapshot current);

    void Write(ulong raw);
}

internal sealed class CpuPowerLimiter : IDisposable
{
    private readonly ICpuPowerLimitBackend _backend;
    private readonly ICpuPowerSessionJournal _journal;
    private readonly CpuPowerConflictPolicy _conflictPolicy;

    private CpuPowerLimitSnapshot? _baseline;
    private CpuPowerLimitSnapshot? _externalHandoff;
    private CpuPowerLimitRequest? _request;
    private ulong? _appliedRaw;

    private Guid _sessionId;
    private long _journalGeneration;
    private DateTimeOffset _sessionCreatedAtUtc;
    private bool _disposed;

    internal CpuPowerLimiter(
        ICpuPowerLimitBackend backend,
        ICpuPowerSessionJournal journal,
        IActiveTimeClock? conflictClock = null)
    {
        _backend =
            backend ??
            throw new ArgumentNullException(nameof(backend));

        _journal =
            journal ??
            throw new ArgumentNullException(nameof(journal));

        if (string.IsNullOrWhiteSpace(_journal.TargetProfileId))
        {
            throw new ArgumentException(
                "CPU power journal must expose an exact target profile id.",
                nameof(journal));
        }

        _conflictPolicy =
            new CpuPowerConflictPolicy(
                conflictClock ??
                new WindowsActiveTimeClock());

        State =
            backend.IsSupported
                ? CpuPowerLimiterState.Disabled
                : CpuPowerLimiterState.Unsupported;
    }

    internal CpuPowerLimiterState State { get; private set; }

    internal string? LastError { get; private set; }

    internal CpuPowerLimitSnapshot? Baseline => _baseline;

    internal CpuPowerLimitSnapshot? ExternalHandoff =>
        _externalHandoff;

    internal ulong? AppliedRaw => _appliedRaw;

    internal int ReacquireAttemptsUsed =>
        _conflictPolicy.AttemptsUsed;

    internal bool Apply(
        CpuPowerLimitRequest request)
    {
        ThrowIfDisposed();
        LastError = null;

        if (State == CpuPowerLimiterState.Unsupported)
            return Fail("CPU_POWER_LIMIT_UNSUPPORTED");

        if (State != CpuPowerLimiterState.Disabled)
            return Fail(
                "CPU_POWER_LIMIT_APPLY_REQUIRES_DISABLED_STATE");

        if (!ValidRequest(request))
            return Fail(
                "CPU_POWER_LIMIT_INVALID_REQUEST");

        try
        {
            if (_journal.Load() is not null)
            {
                return Fail(
                    "CPU_POWER_LIMIT_UNRESOLVED_JOURNAL__RECOVERY_REQUIRED");
            }
        }
        catch (Exception ex)
        {
            return Fail(
                "CPU_POWER_LIMIT_JOURNAL_PREFLIGHT_ERROR: " +
                ex.Message);
        }

        var baseline = _backend.Read();

        if (baseline.Locked)
            return Fail("CPU_POWER_LIMIT_LOCKED");

        var plan =
            _backend.BuildApplyPlan(
                baseline,
                request);

        if (!ValidPlanAgainstOriginalBaseline(
                plan,
                baseline))
        {
            return Fail(
                "CPU_POWER_LIMIT_BACKEND_PLAN_REJECTED");
        }

        BeginSession(
            baseline,
            request,
            plan.RequestedRaw);

        State = CpuPowerLimiterState.Applying;

        if (!TryStoreJournal(
                CpuPowerJournalPhase.WriteArmed,
                plan.RequestedRaw,
                "CPU_POWER_LIMIT_WRITE_ARM_JOURNAL_ERROR"))
        {
            AbortUnwrittenSession();
            return false;
        }

        try
        {
            _backend.Write(plan.RequestedRaw);

            var readback = _backend.Read();

            if (readback.Raw !=
                plan.RequestedRaw)
            {
                LastError =
                    "CPU_POWER_LIMIT_READBACK_MISMATCH";

                return RecoverAfterFailedApply();
            }

            _appliedRaw = readback.Raw;

            if (!TryStoreJournal(
                    CpuPowerJournalPhase.Owned,
                    pendingRaw: null,
                    "CPU_POWER_LIMIT_OWNED_JOURNAL_ERROR"))
            {
                State = CpuPowerLimiterState.Failed;
                return false;
            }

            State = CpuPowerLimiterState.Active;
            return true;
        }
        catch (Exception ex)
        {
            LastError =
                "CPU_POWER_LIMIT_APPLY_ERROR: " +
                ex.Message;

            return RecoverAfterFailedApply();
        }
    }

    /// <summary>
    /// Verifies requested PL1/PL2 ownership and advances the bounded
    /// external-writer policy. This method performs at most one reacquisition
    /// write per call, and every write must be durably armed first.
    /// </summary>
    internal bool VerifyActive()
    {
        ThrowIfDisposed();

        if (!_baseline.HasValue ||
            !_request.HasValue ||
            !_appliedRaw.HasValue)
        {
            return false;
        }

        return State switch
        {
            CpuPowerLimiterState.Active =>
                VerifyOwnedActive(),

            CpuPowerLimiterState.Contested =>
                VerifyContested(),

            CpuPowerLimiterState.ReacquiredPendingStability =>
                VerifyPendingStability(),

            CpuPowerLimiterState.Yielded =>
                false,

            _ =>
                false
        };
    }

    internal bool Release()
    {
        ThrowIfDisposed();

        if (!_baseline.HasValue ||
            !_appliedRaw.HasValue)
        {
            if (State !=
                CpuPowerLimiterState.Unsupported)
            {
                State =
                    CpuPowerLimiterState.Disabled;
            }

            LastError = null;
            return true;
        }

        return RecoverOwnedState();
    }

    private bool VerifyOwnedActive()
    {
        try
        {
            var current =
                _backend.Read();

            if (current.Raw ==
                _appliedRaw!.Value)
            {
                return true;
            }

            if (current.Locked)
            {
                return HandleLockWhileSessionActive(
                    current);
            }

            if (_backend.OwnedFieldsMatch(
                    _appliedRaw.Value,
                    current))
            {
                // Preserve an external change to non-owned fields without
                // falsely starting a PL1/PL2 conflict.
                _appliedRaw = current.Raw;

                if (!TryStoreJournal(
                        CpuPowerJournalPhase.Owned,
                        pendingRaw: null,
                        "CPU_POWER_LIMIT_OWNED_METADATA_JOURNAL_ERROR"))
                {
                    State = CpuPowerLimiterState.Failed;
                    return false;
                }

                LastError = null;
                return true;
            }

            RecordExternalHandoff(current);
            _conflictPolicy.ObserveExternalChange();
            State = CpuPowerLimiterState.Contested;

            if (!TryStoreJournal(
                    CpuPowerJournalPhase.Contested,
                    pendingRaw: null,
                    "CPU_POWER_LIMIT_CONTESTED_JOURNAL_ERROR"))
            {
                State = CpuPowerLimiterState.Failed;
                return false;
            }

            LastError =
                "CPU_POWER_LIMIT_CHANGED_EXTERNALLY__CONTESTED";

            return false;
        }
        catch (Exception ex)
        {
            State = CpuPowerLimiterState.Failed;
            LastError =
                "CPU_POWER_LIMIT_VERIFY_ERROR: " +
                ex.Message;
            return false;
        }
    }

    private bool VerifyContested()
    {
        CpuPowerLimitSnapshot current;

        try
        {
            current = _backend.Read();
        }
        catch (Exception ex)
        {
            State = CpuPowerLimiterState.Failed;
            LastError =
                "CPU_POWER_LIMIT_CONTESTED_READ_ERROR: " +
                ex.Message;
            return false;
        }

        if (current.Locked)
        {
            return HandleLockWhileSessionActive(
                current);
        }

        if (_backend.OwnedFieldsMatch(
                _appliedRaw!.Value,
                current))
        {
            _appliedRaw = current.Raw;
            _conflictPolicy.ObserveRequestedValuePresent();
            State =
                CpuPowerLimiterState.ReacquiredPendingStability;

            if (!TryStoreJournal(
                    CpuPowerJournalPhase.Stability,
                    pendingRaw: null,
                    "CPU_POWER_LIMIT_STABILITY_JOURNAL_ERROR"))
            {
                State = CpuPowerLimiterState.Failed;
                return false;
            }

            LastError =
                "CPU_POWER_LIMIT_REQUESTED_VALUE_RETURNED__STABILITY_PENDING";

            return true;
        }

        var handoffChanged =
            !_externalHandoff.HasValue ||
            _externalHandoff.Value.Raw != current.Raw;

        RecordExternalHandoff(current);
        _conflictPolicy.ObserveExternalChange();

        if (_conflictPolicy.State ==
            CpuPowerConflictState.Yielded)
        {
            State = CpuPowerLimiterState.Yielded;

            if (!TryStoreJournal(
                    CpuPowerJournalPhase.Yielded,
                    pendingRaw: null,
                    "CPU_POWER_LIMIT_YIELDED_JOURNAL_ERROR"))
            {
                State = CpuPowerLimiterState.Failed;
                return false;
            }

            LastError =
                "CPU_POWER_LIMIT_EXTERNAL_CONTROL_YIELDED";

            return false;
        }

        if (!_conflictPolicy.TryBeginReacquire())
        {
            if (handoffChanged &&
                !TryStoreJournal(
                    CpuPowerJournalPhase.Contested,
                    pendingRaw: null,
                    "CPU_POWER_LIMIT_CONTESTED_HANDOFF_JOURNAL_ERROR"))
            {
                State = CpuPowerLimiterState.Failed;
                return false;
            }

            State = CpuPowerLimiterState.Contested;

            LastError =
                $"CPU_POWER_LIMIT_CHANGED_EXTERNALLY__CONTESTED_ATTEMPTS_{_conflictPolicy.AttemptsUsed}_OF_{_conflictPolicy.MaxReacquireAttempts}";

            return false;
        }

        return AttemptBoundedReacquire(
            current);
    }

    private bool AttemptBoundedReacquire(
        CpuPowerLimitSnapshot observedExternal)
    {
        var attemptNumber =
            _conflictPolicy.AttemptsUsed;

        CpuPowerLimitSnapshot preWrite;

        try
        {
            preWrite =
                _backend.Read();
        }
        catch (Exception ex)
        {
            _conflictPolicy.CompleteReacquire(
                exactReadback: false);

            State = CpuPowerLimiterState.Failed;
            LastError =
                "CPU_POWER_LIMIT_REACQUIRE_PREREAD_ERROR__WRITE_NOT_ATTEMPTED: " +
                ex.Message;

            return false;
        }

        if (preWrite.Locked)
        {
            _conflictPolicy.CompleteReacquire(
                exactReadback: false);

            return HandleLockWhileSessionActive(
                preWrite);
        }

        if (_backend.OwnedFieldsMatch(
                _appliedRaw!.Value,
                preWrite))
        {
            _appliedRaw = preWrite.Raw;

            _conflictPolicy.CompleteReacquire(
                exactReadback: true);

            State =
                CpuPowerLimiterState.ReacquiredPendingStability;

            if (!TryStoreJournal(
                    CpuPowerJournalPhase.Stability,
                    pendingRaw: null,
                    "CPU_POWER_LIMIT_REACQUIRE_NO_WRITE_STABILITY_JOURNAL_ERROR"))
            {
                State = CpuPowerLimiterState.Failed;
                return false;
            }

            LastError =
                $"CPU_POWER_LIMIT_REACQUIRED_WITHOUT_WRITE_ATTEMPT_{attemptNumber}_OF_{_conflictPolicy.MaxReacquireAttempts}";

            return true;
        }

        if (preWrite.Raw !=
            observedExternal.Raw)
        {
            RecordExternalHandoff(preWrite);
        }

        CpuPowerLimitApplyPlan plan;

        try
        {
            plan =
                _backend.BuildReacquirePlan(
                    _baseline!.Value,
                    _request!.Value,
                    preWrite);

            if (!ValidPlanAgainstOriginalBaseline(
                    plan,
                    _baseline.Value) ||
                plan.RequestedRaw ==
                    preWrite.Raw)
            {
                throw new InvalidOperationException(
                    "Backend returned an invalid reacquisition plan.");
            }
        }
        catch (Exception ex)
        {
            _conflictPolicy.CompleteReacquire(
                exactReadback: false);

            _conflictPolicy.ForceYield();
            State = CpuPowerLimiterState.Yielded;

            if (!TryStoreJournal(
                    CpuPowerJournalPhase.Yielded,
                    pendingRaw: null,
                    "CPU_POWER_LIMIT_REACQUIRE_PLAN_YIELD_JOURNAL_ERROR"))
            {
                State = CpuPowerLimiterState.Failed;
            }

            LastError =
                "CPU_POWER_LIMIT_REACQUIRE_PLAN_REJECTED__YIELDED: " +
                ex.Message;

            return false;
        }

        if (!TryStoreJournal(
                CpuPowerJournalPhase.ReacquireWriteArmed,
                plan.RequestedRaw,
                "CPU_POWER_LIMIT_REACQUIRE_WRITE_ARM_JOURNAL_ERROR"))
        {
            // No hardware write occurred. Do not continue an in-flight attempt
            // after losing durability.
            _conflictPolicy.ForceYield();
            State = CpuPowerLimiterState.Failed;
            return false;
        }

        try
        {
            _backend.Write(
                plan.RequestedRaw);

            var readback =
                _backend.Read();

            if (readback.Raw ==
                plan.RequestedRaw)
            {
                _appliedRaw =
                    plan.RequestedRaw;

                _conflictPolicy.CompleteReacquire(
                    exactReadback: true);

                State =
                    CpuPowerLimiterState.ReacquiredPendingStability;

                if (!TryStoreJournal(
                        CpuPowerJournalPhase.Stability,
                        pendingRaw: null,
                        "CPU_POWER_LIMIT_REACQUIRE_STABILITY_JOURNAL_ERROR"))
                {
                    State = CpuPowerLimiterState.Failed;
                    return false;
                }

                LastError =
                    $"CPU_POWER_LIMIT_REACQUIRED_ATTEMPT_{attemptNumber}_OF_{_conflictPolicy.MaxReacquireAttempts}__STABILITY_PENDING";

                return true;
            }

            if (readback.Locked)
            {
                _conflictPolicy.CompleteReacquire(
                    exactReadback: false);

                return HandleLockWhileSessionActive(
                    readback);
            }

            if (!_backend.OwnedFieldsMatch(
                    plan.RequestedRaw,
                    readback))
            {
                RecordExternalHandoff(
                    readback);
            }

            return CompleteFailedReacquire(
                $"CPU_POWER_LIMIT_REACQUIRE_READBACK_MISMATCH_ATTEMPT_{attemptNumber}_OF_{_conflictPolicy.MaxReacquireAttempts}");
        }
        catch (Exception ex)
        {
            try
            {
                var afterError =
                    _backend.Read();

                if (afterError.Raw ==
                    plan.RequestedRaw)
                {
                    _appliedRaw =
                        plan.RequestedRaw;

                    _conflictPolicy.CompleteReacquire(
                        exactReadback: true);

                    State =
                        CpuPowerLimiterState.ReacquiredPendingStability;

                    if (!TryStoreJournal(
                            CpuPowerJournalPhase.Stability,
                            pendingRaw: null,
                            "CPU_POWER_LIMIT_REACQUIRE_POSTERROR_STABILITY_JOURNAL_ERROR"))
                    {
                        State = CpuPowerLimiterState.Failed;
                        return false;
                    }

                    LastError =
                        $"CPU_POWER_LIMIT_REACQUIRE_WRITE_ERROR_BUT_EXACT_READBACK_ATTEMPT_{attemptNumber}_OF_{_conflictPolicy.MaxReacquireAttempts}: {ex.Message}";

                    return true;
                }

                if (afterError.Locked)
                {
                    _conflictPolicy.CompleteReacquire(
                        exactReadback: false);

                    return HandleLockWhileSessionActive(
                        afterError);
                }

                if (!_backend.OwnedFieldsMatch(
                        plan.RequestedRaw,
                        afterError))
                {
                    RecordExternalHandoff(
                        afterError);
                }

                return CompleteFailedReacquire(
                    $"CPU_POWER_LIMIT_REACQUIRE_WRITE_ERROR_ATTEMPT_{attemptNumber}_OF_{_conflictPolicy.MaxReacquireAttempts}: {ex.Message}");
            }
            catch
            {
                // ReacquireWriteArmed remains durable. The write may have
                // happened; do not issue a second write from an unknown state.
                _conflictPolicy.CompleteReacquire(
                    exactReadback: false);

                State = CpuPowerLimiterState.Failed;
                LastError =
                    $"CPU_POWER_LIMIT_REACQUIRE_WRITE_RESULT_UNCONFIRMED_ATTEMPT_{attemptNumber}_OF_{_conflictPolicy.MaxReacquireAttempts}: {ex.Message}";

                return false;
            }
        }
    }

    private bool VerifyPendingStability()
    {
        try
        {
            var current =
                _backend.Read();

            if (current.Locked)
            {
                return HandleLockWhileSessionActive(
                    current);
            }

            if (_backend.OwnedFieldsMatch(
                    _appliedRaw!.Value,
                    current))
            {
                var metadataChanged =
                    current.Raw !=
                    _appliedRaw.Value;

                if (metadataChanged)
                {
                    _appliedRaw =
                        current.Raw;

                    if (!TryStoreJournal(
                            CpuPowerJournalPhase.Stability,
                            pendingRaw: null,
                            "CPU_POWER_LIMIT_STABILITY_METADATA_JOURNAL_ERROR"))
                    {
                        State = CpuPowerLimiterState.Failed;
                        return false;
                    }
                }

                if (_conflictPolicy.TryCompleteStableWindow())
                {
                    State =
                        CpuPowerLimiterState.Active;

                    if (!TryStoreJournal(
                            CpuPowerJournalPhase.Owned,
                            pendingRaw: null,
                            "CPU_POWER_LIMIT_STABLE_OWNED_JOURNAL_ERROR"))
                    {
                        State = CpuPowerLimiterState.Failed;
                        return false;
                    }

                    LastError = null;
                }
                else
                {
                    State =
                        CpuPowerLimiterState.ReacquiredPendingStability;
                }

                return true;
            }

            RecordExternalHandoff(current);
            _conflictPolicy.ObserveExternalChange();

            if (_conflictPolicy.State ==
                CpuPowerConflictState.Yielded)
            {
                State =
                    CpuPowerLimiterState.Yielded;

                if (!TryStoreJournal(
                        CpuPowerJournalPhase.Yielded,
                        pendingRaw: null,
                        "CPU_POWER_LIMIT_RECONTESTED_YIELD_JOURNAL_ERROR"))
                {
                    State = CpuPowerLimiterState.Failed;
                    return false;
                }

                LastError =
                    "CPU_POWER_LIMIT_EXTERNAL_CONTROL_YIELDED_AFTER_REACQUIRE";
            }
            else
            {
                State =
                    CpuPowerLimiterState.Contested;

                if (!TryStoreJournal(
                        CpuPowerJournalPhase.Contested,
                        pendingRaw: null,
                        "CPU_POWER_LIMIT_RECONTESTED_JOURNAL_ERROR"))
                {
                    State = CpuPowerLimiterState.Failed;
                    return false;
                }

                LastError =
                    $"CPU_POWER_LIMIT_RECONTESTED_ATTEMPTS_{_conflictPolicy.AttemptsUsed}_OF_{_conflictPolicy.MaxReacquireAttempts}";
            }

            return false;
        }
        catch (Exception ex)
        {
            State = CpuPowerLimiterState.Failed;
            LastError =
                "CPU_POWER_LIMIT_STABILITY_VERIFY_ERROR: " +
                ex.Message;

            return false;
        }
    }

    private bool CompleteFailedReacquire(
        string error)
    {
        _conflictPolicy.CompleteReacquire(
            exactReadback: false);

        var yielded =
            _conflictPolicy.State ==
            CpuPowerConflictState.Yielded;

        State =
            yielded
                ? CpuPowerLimiterState.Yielded
                : CpuPowerLimiterState.Contested;

        var phase =
            yielded
                ? CpuPowerJournalPhase.Yielded
                : CpuPowerJournalPhase.Contested;

        if (!TryStoreJournal(
                phase,
                pendingRaw: null,
                "CPU_POWER_LIMIT_REACQUIRE_RESULT_JOURNAL_ERROR"))
        {
            State = CpuPowerLimiterState.Failed;
            return false;
        }

        LastError =
            yielded
                ? error +
                  " | CPU_POWER_LIMIT_EXTERNAL_CONTROL_YIELDED"
                : error;

        return false;
    }

    private bool HandleLockWhileSessionActive(
        CpuPowerLimitSnapshot current)
    {
        var stillOwnsRequestedPower =
            _appliedRaw.HasValue &&
            _backend.OwnedFieldsMatch(
                _appliedRaw.Value,
                current);

        _conflictPolicy.ForceYield();

        if (stillOwnsRequestedPower)
        {
            State = CpuPowerLimiterState.Failed;
            LastError =
                "CPU_POWER_LIMIT_LOCK_APPEARED_WHILE_REQUESTED_POWER_STILL_PRESENT__NO_WRITE";

            TryStoreUnresolvedBestEffort(
                "LOCK_WHILE_OWNED");

            return false;
        }

        RecordExternalHandoff(current);
        State = CpuPowerLimiterState.Yielded;

        if (!TryStoreJournal(
                CpuPowerJournalPhase.Yielded,
                pendingRaw: null,
                "CPU_POWER_LIMIT_EXTERNAL_LOCK_YIELD_JOURNAL_ERROR"))
        {
            State = CpuPowerLimiterState.Failed;
            return false;
        }

        LastError =
            "CPU_POWER_LIMIT_EXTERNAL_LOCKED_VALUE__YIELDED_NO_WRITE";

        return false;
    }

    private void RecordExternalHandoff(
        CpuPowerLimitSnapshot snapshot)
    {
        _externalHandoff = snapshot;
    }

    private bool RecoverAfterFailedApply()
    {
        var previousError = LastError;
        var restored =
            RecoverOwnedState();

        if (!restored &&
            previousError is not null)
        {
            LastError =
                previousError +
                " | " +
                LastError;
        }
        else if (restored)
        {
            LastError = previousError;
        }

        return false;
    }

    private bool RecoverOwnedState()
    {
        if (!_baseline.HasValue ||
            !_appliedRaw.HasValue)
        {
            return false;
        }

        State = CpuPowerLimiterState.Recovering;

        try
        {
            var restoreTarget =
                _externalHandoff ??
                _baseline.Value;

            var current =
                _backend.Read();

            if (current.Raw ==
                restoreTarget.Raw)
            {
                return CompleteResolvedSession(
                    "CPU_POWER_LIMIT_RESOLVED_JOURNAL_DELETE_ERROR");
            }

            var stillOwnsRequestedPower =
                _backend.OwnedFieldsMatch(
                    _appliedRaw.Value,
                    current);

            var plan =
                _backend.PlanRestore(
                    restoreTarget,
                    _appliedRaw.Value,
                    current);

            if (plan.Value ==
                current.Raw)
            {
                if (!stillOwnsRequestedPower)
                {
                    // Another writer already owns PL1/PL2. Preserve it and
                    // close our session without a restore write.
                    return CompleteResolvedSession(
                        "CPU_POWER_LIMIT_EXTERNAL_HANDOFF_JOURNAL_DELETE_ERROR");
                }

                TryStoreUnresolvedBestEffort(
                    "RESTORE_BLOCKED_WHILE_OWNED");

                return FailRecovery(
                    "CPU_POWER_LIMIT_RESTORE_BLOCKED_WHILE_OWNED: " +
                    plan.Status);
            }

            if (!TryStoreJournal(
                    CpuPowerJournalPhase.Restoring,
                    plan.Value,
                    "CPU_POWER_LIMIT_RESTORE_WRITE_ARM_JOURNAL_ERROR"))
            {
                return FailRecovery(
                    LastError ??
                    "CPU_POWER_LIMIT_RESTORE_WRITE_ARM_JOURNAL_ERROR");
            }

            var secondRead =
                _backend.Read();

            if (secondRead.Raw !=
                current.Raw)
            {
                // Restoring remains durable. No write follows a changed
                // compare-read; a recovery owner must inspect the new state.
                return FailRecovery(
                    "CPU_POWER_LIMIT_RESTORE_CONCURRENT_CHANGE");
            }

            _backend.Write(
                plan.Value);

            var final =
                _backend.Read();

            if (final.Raw !=
                restoreTarget.Raw)
            {
                TryStoreUnresolvedBestEffort(
                    "RESTORE_NOT_TARGET");

                return FailRecovery(
                    "CPU_POWER_LIMIT_RESTORE_NOT_TARGET: " +
                    plan.Status);
            }

            return CompleteResolvedSession(
                "CPU_POWER_LIMIT_RESTORED_JOURNAL_DELETE_ERROR");
        }
        catch (Exception ex)
        {
            // If Restoring or WriteArmed was already persisted, leave it in
            // place. The physical result may be unknown and no second write is
            // safe from this catch path.
            return FailRecovery(
                "CPU_POWER_LIMIT_RESTORE_ERROR: " +
                ex.Message);
        }
    }

    private void BeginSession(
        CpuPowerLimitSnapshot baseline,
        CpuPowerLimitRequest request,
        ulong appliedRaw)
    {
        _baseline = baseline;
        _externalHandoff = null;
        _request = request;
        _appliedRaw = appliedRaw;
        _sessionId = Guid.NewGuid();
        _journalGeneration = 0;
        _sessionCreatedAtUtc =
            DateTimeOffset.UtcNow;

        _conflictPolicy.ResetByUser();
    }

    private bool TryStoreJournal(
        CpuPowerJournalPhase phase,
        ulong? pendingRaw,
        string errorCode)
    {
        try
        {
            if (!_baseline.HasValue ||
                !_request.HasValue ||
                !_appliedRaw.HasValue ||
                _sessionId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "CPU power session is incomplete.");
            }

            var now =
                DateTimeOffset.UtcNow;

            var record =
                new CpuPowerSessionJournalRecord(
                    CpuPowerSessionJournalRecord.CurrentSchemaVersion,
                    _journal.TargetProfileId,
                    _sessionId,
                    checked(_journalGeneration + 1),
                    phase,
                    _baseline.Value,
                    _request.Value,
                    _appliedRaw.Value,
                    _externalHandoff,
                    _conflictPolicy.CaptureSnapshot(),
                    pendingRaw,
                    _sessionCreatedAtUtc,
                    now);

            _journal.Store(record);
            _journalGeneration =
                record.Generation;

            return true;
        }
        catch (Exception ex)
        {
            LastError =
                errorCode +
                ": " +
                ex.Message;

            return false;
        }
    }

    private void TryStoreUnresolvedBestEffort(
        string detail)
    {
        _conflictPolicy.ForceYield();

        if (!TryStoreJournal(
                CpuPowerJournalPhase.Unresolved,
                pendingRaw: null,
                "CPU_POWER_LIMIT_UNRESOLVED_JOURNAL_ERROR"))
        {
            LastError =
                (LastError ?? detail) +
                " | CPU_POWER_LIMIT_UNRESOLVED_JOURNAL_NOT_UPDATED";
        }
    }

    private bool CompleteResolvedSession(
        string deleteErrorCode)
    {
        try
        {
            _journal.Delete();
        }
        catch (Exception ex)
        {
            State = CpuPowerLimiterState.Failed;
            LastError =
                deleteErrorCode +
                ": " +
                ex.Message;
            return false;
        }

        ClearSession();
        return true;
    }

    private void AbortUnwrittenSession()
    {
        _baseline = null;
        _externalHandoff = null;
        _request = null;
        _appliedRaw = null;
        _sessionId = Guid.Empty;
        _journalGeneration = 0;
        _sessionCreatedAtUtc = default;
        _conflictPolicy.ResetByUser();

        State = CpuPowerLimiterState.Failed;
    }

    private void ClearSession()
    {
        _baseline = null;
        _externalHandoff = null;
        _request = null;
        _appliedRaw = null;
        _sessionId = Guid.Empty;
        _journalGeneration = 0;
        _sessionCreatedAtUtc = default;

        _conflictPolicy.ResetByUser();

        LastError = null;
        State = CpuPowerLimiterState.Disabled;
    }

    private bool Fail(
        string error)
    {
        LastError = error;

        if (State !=
            CpuPowerLimiterState.Unsupported)
        {
            State =
                CpuPowerLimiterState.Failed;
        }

        return false;
    }

    private bool FailRecovery(
        string error)
    {
        LastError = error;
        State = CpuPowerLimiterState.Failed;
        return false;
    }

    private static bool ValidRequest(
        CpuPowerLimitRequest request) =>
        double.IsFinite(request.Pl1Watts) &&
        double.IsFinite(request.Pl2Watts) &&
        request.Pl1Watts >= 10 &&
        request.Pl2Watts >=
            request.Pl1Watts;

    private static bool ValidPlanAgainstOriginalBaseline(
        CpuPowerLimitApplyPlan plan,
        CpuPowerLimitSnapshot baseline) =>
        plan.RequestedRaw != baseline.Raw &&
        plan.AppliedPl1Watts >= 10 &&
        plan.AppliedPl2Watts >=
            plan.AppliedPl1Watts &&
        plan.AppliedPl1Watts <
            baseline.Pl1Watts &&
        plan.AppliedPl2Watts <
            baseline.Pl2Watts;

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

        if (_baseline.HasValue &&
            _appliedRaw.HasValue)
        {
            try
            {
                _ =
                    RecoverOwnedState();
            }
            catch
            {
                State =
                    CpuPowerLimiterState.Failed;
            }
        }

        _disposed = true;
    }
}
