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

            var observed =
                await _hardware.ReadSetpointAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!observed.IsFirmwareOwned)
            {
                throw new LeaseProtocolException(
                    "FIRMWARE_NOT_VERIFIED",
                    $"Release refused because EC is still {observed}, not FF/FF.");
            }

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
                    LeaseRecoveryDisposition.ClearedPrepared,
                    observed,
                    RestoreAttempted: false,
                    JournalRetained: false,
                    "PREPARED contains no possible hardware write; journal cleared without restore.");
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
                    LeaseRecoveryDisposition.ClearedPrepared,
                    observed,
                    RestoreAttempted: false,
                    JournalRetained: false,
                    $"Owner loss ({reason}) before any write; PREPARED cleared.");
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

            var now = _clock.Milliseconds;
            string? reason = current.Phase switch
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

        if (observed.IsFirmwareOwned)
        {
            await _journal.DeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            _active = null;

            return new LeaseRecoveryResult(
                LeaseRecoveryDisposition.ClearedAlreadyFirmware,
                observed,
                RestoreAttempted: false,
                JournalRetained: false,
                $"{reason}: EC already FF/FF; lease cleared.");
        }

        if (!AllowedSetpoints(record).Contains(observed))
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
                $"{reason}: VFC-owned setpoint {observed} restored to FF/FF.");
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
