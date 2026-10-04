namespace VictusFanControl.Performance;

internal static class CpuPowerRecoveryExecutorSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private const ulong BaselineRaw = 0x1000;
    private const ulong AppliedRaw = 0x2000;
    private const ulong ExternalRaw = 0x3000;
    private const ulong External2Raw = 0x3100;
    private const ulong ReacquiredRaw = 0x4000;
    private const ulong LockedAppliedRaw = 0xA000;

    internal static int Run(
        TextWriter output)
    {
        try
        {
            NoJournalDoesNothing(output);
            AlreadyReleasedClearsWithoutWrite(output);
            ExternalOwnerIsPreserved(output);
            OwnedValueIsRestoredOnce(output);
            ContestedNeverReacquires(output);
            RestoringStoreFailurePreventsWrite(output);
            ConcurrentExternalChangePreventsWriteAndClears(output);
            ConcurrentOwnedMetadataChangeDefersWithoutWrite(output);
            WriteErrorWithAppliedRestoreStillClears(output);
            PostWriteExternalWinnerIsPreservedWithoutSecondWrite(output);
            LockedOwnedValueRetainsJournal(output);
            DeleteFailureNeverHidesRetainedJournal(output);

            output.WriteLine(
                "CPU power recovery executor self-test: PASS (journal-before-restore, compare-read, max one release write, no reacquire).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "CPU power recovery executor self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void NoJournalDoesNothing(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.NoJournal,
            "no journal disposition");

        Require(
            backend.WriteCount == 0 &&
            backend.ReadCount == 0,
            "no journal performs zero hardware I/O");

        output.WriteLine(
            "PASS recovery executor is idle when no journal exists");
    }

    private static void AlreadyReleasedClearsWithoutWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.WriteArmed,
                    external: null,
                    conflict: Inactive(),
                    pendingRaw: AppliedRaw));

        var backend =
            new FakeBackend(journal)
            {
                Raw = BaselineRaw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.ClearedAlreadyReleased,
            "already baseline clears");

        Require(
            backend.WriteCount == 0 &&
            journal.Current is null,
            "already baseline needs zero writes and deletes journal");

        output.WriteLine(
            "PASS already-released recovery clears journal with zero writes");
    }

    private static void ExternalOwnerIsPreserved(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Owned,
                    external: null,
                    conflict: Inactive()));

        var backend =
            new FakeBackend(journal)
            {
                Raw = ExternalRaw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.ClearedExternalPreserved,
            "external owner disposition");

        Require(
            backend.Raw == ExternalRaw &&
            backend.WriteCount == 0 &&
            journal.Current is null,
            "external owner is never overwritten");

        output.WriteLine(
            "PASS recovery preserves external owner and clears stale VFC journal");
    }

    private static void OwnedValueIsRestoredOnce(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Owned,
                    external: null,
                    conflict: Inactive()));

        var backend =
            new FakeBackend(journal)
            {
                Raw = AppliedRaw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.RestoredAndCleared,
            "owned recovery restored");

        Require(
            result.WriteAttempted &&
            backend.WriteCount == 1,
            "owned recovery performs exactly one write");

        Require(
            backend.Raw == BaselineRaw,
            "owned recovery restores baseline");

        Require(
            backend.PhaseSeenAtWrite ==
            CpuPowerJournalPhase.Restoring,
            "Restoring was durable at write time");

        Require(
            backend.PendingSeenAtWrite ==
            BaselineRaw,
            "durable PendingRaw equals recovery write");

        Require(
            journal.Current is null,
            "exact readback deletes resolved journal");

        output.WriteLine(
            "PASS still-owned value gets one journaled release restore");
    }

    private static void ContestedNeverReacquires(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Contested,
                    external: Snapshot(ExternalRaw),
                    conflict: Contested(
                        attempts: 3)));

        var backend =
            new FakeBackend(journal)
            {
                Raw = AppliedRaw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.ClearedExternalPreserved,
            "Contested restart preserves current state");

        Require(
            backend.WriteCount == 0,
            "Contested restart never writes requested value");

        Require(
            journal.Current is null,
            "Contested recovery session closes instead of resuming 3/5");

        output.WriteLine(
            "PASS executor has no path to resume a Contested reacquisition episode");
    }

    private static void RestoringStoreFailurePreventsWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Owned,
                    external: null,
                    conflict: Inactive()))
            {
                FailStore = true
            };

        var backend =
            new FakeBackend(journal)
            {
                Raw = AppliedRaw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.UnresolvedJournalRetained,
            "Restoring store failure remains unresolved");

        Require(
            backend.WriteCount == 0,
            "failed durable Restoring produces zero writes");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Owned,
            "previous durable journal remains");

        output.WriteLine(
            "PASS recovery never writes when Restoring cannot be persisted");
    }

    private static void ConcurrentExternalChangePreventsWriteAndClears(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Owned,
                    external: null,
                    conflict: Inactive()));

        var backend =
            new FakeBackend(journal)
            {
                Raw = AppliedRaw,
                ChangeBeforeCompareReadTo = External2Raw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.ClearedExternalPreserved,
            "concurrent external writer wins");

        Require(
            backend.WriteCount == 0,
            "compare-read change prevents restore write");

        Require(
            backend.Raw == External2Raw &&
            journal.Current is null,
            "new external owner is preserved and stale journal cleared");

        output.WriteLine(
            "PASS compare-read detects external takeover and performs zero writes");
    }

    private static void ConcurrentOwnedMetadataChangeDefersWithoutWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Owned,
                    external: null,
                    conflict: Inactive()));

        var backend =
            new FakeBackend(journal)
            {
                Raw = AppliedRaw,
                ChangeBeforeCompareReadTo = 0x2100
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.DeferredConcurrentHardwareChange,
            "owned metadata race is deferred");

        Require(
            backend.WriteCount == 0,
            "owned metadata race performs zero writes");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Restoring,
            "durable Restoring is retained for later inspection");

        output.WriteLine(
            "PASS hardware race while ownership still matches is deferred without write");
    }

    private static void WriteErrorWithAppliedRestoreStillClears(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Owned,
                    external: null,
                    conflict: Inactive()));

        var backend =
            new FakeBackend(journal)
            {
                Raw = AppliedRaw,
                ThrowAfterApplyingWrite = true
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.RestoredAndCleared,
            "write exception with observed restore is resolved");

        Require(
            backend.WriteCount == 1 &&
            backend.Raw == BaselineRaw,
            "write happened exactly once despite exception");

        Require(
            journal.Current is null,
            "observed release target clears journal");

        output.WriteLine(
            "PASS write exception is resolved by readback without a second write");
    }

    private static void PostWriteExternalWinnerIsPreservedWithoutSecondWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Owned,
                    external: null,
                    conflict: Inactive()));

        var backend =
            new FakeBackend(journal)
            {
                Raw = AppliedRaw,
                ChangeAfterWriteTo = External2Raw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.ClearedExternalPreserved,
            "post-write external winner is preserved");

        Require(
            backend.WriteCount == 1,
            "executor never issues a second recovery write");

        Require(
            backend.Raw == External2Raw &&
            journal.Current is null,
            "post-write external state survives");

        output.WriteLine(
            "PASS external takeover after recovery write is preserved with no second write");
    }

    private static void LockedOwnedValueRetainsJournal(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.Unresolved,
                    external: null,
                    conflict: Yielded()));

        var backend =
            new FakeBackend(journal)
            {
                Raw = LockedAppliedRaw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.BlockedByLock,
            "locked still-owned state is blocked");

        Require(
            backend.WriteCount == 0 &&
            journal.Current is not null,
            "locked owned state writes nothing and retains journal");

        output.WriteLine(
            "PASS lock blocks recovery write and retains unresolved evidence");
    }

    private static void DeleteFailureNeverHidesRetainedJournal(
        TextWriter output)
    {
        var journal =
            new FakeJournal(
                Record(
                    CpuPowerJournalPhase.WriteArmed,
                    external: null,
                    conflict: Inactive(),
                    pendingRaw: AppliedRaw))
            {
                FailDelete = true
            };

        var backend =
            new FakeBackend(journal)
            {
                Raw = BaselineRaw
            };

        var result =
            new CpuPowerRecoveryExecutor(
                backend,
                journal)
            .Execute();

        Require(
            result.Disposition ==
            CpuPowerRecoveryExecutionDisposition.ResolvedJournalRetained,
            "delete failure is explicit");

        Require(
            result.JournalRetained &&
            journal.Current is not null,
            "resolved hardware does not imply journal disappearance");

        Require(
            backend.WriteCount == 0,
            "delete failure never causes a write");

        output.WriteLine(
            "PASS resolved hardware plus delete failure remains explicitly journal-retained");
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
            9,
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
        int attempts) =>
        new(
            CpuPowerConflictState.Contested,
            attempts,
            CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
            false,
            1000,
            2000,
            1000,
            0);

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

            AppliedRaw or 0x2100 or ReacquiredRaw =>
                new(
                    raw,
                    20,
                    40,
                    false),

            ExternalRaw =>
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

            LockedAppliedRaw =>
                new(
                    raw,
                    20,
                    40,
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

    private sealed class FakeJournal :
        ICpuPowerSessionJournal
    {
        internal CpuPowerSessionJournalRecord? Current;
        internal bool FailStore;
        internal bool FailDelete;

        internal FakeJournal(
            CpuPowerSessionJournalRecord? initial = null)
        {
            Current = initial;
        }

        public string Path =>
            "fake://cpu-power-recovery";

        public string TargetProfileId =>
            CpuPowerRecoveryExecutorSelfTest.TargetProfileId;

        public CpuPowerSessionJournalRecord? Load() =>
            Current;

        public void Store(
            CpuPowerSessionJournalRecord record)
        {
            if (FailStore)
            {
                throw new IOException(
                    "simulated recovery journal store failure");
            }

            Current = record;
        }

        public void Delete()
        {
            if (FailDelete)
            {
                throw new IOException(
                    "simulated recovery journal delete failure");
            }

            Current = null;
        }
    }

    private sealed class FakeBackend :
        ICpuPowerLimitBackend
    {
        private readonly FakeJournal _journal;
        private int _readOrdinal;

        internal FakeBackend(
            FakeJournal journal)
        {
            _journal = journal;
        }

        internal ulong Raw = BaselineRaw;
        internal int ReadCount;
        internal int WriteCount;
        internal ulong? ChangeBeforeCompareReadTo;
        internal ulong? ChangeAfterWriteTo;
        internal bool ThrowAfterApplyingWrite;
        internal CpuPowerJournalPhase? PhaseSeenAtWrite;
        internal ulong? PendingSeenAtWrite;

        public bool IsSupported => true;

        public CpuPowerLimitSnapshot Read()
        {
            ReadCount++;
            _readOrdinal++;

            if (_readOrdinal == 2 &&
                ChangeBeforeCompareReadTo.HasValue)
            {
                Raw =
                    ChangeBeforeCompareReadTo.Value;
            }

            return Snapshot(Raw);
        }

        public CpuPowerLimitApplyPlan BuildApplyPlan(
            CpuPowerLimitSnapshot baseline,
            CpuPowerLimitRequest request) =>
            throw new InvalidOperationException(
                "recovery executor must never build an Apply plan");

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

        public CpuPowerLimitApplyPlan BuildReacquirePlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current) =>
            throw new InvalidOperationException(
                "recovery executor must never build a reacquire plan");

        public CpuPowerLimitApplyPlan BuildOwnedTransitionPlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current) =>
            throw new InvalidOperationException(
                "recovery executor must never build an owned preset transition plan");

        public CpuPowerLimitRestorePlan PlanRestore(
            CpuPowerLimitSnapshot restoreTarget,
            ulong appliedRaw,
            CpuPowerLimitSnapshot current)
        {
            if (!OwnedFieldsMatch(
                    appliedRaw,
                    current))
            {
                return new CpuPowerLimitRestorePlan(
                    current.Raw,
                    "EXTERNAL_OWNER");
            }

            return new CpuPowerLimitRestorePlan(
                restoreTarget.Raw,
                "RECOVERY_RELEASE");
        }

        public void Write(
            ulong raw)
        {
            PhaseSeenAtWrite =
                _journal.Current?.Phase;

            PendingSeenAtWrite =
                _journal.Current?.PendingRaw;

            if (PhaseSeenAtWrite !=
                CpuPowerJournalPhase.Restoring)
            {
                throw new InvalidOperationException(
                    "recovery write occurred without durable Restoring phase");
            }

            if (PendingSeenAtWrite !=
                raw)
            {
                throw new InvalidOperationException(
                    "recovery write does not match durable PendingRaw");
            }

            if (raw is
                AppliedRaw or
                ReacquiredRaw)
            {
                throw new InvalidOperationException(
                    "recovery executor attempted a forbidden apply/reacquire write");
            }

            WriteCount++;
            Raw = raw;

            if (ChangeAfterWriteTo.HasValue)
            {
                Raw =
                    ChangeAfterWriteTo.Value;
            }

            if (ThrowAfterApplyingWrite)
            {
                throw new IOException(
                    "simulated write error after mutation");
            }
        }
    }
}
