namespace VictusFanControl.Performance;

internal enum CpuPowerRecoveryExecutionDisposition
{
    NoJournal,
    ClearedAlreadyReleased,
    ClearedExternalPreserved,
    RestoredAndCleared,
    BlockedByLock,
    DeferredConcurrentHardwareChange,
    DeferredConcurrentJournalChange,
    ResolvedJournalRetained,
    UnresolvedJournalRetained
}

internal readonly record struct CpuPowerRecoveryExecutionResult(
    CpuPowerRecoveryExecutionDisposition Disposition,
    bool WriteAttempted,
    bool JournalRetained,
    string Detail);

/// <summary>
/// Executes only release/recovery actions after guardian death.
///
/// This component has no authority to apply or reacquire the user's requested
/// PL1/PL2 values. Its only permitted write is a release restore selected by
/// CpuPowerRecoveryPlanner, journaled as Restoring before the write.
/// </summary>
internal sealed class CpuPowerRecoveryExecutor
{
    private readonly ICpuPowerLimitBackend _backend;
    private readonly ICpuPowerSessionJournal _journal;

    internal CpuPowerRecoveryExecutor(
        ICpuPowerLimitBackend backend,
        ICpuPowerSessionJournal journal)
    {
        _backend =
            backend ??
            throw new ArgumentNullException(nameof(backend));

        _journal =
            journal ??
            throw new ArgumentNullException(nameof(journal));
    }

    internal CpuPowerRecoveryExecutionResult Execute()
    {
        CpuPowerSessionJournalRecord? record;

        try
        {
            record = _journal.Load();
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_JOURNAL_LOAD_ERROR: " +
                ex.Message);
        }

        if (record is null)
        {
            return new CpuPowerRecoveryExecutionResult(
                CpuPowerRecoveryExecutionDisposition.NoJournal,
                WriteAttempted: false,
                JournalRetained: false,
                "No CPU power recovery journal is present.");
        }

        if (!_backend.IsSupported)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_BACKEND_UNSUPPORTED");
        }

        if (record.SchemaVersion !=
                CpuPowerSessionJournalRecord.CurrentSchemaVersion ||
            !string.Equals(
                record.TargetProfileId,
                _journal.TargetProfileId,
                StringComparison.Ordinal))
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_JOURNAL_IDENTITY_REJECTED");
        }

        CpuPowerLimitSnapshot current;

        try
        {
            current = _backend.Read();
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_INITIAL_READ_ERROR: " +
                ex.Message);
        }

        CpuPowerRecoveryPlan recoveryPlan;

        try
        {
            recoveryPlan =
                CpuPowerRecoveryPlanner.Plan(
                    record,
                    current,
                    _backend);
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_PLAN_ERROR: " +
                ex.Message);
        }

        return recoveryPlan.Disposition switch
        {
            CpuPowerRecoveryDisposition.ClearAlreadyReleased =>
                DeleteResolved(
                    CpuPowerRecoveryExecutionDisposition.ClearedAlreadyReleased,
                    writeAttempted: false,
                    recoveryPlan.Detail),

            CpuPowerRecoveryDisposition.PreserveExternalAndClear =>
                DeleteResolved(
                    CpuPowerRecoveryExecutionDisposition.ClearedExternalPreserved,
                    writeAttempted: false,
                    recoveryPlan.Detail),

            CpuPowerRecoveryDisposition.BlockedByLockRetainJournal =>
                Retained(
                    CpuPowerRecoveryExecutionDisposition.BlockedByLock,
                    writeAttempted: false,
                    recoveryPlan.Detail),

            CpuPowerRecoveryDisposition.RestoreOwnedThenClear =>
                ExecuteReleaseRestore(
                    record,
                    current,
                    recoveryPlan),

            _ =>
                Retained(
                    CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                    writeAttempted: false,
                    "CPU_POWER_RECOVERY_UNKNOWN_DISPOSITION")
        };
    }

    private CpuPowerRecoveryExecutionResult ExecuteReleaseRestore(
        CpuPowerSessionJournalRecord record,
        CpuPowerLimitSnapshot initialCurrent,
        CpuPowerRecoveryPlan recoveryPlan)
    {
        if (!recoveryPlan.RestoreTarget.HasValue)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_RESTORE_TARGET_MISSING");
        }

        var restoreTarget =
            recoveryPlan.RestoreTarget.Value;

        if (restoreTarget.Locked)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_LOCKED_RESTORE_TARGET_REJECTED");
        }

        var ownedRaw =
            ResolveOwnedRawForRelease(
                record,
                initialCurrent);

        if (!ownedRaw.HasValue)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_OWNED_RAW_NOT_IDENTIFIABLE");
        }

        CpuPowerLimitRestorePlan backendPlan;

        try
        {
            backendPlan =
                _backend.PlanRestore(
                    restoreTarget,
                    ownedRaw.Value,
                    initialCurrent);
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_RESTORE_PLAN_ERROR: " +
                ex.Message);
        }

        if (backendPlan.Value ==
            initialCurrent.Raw)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_RESTORE_PLAN_NOOP_WHILE_OWNED: " +
                backendPlan.Status);
        }

        if (!JournalGenerationStillMatches(
                record,
                out var journalCheckError))
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.DeferredConcurrentJournalChange,
                writeAttempted: false,
                journalCheckError);
        }

        var restoring =
            record with
            {
                Generation =
                    checked(record.Generation + 1),

                Phase =
                    CpuPowerJournalPhase.Restoring,

                AppliedRaw =
                    ownedRaw.Value,

                Conflict =
                    NormalizeConflictForRecovery(
                        record.Conflict),

                PendingRaw =
                    backendPlan.Value,

                UpdatedAtUtc =
                    DateTimeOffset.UtcNow
            };

        try
        {
            _journal.Store(restoring);
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_RESTORING_JOURNAL_ERROR: " +
                ex.Message);
        }

        CpuPowerLimitSnapshot compareCurrent;

        try
        {
            compareCurrent =
                _backend.Read();
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_COMPARE_READ_ERROR: " +
                ex.Message);
        }

        if (compareCurrent.Raw !=
            initialCurrent.Raw)
        {
            return ResolveConcurrentHardwareChange(
                record,
                restoring,
                compareCurrent);
        }

        if (!JournalGenerationStillMatches(
                restoring,
                out journalCheckError))
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.DeferredConcurrentJournalChange,
                writeAttempted: false,
                journalCheckError);
        }

        try
        {
            _backend.Write(
                backendPlan.Value);
        }
        catch (Exception ex)
        {
            return ResolveAfterWriteProblem(
                record,
                restoring,
                writeAttempted: true,
                "CPU_POWER_RECOVERY_WRITE_ERROR: " +
                ex.Message);
        }

        CpuPowerLimitSnapshot final;

        try
        {
            final =
                _backend.Read();
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted: true,
                "CPU_POWER_RECOVERY_POSTWRITE_READ_ERROR: " +
                ex.Message);
        }

        if (final.Raw ==
            backendPlan.Value)
        {
            return DeleteResolved(
                CpuPowerRecoveryExecutionDisposition.RestoredAndCleared,
                writeAttempted: true,
                "Recovery release restore completed with exact readback.");
        }

        return ResolveObservedPostWriteState(
            record,
            restoring,
            final,
            writeAttempted: true,
            "CPU_POWER_RECOVERY_POSTWRITE_READBACK_MISMATCH");
    }

    private CpuPowerRecoveryExecutionResult ResolveConcurrentHardwareChange(
        CpuPowerSessionJournalRecord originalRecord,
        CpuPowerSessionJournalRecord restoring,
        CpuPowerLimitSnapshot current)
    {
        if (IsPresetSwitchOwnedCandidate(
                originalRecord,
                current))
        {
            return RetainPresetSwitchRecoveryEvidence(
                originalRecord,
                restoring,
                current,
                writeAttempted: false,
                "Hardware changed between recovery planning and compare-read to the other VFC preset candidate; no write was issued.");
        }

        CpuPowerRecoveryPlan replanned;

        try
        {
            replanned =
                CpuPowerRecoveryPlanner.Plan(
                    restoring,
                    current,
                    _backend);
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.DeferredConcurrentHardwareChange,
                writeAttempted: false,
                "CPU_POWER_RECOVERY_CONCURRENT_REPLAN_ERROR: " +
                ex.Message);
        }

        return replanned.Disposition switch
        {
            CpuPowerRecoveryDisposition.ClearAlreadyReleased =>
                DeleteResolved(
                    CpuPowerRecoveryExecutionDisposition.ClearedAlreadyReleased,
                    writeAttempted: false,
                    "Hardware changed before recovery write, but release target is already present."),

            CpuPowerRecoveryDisposition.PreserveExternalAndClear =>
                DeleteResolved(
                    CpuPowerRecoveryExecutionDisposition.ClearedExternalPreserved,
                    writeAttempted: false,
                    "Hardware changed before recovery write; external owner is preserved."),

            CpuPowerRecoveryDisposition.BlockedByLockRetainJournal =>
                Retained(
                    CpuPowerRecoveryExecutionDisposition.BlockedByLock,
                    writeAttempted: false,
                    "Hardware changed before recovery write and is now locked while still owned."),

            _ =>
                Retained(
                    CpuPowerRecoveryExecutionDisposition.DeferredConcurrentHardwareChange,
                    writeAttempted: false,
                    "Hardware changed between recovery planning and compare-read; no write was issued.")
        };
    }

    private CpuPowerRecoveryExecutionResult ResolveAfterWriteProblem(
        CpuPowerSessionJournalRecord originalRecord,
        CpuPowerSessionJournalRecord restoring,
        bool writeAttempted,
        string error)
    {
        CpuPowerLimitSnapshot observed;

        try
        {
            observed =
                _backend.Read();
        }
        catch (Exception readError)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted,
                error +
                " | CPU_POWER_RECOVERY_POSTERROR_READ_ERROR: " +
                readError.Message);
        }

        return ResolveObservedPostWriteState(
            originalRecord,
            restoring,
            observed,
            writeAttempted,
            error);
    }

    private CpuPowerRecoveryExecutionResult ResolveObservedPostWriteState(
        CpuPowerSessionJournalRecord originalRecord,
        CpuPowerSessionJournalRecord restoring,
        CpuPowerLimitSnapshot observed,
        bool writeAttempted,
        string prefix)
    {
        if (IsPresetSwitchOwnedCandidate(
                originalRecord,
                observed))
        {
            return RetainPresetSwitchRecoveryEvidence(
                originalRecord,
                restoring,
                observed,
                writeAttempted,
                prefix +
                " | old/new VFC preset candidate is still present; no second recovery write is permitted.");
        }

        CpuPowerRecoveryPlan replanned;

        try
        {
            replanned =
                CpuPowerRecoveryPlanner.Plan(
                    restoring,
                    observed,
                    _backend);
        }
        catch (Exception ex)
        {
            TryMarkUnresolved(
                restoring);

            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted,
                prefix +
                " | CPU_POWER_RECOVERY_POSTWRITE_REPLAN_ERROR: " +
                ex.Message);
        }

        switch (replanned.Disposition)
        {
            case CpuPowerRecoveryDisposition.ClearAlreadyReleased:
                return DeleteResolved(
                    CpuPowerRecoveryExecutionDisposition.RestoredAndCleared,
                    writeAttempted,
                    prefix +
                    " | release target/equivalent owned fields are present.");

            case CpuPowerRecoveryDisposition.PreserveExternalAndClear:
                return DeleteResolved(
                    CpuPowerRecoveryExecutionDisposition.ClearedExternalPreserved,
                    writeAttempted,
                    prefix +
                    " | another writer now owns PL1/PL2; preserving it.");

            case CpuPowerRecoveryDisposition.BlockedByLockRetainJournal:
                return Retained(
                    CpuPowerRecoveryExecutionDisposition.BlockedByLock,
                    writeAttempted,
                    prefix +
                    " | requested PL fields remain but register is locked.");

            default:
                TryMarkUnresolved(
                    restoring);

                return Retained(
                    CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                    writeAttempted,
                    prefix +
                    " | requested PL fields still require release; executor will not issue a second write.");
        }
    }


    private ulong? ResolveOwnedRawForRelease(
        CpuPowerSessionJournalRecord record,
        CpuPowerLimitSnapshot current)
    {
        if (record.Phase !=
            CpuPowerJournalPhase.PresetSwitchWriteArmed)
        {
            return record.AppliedRaw;
        }

        if (!record.PendingRaw.HasValue)
            return null;

        if (_backend.OwnedFieldsMatch(
                record.PendingRaw.Value,
                current))
        {
            return record.PendingRaw.Value;
        }

        if (_backend.OwnedFieldsMatch(
                record.AppliedRaw,
                current))
        {
            return record.AppliedRaw;
        }

        return null;
    }

    private bool IsPresetSwitchOwnedCandidate(
        CpuPowerSessionJournalRecord originalRecord,
        CpuPowerLimitSnapshot current)
    {
        if (originalRecord.Phase !=
                CpuPowerJournalPhase.PresetSwitchWriteArmed ||
            !originalRecord.PendingRaw.HasValue)
        {
            return false;
        }

        return
            _backend.OwnedFieldsMatch(
                originalRecord.AppliedRaw,
                current) ||
            _backend.OwnedFieldsMatch(
                originalRecord.PendingRaw.Value,
                current);
    }

    private CpuPowerRecoveryExecutionResult RetainPresetSwitchRecoveryEvidence(
        CpuPowerSessionJournalRecord originalRecord,
        CpuPowerSessionJournalRecord restoring,
        CpuPowerLimitSnapshot current,
        bool writeAttempted,
        string detail)
    {
        try
        {
            var retained =
                originalRecord with
                {
                    Generation =
                        checked(restoring.Generation + 1),

                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                };

            _journal.Store(
                retained);

            return Retained(
                CpuPowerRecoveryExecutionDisposition.DeferredConcurrentHardwareChange,
                writeAttempted,
                detail +
                $" Observed raw 0x{current.Raw:X}; PresetSwitchWriteArmed retained for release-only retry.");
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
                writeAttempted,
                detail +
                " | CPU_POWER_RECOVERY_PRESET_SWITCH_EVIDENCE_RETAIN_ERROR: " +
                ex.Message);
        }
    }

    private bool JournalGenerationStillMatches(
        CpuPowerSessionJournalRecord expected,
        out string error)
    {
        try
        {
            var current =
                _journal.Load();

            if (current is null)
            {
                error =
                    "CPU_POWER_RECOVERY_JOURNAL_DISAPPEARED";
                return false;
            }

            if (current.SessionId !=
                    expected.SessionId ||
                current.Generation !=
                    expected.Generation ||
                current.Phase !=
                    expected.Phase ||
                current.PendingRaw !=
                    expected.PendingRaw)
            {
                error =
                    "CPU_POWER_RECOVERY_JOURNAL_CHANGED_CONCURRENTLY";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error =
                "CPU_POWER_RECOVERY_JOURNAL_RECHECK_ERROR: " +
                ex.Message;
            return false;
        }
    }

    private void TryMarkUnresolved(
        CpuPowerSessionJournalRecord restoring)
    {
        try
        {
            var unresolved =
                restoring with
                {
                    Generation =
                        checked(restoring.Generation + 1),

                    Phase =
                        CpuPowerJournalPhase.Unresolved,

                    Conflict =
                        NormalizeConflictForRecovery(
                            restoring.Conflict),

                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow
                };

            _journal.Store(
                unresolved);
        }
        catch
        {
            // The prior durable Restoring record is intentionally retained.
        }
    }

    private CpuPowerRecoveryExecutionResult DeleteResolved(
        CpuPowerRecoveryExecutionDisposition successDisposition,
        bool writeAttempted,
        string detail)
    {
        try
        {
            _journal.Delete();

            return new CpuPowerRecoveryExecutionResult(
                successDisposition,
                writeAttempted,
                JournalRetained: false,
                detail);
        }
        catch (Exception ex)
        {
            return Retained(
                CpuPowerRecoveryExecutionDisposition.ResolvedJournalRetained,
                writeAttempted,
                detail +
                " | CPU_POWER_RECOVERY_JOURNAL_DELETE_ERROR: " +
                ex.Message);
        }
    }

    private static CpuPowerConflictSnapshot NormalizeConflictForRecovery(
        CpuPowerConflictSnapshot conflict) =>
        conflict with
        {
            AttemptInFlight = false
        };

    private static CpuPowerRecoveryExecutionResult Retained(
        CpuPowerRecoveryExecutionDisposition disposition,
        bool writeAttempted,
        string detail) =>
        new(
            disposition,
            writeAttempted,
            JournalRetained: true,
            detail);
}
