namespace VictusFanControl.Watchdog;

internal sealed class WatchdogLeaseManager
{
    internal static readonly TimeSpan OwnedHeartbeatTimeout =
        TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan WriteArmedDeadline =
        TimeSpan.FromSeconds(12);

    internal static readonly TimeSpan RestoringDeadline =
        TimeSpan.FromSeconds(8);

    private readonly ILeaseJournal _journal;
    private readonly ILeaseRecoveryHardware _hardware;
    private readonly IMonotonicClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WatchdogLeaseRecord? _active;
    private ulong _lastHeartbeatMs;
    private ulong _operationStartedMs;

    public WatchdogLeaseManager(
        ILeaseJournal journal,
        ILeaseRecoveryHardware hardware,
        IMonotonicClock clock)
    {
        _journal = journal;
        _hardware = hardware;
        _clock = clock;
    }

    public WatchdogLeaseRecord? Active => _active;

    public async ValueTask<LeaseOperationResult> PrepareAsync(
        ControllerIdentity controller,
        CancellationToken cancellationToken)
    {
        if (controller.ProcessId <= 0 ||
            controller.ProcessStartUtcTicks <= 0)
        {
            throw new LeaseProtocolException(
                "INVALID_CONTROLLER",
                "Controller identity is invalid.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureNoExistingLeaseAsync(cancellationToken)
                .ConfigureAwait(false);

            var observed =
                await _hardware.ReadSetpointAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!observed.IsFirmwareOwned)
            {
                throw new LeaseProtocolException(
                    "EXTERNAL_OVERRIDE",
                    $"Prepare refused because EC is already fixed at {observed}; VFC does not own it.");
            }

            var record = new WatchdogLeaseRecord(
                WatchdogLeaseRecord.CurrentSchemaVersion,
                Guid.NewGuid(),
                controller,
                WatchdogLeasePhase.Prepared,
                Generation: 1,
                PreviousOwned: null,
                Pending: null,
                Owned: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            await _journal.StoreAsync(record, cancellationToken)
                .ConfigureAwait(false);

            _active = record;
            _lastHeartbeatMs = _clock.Milliseconds;
            _operationStartedMs = _clock.Milliseconds;

            return Result(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CancelPreparedAsync(
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireActiveAsync(
                sessionId,
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);

            if (current.Phase != WatchdogLeasePhase.Prepared)
            {
                throw InvalidPhase(
                    current,
                    "CancelPrepared requires PREPARED.");
            }

            // PREPARED guarantees that this lease has not authorized any VFC
            // fan write. Clear only the ownership record; never touch hardware.
            await _journal.DeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            _active = null;
            _lastHeartbeatMs = 0;
            _operationStartedMs = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseOperationResult> WriteIntentAsync(
        Guid sessionId,
        long expectedGeneration,
        FanSetpoint target,
        CancellationToken cancellationToken)
    {
        if (!target.IsValidatedCustom)
        {
            throw new LeaseProtocolException(
                "TARGET_OUT_OF_RANGE",
                $"Setpoint {target} is outside the validated 14-50 range.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireActiveAsync(
                sessionId,
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);

            await ThrowIfExpiredLockedAsync(
                current,
                cancellationToken).ConfigureAwait(false);

            if (current.Phase is not
                (WatchdogLeasePhase.Prepared or WatchdogLeasePhase.Owned))
            {
                throw InvalidPhase(
                    current,
                    "WriteIntent requires PREPARED or OWNED.");
            }

            var previous =
                current.Phase == WatchdogLeasePhase.Owned
                    ? current.Owned
                    : null;

            var next = current with
            {
                Phase = WatchdogLeasePhase.WriteArmed,
                Generation = checked(current.Generation + 1),
                PreviousOwned = previous,
                Pending = target,
                Owned = null
            };

            // This durable store is the safety boundary. The caller may not
            // dispatch SetFanLevel until this method returns successfully.
            await _journal.StoreAsync(next, cancellationToken)
                .ConfigureAwait(false);

            _active = next;
            _operationStartedMs = _clock.Milliseconds;
            return Result(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseOperationResult> AbortWriteIntentAsync(
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireActiveAsync(
                sessionId,
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);

            await ThrowIfExpiredLockedAsync(
                current,
                cancellationToken).ConfigureAwait(false);

            if (current.Phase != WatchdogLeasePhase.WriteArmed)
            {
                throw InvalidPhase(
                    current,
                    "AbortWriteIntent requires WRITE_ARMED.");
            }

            var observed =
                await _hardware.ReadSetpointAsync(cancellationToken)
                    .ConfigureAwait(false);

            WatchdogLeaseRecord next;

            if (current.PreviousOwned.HasValue)
            {
                if (observed != current.PreviousOwned.Value)
                {
                    throw new LeaseProtocolException(
                        "ABORT_UNSAFE",
                        $"WriteIntent abort refused because EC is {observed}; expected the previously owned {current.PreviousOwned.Value}.");
                }

                next = current with
                {
                    Phase = WatchdogLeasePhase.Owned,
                    Generation = checked(current.Generation + 1),
                    PreviousOwned = null,
                    Pending = null,
                    Owned = current.PreviousOwned
                };

                _lastHeartbeatMs = _clock.Milliseconds;
            }
            else
            {
                if (!observed.IsFirmwareOwned)
                {
                    throw new LeaseProtocolException(
                        "ABORT_UNSAFE",
                        $"First WriteIntent abort refused because EC is {observed}; expected firmware-owned FF/FF.");
                }

                next = current with
                {
                    Phase = WatchdogLeasePhase.Prepared,
                    Generation = checked(current.Generation + 1),
                    PreviousOwned = null,
                    Pending = null,
                    Owned = null
                };
            }

            await _journal.StoreAsync(next, cancellationToken)
                .ConfigureAwait(false);

            _active = next;
            _operationStartedMs = _clock.Milliseconds;
            return Result(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseOperationResult> CommitAsync(
        Guid sessionId,
        long expectedGeneration,
        FanSetpoint acknowledgedTarget,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireActiveAsync(
                sessionId,
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);

            await ThrowIfExpiredLockedAsync(
                current,
                cancellationToken).ConfigureAwait(false);

            if (current.Phase != WatchdogLeasePhase.WriteArmed ||
                current.Pending != acknowledgedTarget)
            {
                throw InvalidPhase(
                    current,
                    $"Commit does not match pending target {current.Pending?.ToString() ?? "none"}.");
            }

            var next = current with
            {
                Phase = WatchdogLeasePhase.Owned,
                Generation = checked(current.Generation + 1),
                PreviousOwned = null,
                Pending = null,
                Owned = acknowledgedTarget
            };

            await _journal.StoreAsync(next, cancellationToken)
                .ConfigureAwait(false);

            _active = next;
            _lastHeartbeatMs = _clock.Milliseconds;
            _operationStartedMs = _clock.Milliseconds;
            return Result(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseOperationResult> HeartbeatAsync(
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireActiveAsync(
                sessionId,
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);

            await ThrowIfExpiredLockedAsync(
                current,
                cancellationToken).ConfigureAwait(false);

            if (current.Phase != WatchdogLeasePhase.Owned)
            {
                throw InvalidPhase(
                    current,
                    "Heartbeat is accepted only while OWNED.");
            }

            _lastHeartbeatMs = _clock.Milliseconds;
            return Result(current);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseOperationResult> RestoreBeginAsync(
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireActiveAsync(
                sessionId,
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);

            await ThrowIfExpiredLockedAsync(
                current,
                cancellationToken).ConfigureAwait(false);

            if (current.Phase is not
                (WatchdogLeasePhase.WriteArmed or WatchdogLeasePhase.Owned))
            {
                throw InvalidPhase(
                    current,
                    "RestoreBegin requires WRITE_ARMED or OWNED.");
            }

            var next = current with
            {
                Phase = WatchdogLeasePhase.Restoring,
                Generation = checked(current.Generation + 1),
                PreviousOwned =
                    current.PreviousOwned ??
                    current.Owned,
                Pending = current.Pending,
                Owned = current.Owned
            };

            await _journal.StoreAsync(next, cancellationToken)
                .ConfigureAwait(false);

            _active = next;
            _operationStartedMs = _clock.Milliseconds;
            return Result(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireActiveAsync(
                sessionId,
                expectedGeneration,
                cancellationToken).ConfigureAwait(false);

            if (current.Phase != WatchdogLeasePhase.Restoring)
            {
                throw InvalidPhase(
                    current,
                    "Release requires RESTORING.");
            }

            // FF/FF alone is not sufficient proof that the complete HP
            // handoff finished: it can be the midpoint between fixed-level
            // release and LegacyDefault. Therefore Release never merely drops
            // the durable lease. It asks the watchdog hardware adapter to
            // complete/normalize the validated restore primitive, then clears
            // ownership only after FF/FF is verified.
            var recovery =
                await RecoverPotentialWriteLockedAsync(
                    current,
                    "controller release",
                    cancellationToken).ConfigureAwait(false);

            if (recovery.Disposition !=
                LeaseRecoveryDisposition.RestoredFirmware)
            {
                throw new LeaseProtocolException(
                    "RESTORE_NOT_VERIFIED",
                    $"Release refused: {recovery.Detail}");
            }

            _lastHeartbeatMs = 0;
            _operationStartedMs = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseRecoveryResult> RecoverOnStartupAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WatchdogLeaseRecord? record;
            try
            {
                record = await _journal.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                var observed =
                    await _hardware.ReadSetpointAsync(cancellationToken)
                        .ConfigureAwait(false);

                return new LeaseRecoveryResult(
                    LeaseRecoveryDisposition.JournalInvalid,
                    observed,
                    RestoreAttempted: false,
                    JournalRetained: true,
                    $"Journal is invalid; no restore attempted: {ex.Message}");
            }

            _active = record;

            if (record is null)
            {
                var observed =
                    await _hardware.ReadSetpointAsync(cancellationToken)
                        .ConfigureAwait(false);

                return new LeaseRecoveryResult(
                    observed.IsFirmwareOwned
                        ? LeaseRecoveryDisposition.Ready
                        : LeaseRecoveryDisposition.ExternalOverrideBlocked,
                    observed,
                    RestoreAttempted: false,
                    JournalRetained: false,
                    observed.IsFirmwareOwned
                        ? "No journal and EC is FF/FF."
                        : $"No VFC lease exists; fixed external setpoint {observed} is not cleared.");
            }

            if (record.Phase == WatchdogLeasePhase.Prepared)
            {
                var observed =
                    await _hardware.ReadSetpointAsync(cancellationToken)
                        .ConfigureAwait(false);

                await _journal.DeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                _active = null;

                return new LeaseRecoveryResult(
                    observed.IsFirmwareOwned
                        ? LeaseRecoveryDisposition.ClearedPrepared
                        : LeaseRecoveryDisposition.ExternalOverrideBlocked,
                    observed,
                    RestoreAttempted: false,
                    JournalRetained: false,
                    observed.IsFirmwareOwned
                        ? "PREPARED contains no possible hardware write; journal cleared without restore."
                        : $"PREPARED was cleared without restore, but external fixed setpoint {observed} keeps admission blocked.");
            }

            return await RecoverPotentialWriteLockedAsync(
                record,
                "service startup",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseRecoveryResult?> HandleOwnerLossAsync(
        ControllerIdentity controller,
        string reason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current =
                _active ??
                await _journal.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (current is null ||
                current.Controller != controller)
            {
                return null;
            }

            _active = current;

            if (current.Phase == WatchdogLeasePhase.Prepared)
            {
                var observed =
                    await _hardware.ReadSetpointAsync(cancellationToken)
                        .ConfigureAwait(false);

                await _journal.DeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                _active = null;

                return new LeaseRecoveryResult(
                    observed.IsFirmwareOwned
                        ? LeaseRecoveryDisposition.ClearedPrepared
                        : LeaseRecoveryDisposition.ExternalOverrideBlocked,
                    observed,
                    RestoreAttempted: false,
                    JournalRetained: false,
                    observed.IsFirmwareOwned
                        ? $"Owner loss ({reason}) before any write; PREPARED cleared."
                        : $"Owner loss ({reason}) before any VFC write; PREPARED cleared but external fixed setpoint {observed} remains blocked.");
            }

            return await RecoverPotentialWriteLockedAsync(
                current,
                $"owner loss: {reason}",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseRecoveryResult?> RecoverForServiceStopAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WatchdogLeaseRecord? current;
            try
            {
                current =
                    _active ??
                    await _journal.LoadAsync(cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // A corrupt ownership journal cannot authorize a blind restore.
                return null;
            }

            if (current is null)
            {
                return null;
            }

            _active = current;

            if (current.Phase == WatchdogLeasePhase.Prepared)
            {
                var observed =
                    await _hardware.ReadSetpointAsync(cancellationToken)
                        .ConfigureAwait(false);

                await _journal.DeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                _active = null;
                _lastHeartbeatMs = 0;
                _operationStartedMs = 0;

                return new LeaseRecoveryResult(
                    observed.IsFirmwareOwned
                        ? LeaseRecoveryDisposition.ClearedPrepared
                        : LeaseRecoveryDisposition.ExternalOverrideBlocked,
                    observed,
                    RestoreAttempted: false,
                    JournalRetained: false,
                    observed.IsFirmwareOwned
                        ? $"{reason}: PREPARED cleared; no hardware write was authorized."
                        : $"{reason}: PREPARED cleared without touching external fixed setpoint {observed}.");
            }

            return await RecoverPotentialWriteLockedAsync(
                current,
                reason,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LeaseRecoveryResult?> CheckDeadlinesAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _active;
            if (current is null)
            {
                return null;
            }

            var reason = GetExpiredReasonLocked(current);

            if (reason is null)
            {
                return null;
            }

            return await RecoverPotentialWriteLockedAsync(
                current,
                reason,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ThrowIfExpiredLockedAsync(
        WatchdogLeaseRecord current,
        CancellationToken cancellationToken)
    {
        var reason = GetExpiredReasonLocked(current);
        if (reason is null)
        {
            return;
        }

        var recovery =
            await RecoverPotentialWriteLockedAsync(
                current,
                reason,
                cancellationToken).ConfigureAwait(false);

        throw new LeaseProtocolException(
            "LEASE_EXPIRED",
            $"{reason}; controller command refused. {recovery.Detail}");
    }

    private string? GetExpiredReasonLocked(
        WatchdogLeaseRecord current)
    {
        var now = _clock.Milliseconds;

        return current.Phase switch
        {
            WatchdogLeasePhase.Owned
                when Elapsed(now, _lastHeartbeatMs) >
                     OwnedHeartbeatTimeout =>
                "OWNED heartbeat timeout",

            WatchdogLeasePhase.WriteArmed
                when Elapsed(now, _operationStartedMs) >
                     WriteArmedDeadline =>
                "WRITE_ARMED operation deadline",

            WatchdogLeasePhase.Restoring
                when Elapsed(now, _operationStartedMs) >
                     RestoringDeadline =>
                "RESTORING takeover deadline",

            _ => null
        };
    }

    private async ValueTask EnsureNoExistingLeaseAsync(
        CancellationToken cancellationToken)
    {
        if (_active is not null)
        {
            throw new LeaseProtocolException(
                "LEASE_BUSY",
                "A watchdog lease is already active.");
        }

        var existing =
            await _journal.LoadAsync(cancellationToken)
                .ConfigureAwait(false);

        if (existing is not null)
        {
            _active = existing;
            throw new LeaseProtocolException(
                "LEASE_BUSY",
                $"Durable lease {existing.SessionId} is still active in phase {existing.Phase}.");
        }
    }

    private async ValueTask<WatchdogLeaseRecord> RequireActiveAsync(
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        var current =
            _active ??
            await _journal.LoadAsync(cancellationToken)
                .ConfigureAwait(false);

        if (current is null)
        {
            throw new LeaseProtocolException(
                "NO_ACTIVE_LEASE",
                "No watchdog lease is active.");
        }

        _active = current;

        if (current.SessionId != sessionId)
        {
            throw new LeaseProtocolException(
                "SESSION_MISMATCH",
                "Lease session id does not match the active session.");
        }

        if (current.Generation != expectedGeneration)
        {
            throw new LeaseProtocolException(
                "STALE_GENERATION",
                $"Expected generation {current.Generation}, received {expectedGeneration}.");
        }

        return current;
    }

    private async ValueTask<LeaseRecoveryResult>
        RecoverPotentialWriteLockedAsync(
            WatchdogLeaseRecord record,
            string reason,
            CancellationToken cancellationToken)
    {
        var observed =
            await _hardware.ReadSetpointAsync(cancellationToken)
                .ConfigureAwait(false);

        // PREPARED is handled before this method because no hardware
        // write is possible in that phase. From WRITE_ARMED onward, however,
        // the watchdog must assume a SetFanLevel dispatch may have occurred.
        // Even an observed FF/FF can be the midpoint of a partially completed
        // FF/FF -> LegacyDefault handoff, so complete the validated restore
        // primitive instead of merely deleting ownership evidence.
        if (!observed.IsFirmwareOwned &&
            !AllowedSetpoints(record).Contains(observed))
        {
            return new LeaseRecoveryResult(
                LeaseRecoveryDisposition.OwnershipAmbiguous,
                observed,
                RestoreAttempted: false,
                JournalRetained: true,
                $"{reason}: observed {observed} does not match any setpoint permitted by lease {record.SessionId}; no restore.");
        }

        try
        {
            await _hardware.RestoreFirmwareAutoAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            var after =
                await _hardware.ReadSetpointAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!after.IsFirmwareOwned)
            {
                return new LeaseRecoveryResult(
                    LeaseRecoveryDisposition.RestoreFailed,
                    after,
                    RestoreAttempted: true,
                    JournalRetained: true,
                    $"{reason}: restore returned but EC is {after}, not FF/FF.");
            }

            await _journal.DeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            _active = null;

            return new LeaseRecoveryResult(
                LeaseRecoveryDisposition.RestoredFirmware,
                after,
                RestoreAttempted: true,
                JournalRetained: false,
                observed.IsFirmwareOwned
                    ? $"{reason}: EC was already FF/FF, but the active {record.Phase} lease required completion/normalization of the validated firmware restore."
                    : $"{reason}: VFC-owned setpoint {observed} restored to FF/FF.");
        }
        catch (Exception ex)
        {
            return new LeaseRecoveryResult(
                LeaseRecoveryDisposition.RestoreFailed,
                observed,
                RestoreAttempted: true,
                JournalRetained: true,
                $"{reason}: restore failed: {ex.Message}");
        }
    }

    private static HashSet<FanSetpoint> AllowedSetpoints(
        WatchdogLeaseRecord record)
    {
        var set = new HashSet<FanSetpoint>();

        if (record.PreviousOwned.HasValue)
        {
            set.Add(record.PreviousOwned.Value);
        }

        if (record.Pending.HasValue)
        {
            set.Add(record.Pending.Value);
        }

        if (record.Owned.HasValue)
        {
            set.Add(record.Owned.Value);
        }

        return set;
    }

    private static TimeSpan Elapsed(
        ulong now,
        ulong then)
    {
        var delta = unchecked(now - then);
        return TimeSpan.FromMilliseconds(delta);
    }

    private static LeaseOperationResult Result(
        WatchdogLeaseRecord record) =>
        new(
            record.SessionId,
            record.Generation,
            record.Phase);

    private static LeaseProtocolException InvalidPhase(
        WatchdogLeaseRecord record,
        string detail) =>
        new(
            "INVALID_PHASE",
            $"{detail} Current phase={record.Phase}, generation={record.Generation}.");
}
