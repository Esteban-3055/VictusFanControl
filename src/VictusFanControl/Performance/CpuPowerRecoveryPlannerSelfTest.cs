namespace VictusFanControl.Performance;

internal static class CpuPowerRecoveryPlannerSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const ulong BaselineRaw = 0x1000;
    private const ulong AppliedRaw = 0x2000;
    private const ulong AppliedMetadataRaw = 0x2100;
    private const ulong ExternalRaw = 0x3000;
    private const ulong External2Raw = 0x3100;
    private const ulong ReacquiredRaw = 0x4000;
    private const ulong RestorePendingRaw = 0x5000;
    private const ulong PresetSwitchNewRaw = 0x6000;
    private const ulong LockedAppliedRaw = 0xA000;
    private const ulong LockedExternalRaw = 0xB000;

    internal static int Run(
        TextWriter output)
    {
        try
        {
            var ownership =
                new FakeOwnershipComparer();

            InitialWriteDidNotHappen(ownership, output);
            InitialWriteDidHappen(ownership, output);
            InitialWriteWasExternallyReplaced(ownership, output);
            OwnedSessionRestoresBaseline(ownership, output);
            StableReacquireRestoresExternalHandoff(ownership, output);
            ContestedNeverResumesReacquisition(ownership, output);
            ReacquireWriteArmedReleasesIfWriteHappened(ownership, output);
            ReacquireWriteArmedPreservesExternalIfWriteDidNotHappen(ownership, output);
            PresetSwitchOldRawIsReleased(ownership, output);
            PresetSwitchNewRawIsReleased(ownership, output);
            PresetSwitchExternalRawIsPreserved(ownership, output);
            StabilityDoesNotResumeAfterGuardianDeath(ownership, output);
            YieldedNeverReacquiresEvenIfRequestedValueIsPresent(ownership, output);
            RestoringAlreadyCompletedClears(ownership, output);
            RestoringStillOwnedAllowsOneReleaseRestore(ownership, output);
            UnresolvedUnlockedOwnedCanBeReleased(ownership, output);
            LockedOwnedRetainsJournal(ownership, output);
            LockedExternalIsPreservedAndCleared(ownership, output);

            output.WriteLine(
                "CPU power recovery planner self-test: PASS (recovery-only, no reacquire action, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "CPU power recovery planner self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void InitialWriteDidNotHappen(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.WriteArmed,
                external: null,
                conflict: Inactive(),
                pendingRaw: AppliedRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(BaselineRaw),
                ownership);

        Require(
            plan.Disposition ==
            CpuPowerRecoveryDisposition.ClearAlreadyReleased,
            "WriteArmed + baseline clears without write");

        Require(!plan.AllowsWrite, "baseline recovery cannot write");

        output.WriteLine(
            "PASS recovery clears initial WriteArmed when hardware stayed at baseline");
    }

    private static void InitialWriteDidHappen(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.WriteArmed,
                external: null,
                conflict: Inactive(),
                pendingRaw: AppliedRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(AppliedMetadataRaw),
                ownership);

        RequireRestore(
            plan,
            BaselineRaw,
            "WriteArmed + requested PL fields");

        output.WriteLine(
            "PASS recovery releases an initial write that completed before guardian death");
    }

    private static void InitialWriteWasExternallyReplaced(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.WriteArmed,
                external: null,
                conflict: Inactive(),
                pendingRaw: AppliedRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(ExternalRaw),
                ownership);

        RequirePreserve(
            plan,
            "external value after initial WriteArmed");

        output.WriteLine(
            "PASS recovery preserves external PL fields after ambiguous initial write");
    }

    private static void OwnedSessionRestoresBaseline(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Owned,
                external: null,
                conflict: Inactive());

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(AppliedRaw),
                ownership);

        RequireRestore(
            plan,
            BaselineRaw,
            "Owned requested PL fields");

        output.WriteLine(
            "PASS recovery releases a still-owned normal session to original baseline");
    }

    private static void StableReacquireRestoresExternalHandoff(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Owned,
                external: Snapshot(ExternalRaw),
                conflict: Inactive(),
                appliedRaw: ReacquiredRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(ReacquiredRaw),
                ownership);

        RequireRestore(
            plan,
            ExternalRaw,
            "Owned after stable bounded reacquire");

        output.WriteLine(
            "PASS recovery returns a stable reacquire to the captured external handoff");
    }

    private static void ContestedNeverResumesReacquisition(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Contested,
                external: Snapshot(ExternalRaw),
                conflict: Contested());

        foreach (var current in new[]
                 {
                     Snapshot(ExternalRaw),
                     Snapshot(AppliedRaw),
                     Snapshot(External2Raw)
                 })
        {
            var plan =
                CpuPowerRecoveryPlanner.Plan(
                    journal,
                    current,
                    ownership);

            RequirePreserve(
                plan,
                "Contested restart");

            Require(
                !plan.AllowsWrite,
                "Contested recovery has no reacquire write action");
        }

        output.WriteLine(
            "PASS restarted guardian never resumes a Contested retry budget");
    }

    private static void ReacquireWriteArmedReleasesIfWriteHappened(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.ReacquireWriteArmed,
                external: Snapshot(ExternalRaw),
                conflict: Contested(
                    attempts: 3,
                    inFlight: true),
                pendingRaw: ReacquiredRaw,
                appliedRaw: AppliedRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(ReacquiredRaw),
                ownership);

        RequireRestore(
            plan,
            ExternalRaw,
            "reacquire write completed");

        output.WriteLine(
            "PASS recovery releases a reacquire write that completed before crash");
    }

    private static void ReacquireWriteArmedPreservesExternalIfWriteDidNotHappen(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.ReacquireWriteArmed,
                external: Snapshot(ExternalRaw),
                conflict: Contested(
                    attempts: 3,
                    inFlight: true),
                pendingRaw: ReacquiredRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(ExternalRaw),
                ownership);

        Require(
            plan.Disposition ==
            CpuPowerRecoveryDisposition.ClearAlreadyReleased,
            "external handoff already present after armed reacquire");

        Require(
            !plan.AllowsWrite,
            "no write after reacquire did not take effect");

        output.WriteLine(
            "PASS armed reacquire that did not take effect is cleared without retry");
    }


    private static void PresetSwitchOldRawIsReleased(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.PresetSwitchWriteArmed,
                external: null,
                conflict: Inactive(),
                pendingRaw: PresetSwitchNewRaw,
                appliedRaw: AppliedRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(AppliedRaw),
                ownership);

        RequireRestore(
            plan,
            BaselineRaw,
            "preset switch old raw");

        output.WriteLine(
            "PASS recovery releases the old VFC preset after crash during source switch");
    }

    private static void PresetSwitchNewRawIsReleased(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.PresetSwitchWriteArmed,
                external: Snapshot(ExternalRaw),
                conflict: Inactive(),
                pendingRaw: PresetSwitchNewRaw,
                appliedRaw: AppliedRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(PresetSwitchNewRaw),
                ownership);

        RequireRestore(
            plan,
            ExternalRaw,
            "preset switch new raw");

        output.WriteLine(
            "PASS recovery releases the new VFC preset to ExternalHandoff after crash");
    }

    private static void PresetSwitchExternalRawIsPreserved(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.PresetSwitchWriteArmed,
                external: null,
                conflict: Inactive(),
                pendingRaw: PresetSwitchNewRaw,
                appliedRaw: AppliedRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(External2Raw),
                ownership);

        RequirePreserve(
            plan,
            "external raw during preset switch recovery");

        output.WriteLine(
            "PASS recovery preserves an external owner instead of finishing an old preset switch");
    }

    private static void StabilityDoesNotResumeAfterGuardianDeath(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Stability,
                external: Snapshot(ExternalRaw),
                conflict: Stability(
                    attempts: 2),
                appliedRaw: ReacquiredRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(ReacquiredRaw),
                ownership);

        RequireRestore(
            plan,
            ExternalRaw,
            "provisional reacquire");

        output.WriteLine(
            "PASS recovery releases provisional Stability instead of resuming its timer");
    }

    private static void YieldedNeverReacquiresEvenIfRequestedValueIsPresent(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Yielded,
                external: Snapshot(ExternalRaw),
                conflict: Yielded(),
                appliedRaw: ReacquiredRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(ReacquiredRaw),
                ownership);

        RequirePreserve(
            plan,
            "Yielded requested-equivalent current value");

        output.WriteLine(
            "PASS recovery never infers new ownership from a Yielded requested-equivalent value");
    }

    private static void RestoringAlreadyCompletedClears(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Restoring,
                external: Snapshot(ExternalRaw),
                conflict: Inactive(),
                pendingRaw: RestorePendingRaw,
                appliedRaw: ReacquiredRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(RestorePendingRaw),
                ownership);

        Require(
            plan.Disposition ==
            CpuPowerRecoveryDisposition.ClearAlreadyReleased,
            "completed restore clears");

        Require(!plan.AllowsWrite, "completed restore does not rewrite");

        output.WriteLine(
            "PASS recovery recognizes an already-completed restore");
    }

    private static void RestoringStillOwnedAllowsOneReleaseRestore(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Restoring,
                external: Snapshot(ExternalRaw),
                conflict: Inactive(),
                pendingRaw: RestorePendingRaw,
                appliedRaw: ReacquiredRaw);

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(ReacquiredRaw),
                ownership);

        RequireRestore(
            plan,
            ExternalRaw,
            "armed restore still sees requested PL fields");

        output.WriteLine(
            "PASS recovery may retry one release restore when an armed restore did not happen");
    }

    private static void UnresolvedUnlockedOwnedCanBeReleased(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Unresolved,
                external: null,
                conflict: Yielded());

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(AppliedRaw),
                ownership);

        RequireRestore(
            plan,
            BaselineRaw,
            "unresolved but unlocked requested PL fields");

        output.WriteLine(
            "PASS recovery can release an Unresolved session after the lock/obstacle disappears");
    }

    private static void LockedOwnedRetainsJournal(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Unresolved,
                external: null,
                conflict: Yielded());

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(LockedAppliedRaw),
                ownership);

        Require(
            plan.Disposition ==
            CpuPowerRecoveryDisposition.BlockedByLockRetainJournal,
            "locked requested value retains journal");

        Require(
            plan.RetainsJournal &&
            !plan.AllowsWrite,
            "locked requested value cannot write or clear");

        output.WriteLine(
            "PASS locked still-owned PL fields retain unresolved journal with zero write authority");
    }

    private static void LockedExternalIsPreservedAndCleared(
        ICpuPowerOwnershipComparer ownership,
        TextWriter output)
    {
        var journal =
            Record(
                CpuPowerJournalPhase.Owned,
                external: null,
                conflict: Inactive());

        var plan =
            CpuPowerRecoveryPlanner.Plan(
                journal,
                Snapshot(LockedExternalRaw),
                ownership);

        RequirePreserve(
            plan,
            "locked external PL fields");

        output.WriteLine(
            "PASS locked external owner is preserved; recovery does not retain stale VFC ownership");
    }

    private static void RequireRestore(
        CpuPowerRecoveryPlan plan,
        ulong expectedTargetRaw,
        string label)
    {
        Require(
            plan.Disposition ==
            CpuPowerRecoveryDisposition.RestoreOwnedThenClear,
            label + " should allow release-only restore");

        Require(
            plan.AllowsWrite,
            label + " should expose one recovery write");

        Require(
            plan.RestoreTarget?.Raw ==
            expectedTargetRaw,
            label + " restore target");
    }

    private static void RequirePreserve(
        CpuPowerRecoveryPlan plan,
        string label)
    {
        Require(
            plan.Disposition ==
            CpuPowerRecoveryDisposition.PreserveExternalAndClear,
            label + " should preserve current external value");

        Require(
            !plan.AllowsWrite &&
            plan.ClearsWithoutWrite,
            label + " should clear journal with zero writes");
    }

    private static CpuPowerSessionJournalRecord Record(
        CpuPowerJournalPhase phase,
        CpuPowerLimitSnapshot? external,
        CpuPowerConflictSnapshot conflict,
        ulong? pendingRaw = null,
        ulong appliedRaw = AppliedRaw)
    {
        var now =
            DateTimeOffset.UtcNow;

        return new CpuPowerSessionJournalRecord(
            CpuPowerSessionJournalRecord.CurrentSchemaVersion,
            TargetProfileId,
            Guid.NewGuid(),
            7,
            phase,
            Snapshot(BaselineRaw),
            new CpuPowerLimitRequest(
                20,
                40),
            appliedRaw,
            external,
            conflict,
            pendingRaw,
            now.AddMinutes(-1),
            now);
    }

    private static CpuPowerConflictSnapshot Inactive() =>
        new(
            CpuPowerConflictState.Inactive,
            0,
            CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
            false,
            null,
            null,
            0,
            0);

    private static CpuPowerConflictSnapshot Contested(
        int attempts = 0,
        bool inFlight = false) =>
        new(
            CpuPowerConflictState.Contested,
            attempts,
            CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
            inFlight,
            1000,
            2000,
            1000,
            0);

    private static CpuPowerConflictSnapshot Stability(
        int attempts) =>
        new(
            CpuPowerConflictState.ReacquiredPendingStability,
            attempts,
            CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
            false,
            1000,
            2000,
            1000,
            3000);

    private static CpuPowerConflictSnapshot Yielded() =>
        new(
            CpuPowerConflictState.Yielded,
            CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
            CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
            false,
            1000,
            5000,
            4000,
            0);

    private static CpuPowerLimitSnapshot Snapshot(
        ulong raw) =>
        raw switch
        {
            BaselineRaw =>
                new(
                    raw,
                    45,
                    115,
                    false),

            AppliedRaw or
            AppliedMetadataRaw or
            ReacquiredRaw =>
                new(
                    raw,
                    20,
                    40,
                    false),

            ExternalRaw or
            RestorePendingRaw =>
                new(
                    raw,
                    30,
                    60,
                    false),

            External2Raw =>
                new(
                    raw,
                    35,
                    70,
                    false),

            PresetSwitchNewRaw =>
                new(
                    raw,
                    15,
                    30,
                    false),

            LockedAppliedRaw =>
                new(
                    raw,
                    20,
                    40,
                    true),

            LockedExternalRaw =>
                new(
                    raw,
                    30,
                    60,
                    true),

            _ =>
                throw new InvalidOperationException(
                    $"Unknown fake raw 0x{raw:X}.")
        };

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeOwnershipComparer :
        ICpuPowerOwnershipComparer
    {
        public bool OwnedFieldsMatch(
            ulong expectedRaw,
            CpuPowerLimitSnapshot current)
        {
            var expected =
                Snapshot(expectedRaw);

            return expected.Pl1Watts ==
                       current.Pl1Watts &&
                   expected.Pl2Watts ==
                       current.Pl2Watts;
        }
    }
}
