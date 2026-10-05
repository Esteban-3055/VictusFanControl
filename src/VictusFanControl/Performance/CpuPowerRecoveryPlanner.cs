namespace VictusFanControl.Performance;

/// <summary>
/// Recovery after guardian death is release-only. There is deliberately no
/// action that resumes a Contested retry budget or reapplies the requested
/// PL1/PL2 values.
/// </summary>
internal enum CpuPowerRecoveryDisposition
{
    ClearAlreadyReleased,
    PreserveExternalAndClear,
    RestoreOwnedThenClear,
    BlockedByLockRetainJournal
}

internal readonly record struct CpuPowerRecoveryPlan(
    CpuPowerRecoveryDisposition Disposition,
    CpuPowerLimitSnapshot Observed,
    CpuPowerLimitSnapshot? RestoreTarget,
    string Detail)
{
    internal bool AllowsWrite =>
        Disposition ==
        CpuPowerRecoveryDisposition.RestoreOwnedThenClear;

    internal bool ClearsWithoutWrite =>
        Disposition is
            CpuPowerRecoveryDisposition.ClearAlreadyReleased or
            CpuPowerRecoveryDisposition.PreserveExternalAndClear;

    internal bool RetainsJournal =>
        Disposition ==
        CpuPowerRecoveryDisposition.BlockedByLockRetainJournal;
}

internal static class CpuPowerRecoveryPlanner
{
    internal static CpuPowerRecoveryPlan Plan(
        CpuPowerSessionJournalRecord journal,
        CpuPowerLimitSnapshot current,
        ICpuPowerOwnershipComparer ownership)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(ownership);

        var restoreTarget =
            journal.ExternalHandoff ??
            journal.OriginalBaseline;

        var requestedOwned =
            ownership.OwnedFieldsMatch(
                journal.AppliedRaw,
                current);

        var restoreTargetPresent =
            ownership.OwnedFieldsMatch(
                restoreTarget.Raw,
                current);

        return journal.Phase switch
        {
            CpuPowerJournalPhase.WriteArmed =>
                PlanPotentiallyOwned(
                    current,
                    restoreTarget,
                    requestedOwned,
                    restoreTargetPresent,
                    "initial write may have completed before guardian death"),

            CpuPowerJournalPhase.Owned =>
                PlanPotentiallyOwned(
                    current,
                    restoreTarget,
                    requestedOwned,
                    restoreTargetPresent,
                    "journal says VictusFanControl owned the requested PL fields"),

            CpuPowerJournalPhase.Contested =>
                PreserveExternal(
                    current,
                    "Contested is recovery-only after guardian death; retry budget is not resumed"),

            CpuPowerJournalPhase.ReacquireWriteArmed =>
                PlanPotentiallyOwned(
                    current,
                    restoreTarget,
                    requestedOwned,
                    restoreTargetPresent,
                    "bounded reacquire may have completed before guardian death"),

            CpuPowerJournalPhase.PresetSwitchWriteArmed =>
                PlanPresetSwitchWriteArmed(
                    journal,
                    current,
                    restoreTarget,
                    restoreTargetPresent,
                    ownership),

            CpuPowerJournalPhase.Stability =>
                PlanPotentiallyOwned(
                    current,
                    restoreTarget,
                    requestedOwned,
                    restoreTargetPresent,
                    "reacquired value was provisional; recovery releases it instead of resuming stability"),

            CpuPowerJournalPhase.Yielded =>
                PreserveExternal(
                    current,
                    "Yielded authority is never reacquired by a restarted guardian"),

            CpuPowerJournalPhase.Restoring =>
                PlanRestoring(
                    journal,
                    current,
                    restoreTarget,
                    requestedOwned,
                    restoreTargetPresent,
                    ownership),

            CpuPowerJournalPhase.Unresolved =>
                PlanPotentiallyOwned(
                    current,
                    restoreTarget,
                    requestedOwned,
                    restoreTargetPresent,
                    "unresolved session may be repaired only by releasing still-owned PL fields"),

            _ =>
                throw new InvalidOperationException(
                    $"Unknown CPU power journal phase {journal.Phase}.")
        };
    }


    private static CpuPowerRecoveryPlan PlanPresetSwitchWriteArmed(
        CpuPowerSessionJournalRecord journal,
        CpuPowerLimitSnapshot current,
        CpuPowerLimitSnapshot restoreTarget,
        bool restoreTargetPresent,
        ICpuPowerOwnershipComparer ownership)
    {
        if (!journal.PendingRaw.HasValue)
        {
            throw new InvalidOperationException(
                "PresetSwitchWriteArmed journal is missing PendingRaw.");
        }

        if (restoreTargetPresent)
        {
            return new CpuPowerRecoveryPlan(
                CpuPowerRecoveryDisposition.ClearAlreadyReleased,
                current,
                null,
                "preset switch recovery found the release target already present");
        }

        var oldPresetOwned =
            ownership.OwnedFieldsMatch(
                journal.AppliedRaw,
                current);

        var newPresetOwned =
            ownership.OwnedFieldsMatch(
                journal.PendingRaw.Value,
                current);

        if (!oldPresetOwned &&
            !newPresetOwned)
        {
            return PreserveExternal(
                current,
                "preset switch recovery found neither old nor new VFC-owned PL fields; preserve external owner");
        }

        if (current.Locked)
        {
            return new CpuPowerRecoveryPlan(
                CpuPowerRecoveryDisposition.BlockedByLockRetainJournal,
                current,
                restoreTarget,
                "preset switch recovery found VFC-owned PL fields locked; no unlock/bypass write is permitted");
        }

        return new CpuPowerRecoveryPlan(
            CpuPowerRecoveryDisposition.RestoreOwnedThenClear,
            current,
            restoreTarget,
            oldPresetOwned
                ? "preset switch crashed with old VFC-owned PL fields still present; release-only recovery is allowed"
                : "preset switch crashed after new VFC-owned PL fields appeared; release-only recovery is allowed");
    }

    private static CpuPowerRecoveryPlan PlanPotentiallyOwned(
        CpuPowerLimitSnapshot current,
        CpuPowerLimitSnapshot restoreTarget,
        bool requestedOwned,
        bool restoreTargetPresent,
        string reason)
    {
        if (restoreTargetPresent)
        {
            return new CpuPowerRecoveryPlan(
                CpuPowerRecoveryDisposition.ClearAlreadyReleased,
                current,
                null,
                reason +
                "; restore/handoff PL fields are already present, so no write is needed");
        }

        if (!requestedOwned)
        {
            return PreserveExternal(
                current,
                reason +
                "; requested PL fields are no longer present");
        }

        if (current.Locked)
        {
            return new CpuPowerRecoveryPlan(
                CpuPowerRecoveryDisposition.BlockedByLockRetainJournal,
                current,
                restoreTarget,
                reason +
                "; requested PL fields remain present but MSR lock is set");
        }

        return new CpuPowerRecoveryPlan(
            CpuPowerRecoveryDisposition.RestoreOwnedThenClear,
            current,
            restoreTarget,
            reason +
            "; requested PL fields still match, so one conditional release restore is allowed");
    }

    private static CpuPowerRecoveryPlan PlanRestoring(
        CpuPowerSessionJournalRecord journal,
        CpuPowerLimitSnapshot current,
        CpuPowerLimitSnapshot restoreTarget,
        bool requestedOwned,
        bool restoreTargetPresent,
        ICpuPowerOwnershipComparer ownership)
    {
        if (!journal.PendingRaw.HasValue)
        {
            throw new InvalidOperationException(
                "Restoring journal is missing PendingRaw.");
        }

        var pendingPresent =
            current.Raw ==
            journal.PendingRaw.Value ||
            ownership.OwnedFieldsMatch(
                journal.PendingRaw.Value,
                current);

        if (pendingPresent ||
            restoreTargetPresent)
        {
            return new CpuPowerRecoveryPlan(
                CpuPowerRecoveryDisposition.ClearAlreadyReleased,
                current,
                null,
                "restore was already applied, or equivalent restore-target PL fields are present");
        }

        if (!requestedOwned)
        {
            return PreserveExternal(
                current,
                "restore result is not present, but another writer now owns PL1/PL2; preserve it");
        }

        if (current.Locked)
        {
            return new CpuPowerRecoveryPlan(
                CpuPowerRecoveryDisposition.BlockedByLockRetainJournal,
                current,
                restoreTarget,
                "restore was armed but requested PL fields remain locked; no unlock/bypass write is permitted");
        }

        return new CpuPowerRecoveryPlan(
            CpuPowerRecoveryDisposition.RestoreOwnedThenClear,
            current,
            restoreTarget,
            "restore was armed but requested PL fields are still present; one conditional recovery restore is allowed");
    }

    private static CpuPowerRecoveryPlan PreserveExternal(
        CpuPowerLimitSnapshot current,
        string detail) =>
        new(
            CpuPowerRecoveryDisposition.PreserveExternalAndClear,
            current,
            null,
            detail);
}
