using VictusFanControl.Runtime;

namespace VictusFanControl.Performance;

internal static class CpuPowerLimiterSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        TextWriter output)
    {
        try
        {
            Require(
                CpuPowerConflictPolicySelfTest.Run(output) == 0,
                "bounded external-writer conflict policy");

            Require(
                CpuPowerSessionJournalSelfTest.Run(output) == 0,
                "durable CPU power session journal");

            Require(
                CpuPowerRecoveryPlannerSelfTest.Run(output) == 0,
                "CPU power recovery-only planner");

            Require(
                CpuPowerRecoveryExecutorSelfTest.Run(output) == 0,
                "CPU power recovery executor");

            NormalApplyVerifyRelease(output);
            NonOwnedMutationIsPreservedWithoutConflict(output);
            SuccessfulReacquireRestoresExternalHandoff(output);
            EvolvingExternalValueBecomesLatestHandoff(output);
            FiveRejectedReacquiresYieldWithoutBaselineOverwrite(output);
            ExistingJournalBlocksNewApply(output);
            InitialJournalArmFailurePreventsWrite(output);
            OwnedJournalFailureLeavesWriteArmedAndNoSecondWrite(output);
            ReacquireJournalArmFailurePreventsWrite(output);
            RestoreJournalArmFailurePreventsRestoreWrite(output);
            LockWhileOwnedFailsWithoutAnotherWrite(output);
            InitialRejectedApplyRestoresBaseline(output);
            UnsupportedBackendNeverWrites(output);

            output.WriteLine(
                "CPU power limiter P2B journaled ownership self-test: PASS (no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "CPU power limiter P2B journaled ownership self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void NormalApplyVerifyRelease(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.State ==
            CpuPowerLimiterState.Disabled,
            "starts disabled");

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "apply 20/40");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Owned,
            "exact apply leaves durable Owned");

        Require(
            backend.WriteCount == 1,
            "single apply write");

        Require(
            limiter.VerifyActive(),
            "verify exact active raw");

        Require(
            backend.WriteCount == 1,
            "normal verify never rewrites");

        Require(
            limiter.Release(),
            "normal release");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Disabled,
            "disabled after release");

        Require(
            backend.WriteCount == 2,
            "one apply plus one restore");

        Require(
            backend.Raw ==
            FakeBackend.BaselineRaw,
            "original baseline restored");

        Require(
            journal.Current is null,
            "resolved release deletes durable journal");

        Require(
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.WriteArmed) &&
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.Owned) &&
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.Restoring),
            "apply and restore were durably armed");

        output.WriteLine(
            "PASS journal precedes normal apply and restore writes");
    }

    private static void NonOwnedMutationIsPreservedWithoutConflict(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "metadata fixture apply");

        backend.Raw =
            FakeBackend.AppliedMetadataRaw;

        Require(
            limiter.VerifyActive(),
            "non-owned mutation keeps requested power owned");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Active,
            "non-owned mutation stays Active");

        Require(
            limiter.AppliedRaw ==
            FakeBackend.AppliedMetadataRaw,
            "limiter adopts exact raw containing external metadata");

        Require(
            journal.Current?.AppliedRaw ==
            FakeBackend.AppliedMetadataRaw,
            "durable Owned tracks adopted raw");

        Require(
            backend.WriteCount == 1,
            "non-owned mutation causes no write");

        Require(
            limiter.Release(),
            "metadata fixture release");

        output.WriteLine(
            "PASS non-owned changes are adopted durably without false contention");
    }

    private static void SuccessfulReacquireRestoresExternalHandoff(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "reacquire fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "external power change detected");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Contested,
            "external change enters Contested");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Contested,
            "conflict is durable before any retry");

        Require(
            backend.WriteCount == 1,
            "no immediate write on conflict");

        clock.Advance(
            TimeSpan.FromSeconds(30));

        Require(
            limiter.VerifyActive(),
            "30 s allows bounded reacquire");

        Require(
            limiter.State ==
            CpuPowerLimiterState.ReacquiredPendingStability,
            "exact reacquire is provisional");

        Require(
            limiter.ReacquireAttemptsUsed == 1,
            "attempt 1 is recorded");

        Require(
            backend.Raw ==
            FakeBackend.ReacquiredRaw,
            "reacquire raw is applied");

        Require(
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.ReacquireWriteArmed),
            "reacquire write was durably armed");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Stability,
            "exact readback transitions durable journal to Stability");

        clock.Advance(
            TimeSpan.FromSeconds(60));

        Require(
            limiter.VerifyActive(),
            "60 s closes stability");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Active,
            "stable reacquire returns Active");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Owned,
            "stable reacquire returns durable journal to Owned");

        Require(
            limiter.Release(),
            "release after successful reacquire");

        Require(
            backend.Raw ==
            FakeBackend.ExternalRaw,
            "release returns control to external handoff");

        Require(
            journal.Current is null,
            "handoff release deletes journal after exact readback");

        output.WriteLine(
            "PASS reacquire is armed durably and later restores external handoff");
    }

    private static void EvolvingExternalValueBecomesLatestHandoff(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "evolving fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "first external value detected");

        clock.Advance(
            TimeSpan.FromSeconds(20));

        backend.Raw =
            FakeBackend.External2Raw;

        Require(
            !limiter.VerifyActive(),
            "second external value observed");

        Require(
            journal.Current?.ExternalHandoff?.Raw ==
            FakeBackend.External2Raw,
            "latest external handoff is persisted");

        clock.Advance(
            TimeSpan.FromSeconds(10));

        Require(
            limiter.VerifyActive(),
            "original 30 s deadline still permits attempt");

        Require(
            backend.Raw ==
            FakeBackend.Reacquired2Raw,
            "reacquire plan uses latest external snapshot");

        Require(
            limiter.Release(),
            "release evolving fixture");

        Require(
            backend.Raw ==
            FakeBackend.External2Raw,
            "latest external value is restored");

        output.WriteLine(
            "PASS evolving external handoff is durable without postponing retry");
    }

    private static void FiveRejectedReacquiresYieldWithoutBaselineOverwrite(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend =
            new FakeBackend(journal)
            {
                RejectReacquire = true
            };

        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "yield fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "yield fixture conflict detected");

        for (var attempt = 1;
             attempt <= 5;
             attempt++)
        {
            clock.Advance(
                TimeSpan.FromSeconds(30));

            Require(
                !limiter.VerifyActive(),
                $"reacquire attempt {attempt} rejected");

            Require(
                limiter.ReacquireAttemptsUsed ==
                attempt,
                $"attempt {attempt} counted");
        }

        Require(
            limiter.State ==
            CpuPowerLimiterState.Yielded,
            "fifth rejection yields");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Yielded,
            "Yielded state is durable");

        Require(
            backend.WriteCount == 6,
            "initial apply plus exactly five reacquire writes");

        clock.Advance(
            TimeSpan.FromMinutes(10));

        Require(
            !limiter.VerifyActive(),
            "Yielded remains read-only");

        Require(
            backend.WriteCount == 6,
            "there is no automatic sixth write");

        Require(
            limiter.Release(),
            "yielded session releases");

        Require(
            backend.Raw ==
            FakeBackend.ExternalRaw,
            "yield release preserves external owner");

        Require(
            backend.WriteCount == 6,
            "yield release performs no stale baseline write");

        output.WriteLine(
            "PASS exactly five durable retries then Yielded with no sixth write");
    }

    private static void ExistingJournalBlocksNewApply(
        TextWriter output)
    {
        var journal = new FakeJournal();
        journal.Seed(
            MakeWriteArmedRecord());

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "unresolved journal blocks new Apply");

        Require(
            backend.WriteCount == 0,
            "unresolved journal causes zero writes");

        Require(
            limiter.LastError?.Contains(
                "UNRESOLVED_JOURNAL",
                StringComparison.Ordinal) == true,
            "unresolved journal is explicit");

        output.WriteLine(
            "PASS unresolved durable session blocks a new authority episode");
    }

    private static void InitialJournalArmFailurePreventsWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 1
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "write-arm journal failure rejects Apply");

        Require(
            backend.WriteCount == 0,
            "journal failure before Apply produces zero writes");

        Require(
            backend.Raw ==
            FakeBackend.BaselineRaw,
            "baseline remains unchanged");

        output.WriteLine(
            "PASS failed WriteArmed persistence blocks the hardware write");
    }

    private static void OwnedJournalFailureLeavesWriteArmedAndNoSecondWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 2
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "Owned journal failure is surfaced");

        Require(
            backend.WriteCount == 1,
            "initial write happened only after WriteArmed");

        Require(
            backend.Raw ==
            FakeBackend.AppliedRaw,
            "hardware result remains observable");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.WriteArmed,
            "last durable state remains conservative WriteArmed");

        limiter.Dispose();

        Require(
            backend.WriteCount == 1,
            "Dispose cannot issue restore when Restoring cannot be persisted");

        output.WriteLine(
            "PASS post-write journal failure leaves WriteArmed and forbids an unjournaled restore");
    }

    private static void ReacquireJournalArmFailurePreventsWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 4
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "reacquire-arm fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "conflict journal succeeds");

        clock.Advance(
            TimeSpan.FromSeconds(30));

        Require(
            !limiter.VerifyActive(),
            "reacquire arm failure is surfaced");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Failed,
            "lost durability fails closed");

        Require(
            backend.WriteCount == 1,
            "failed ReacquireWriteArmed persistence causes no retry write");

        Require(
            backend.Raw ==
            FakeBackend.ExternalRaw,
            "external owner is preserved");

        limiter.Dispose();

        Require(
            backend.WriteCount == 1,
            "Dispose also cannot write while journal store remains unavailable");

        output.WriteLine(
            "PASS ReacquireWriteArmed failure prevents the reacquisition write");
    }

    private static void RestoreJournalArmFailurePreventsRestoreWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 3
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "restore-arm fixture apply");

        Require(
            !limiter.Release(),
            "restore journal failure is surfaced");

        Require(
            backend.WriteCount == 1,
            "Restoring must persist before restore write");

        Require(
            backend.Raw ==
            FakeBackend.AppliedRaw,
            "unarmed restore leaves current hardware untouched");

        limiter.Dispose();

        Require(
            backend.WriteCount == 1,
            "Dispose never bypasses failed Restoring persistence");

        output.WriteLine(
            "PASS failed Restoring persistence blocks the restore write");
    }

    private static void LockWhileOwnedFailsWithoutAnotherWrite(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "lock fixture apply");

        backend.Raw =
            FakeBackend.LockedAppliedRaw;

        Require(
            !limiter.VerifyActive(),
            "lock appearance detected");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Failed,
            "lock while owned is unresolved Failed");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Unresolved,
            "lock while owned is durable Unresolved");

        Require(
            backend.WriteCount == 1,
            "lock path never writes");

        Require(
            !limiter.Release(),
            "locked owned value cannot be falsely released");

        Require(
            backend.WriteCount == 1,
            "release does not clear lock or rewrite RAPL");

        output.WriteLine(
            "PASS lock while owned becomes durable Unresolved with no bypass write");
    }

    private static void InitialRejectedApplyRestoresBaseline(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend =
            new FakeBackend(journal)
            {
                RejectApply = true
            };

        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "rejected readback returns false");

        Require(
            backend.Raw ==
            FakeBackend.BaselineRaw,
            "rejected apply baseline retained");

        Require(
            backend.WriteCount == 1,
            "rejected hardware saw only attempted apply");

        Require(
            journal.Current is null,
            "no unresolved journal remains when baseline is confirmed");

        output.WriteLine(
            "PASS rejected initial write resolves journal when baseline remains exact");
    }

    private static void UnsupportedBackendNeverWrites(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend =
            new FakeBackend(journal)
            {
                IsSupported = false
            };

        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.State ==
            CpuPowerLimiterState.Unsupported,
            "unsupported state");

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "unsupported apply denied");

        Require(
            backend.WriteCount == 0,
            "unsupported path never writes");

        Require(
            journal.StoreAttempts == 0,
            "unsupported path never arms a journal");

        output.WriteLine(
            "PASS unsupported backend remains fully write-closed");
    }

    private static CpuPowerSessionJournalRecord
        MakeWriteArmedRecord()
    {
        var now = DateTimeOffset.UtcNow;

        return new CpuPowerSessionJournalRecord(
            CpuPowerSessionJournalRecord.CurrentSchemaVersion,
            TargetProfileId,
            Guid.NewGuid(),
            1,
            CpuPowerJournalPhase.WriteArmed,
            new CpuPowerLimitSnapshot(
                FakeBackend.BaselineRaw,
                45,
                115,
                false),
            new CpuPowerLimitRequest(
                20,
                40),
            FakeBackend.AppliedRaw,
            null,
            new CpuPowerConflictSnapshot(
                CpuPowerConflictState.Inactive,
                0,
                CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
                false,
                null,
                null,
                0,
                0),
            FakeBackend.AppliedRaw,
            now,
            now);
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeActiveTimeClock :
        IActiveTimeClock
    {
        public ulong Milliseconds { get; private set; }

        internal void Advance(
            TimeSpan duration)
        {
            Milliseconds =
                checked(
                    Milliseconds +
                    ActiveTimeClock.TimeoutMilliseconds(
                        duration));
        }
    }

    private sealed class FakeJournal :
        ICpuPowerSessionJournal
    {
        internal CpuPowerSessionJournalRecord? Current;
        internal int StoreAttempts;
        internal int DeleteAttempts;
        internal int? FailStoreFromAttempt;
        internal readonly List<CpuPowerJournalPhase>
            StoredPhases = new();

        public string Path =>
            "fake://cpu-power-session";

        public string TargetProfileId =>
            CpuPowerLimiterSelfTest.TargetProfileId;

        public CpuPowerSessionJournalRecord? Load() =>
            Current;

        public void Store(
            CpuPowerSessionJournalRecord record)
        {
            StoreAttempts++;

            if (FailStoreFromAttempt.HasValue &&
                StoreAttempts >=
                FailStoreFromAttempt.Value)
            {
                throw new IOException(
                    "simulated durable journal failure");
            }

            Current = record;
            StoredPhases.Add(
                record.Phase);
        }

        public void Delete()
        {
            DeleteAttempts++;
            Current = null;
        }

        internal void Seed(
            CpuPowerSessionJournalRecord record)
        {
            Current = record;
        }
    }

    private sealed class FakeBackend :
        ICpuPowerLimitBackend
    {
        internal const ulong BaselineRaw = 0x1000;
        internal const ulong AppliedRaw = 0x2000;
        internal const ulong AppliedMetadataRaw = 0x2100;
        internal const ulong ExternalRaw = 0x3000;
        internal const ulong External2Raw = 0x3100;
        internal const ulong ReacquiredRaw = 0x4000;
        internal const ulong Reacquired2Raw = 0x4100;
        internal const ulong LockedAppliedRaw = 0xA000;

        private readonly FakeJournal _journal;

        internal FakeBackend(
            FakeJournal journal)
        {
            _journal = journal;
        }

        internal ulong Raw = BaselineRaw;
        internal int WriteCount;
        internal bool RejectApply;
        internal bool RejectReacquire;

        public bool IsSupported { get; set; } = true;

        public CpuPowerLimitSnapshot Read() =>
            Snapshot(Raw);

        public CpuPowerLimitApplyPlan BuildApplyPlan(
            CpuPowerLimitSnapshot baseline,
            CpuPowerLimitRequest request)
        {
            if (baseline.Raw != BaselineRaw ||
                request.Pl1Watts != 20 ||
                request.Pl2Watts != 40)
            {
                throw new InvalidOperationException(
                    "unexpected fake initial plan request");
            }

            return new CpuPowerLimitApplyPlan(
                AppliedRaw,
                20,
                40);
        }

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
            CpuPowerLimitSnapshot current)
        {
            if (originalBaseline.Raw !=
                    BaselineRaw ||
                request.Pl1Watts != 20 ||
                request.Pl2Watts != 40 ||
                current.Locked)
            {
                throw new InvalidOperationException(
                    "unexpected fake reacquire plan request");
            }

            var raw =
                current.Raw ==
                External2Raw
                    ? Reacquired2Raw
                    : ReacquiredRaw;

            return new CpuPowerLimitApplyPlan(
                raw,
                20,
                40);
        }

        public CpuPowerLimitRestorePlan PlanRestore(
            CpuPowerLimitSnapshot restoreTarget,
            ulong appliedRaw,
            CpuPowerLimitSnapshot current)
        {
            if (current.Raw ==
                restoreTarget.Raw)
            {
                return new CpuPowerLimitRestorePlan(
                    current.Raw,
                    "ALREADY_TARGET");
            }

            if (current.Locked)
            {
                return new CpuPowerLimitRestorePlan(
                    current.Raw,
                    "RESTORE_BLOCKED_LOCK");
            }

            if (OwnedFieldsMatch(
                    appliedRaw,
                    current))
            {
                return new CpuPowerLimitRestorePlan(
                    restoreTarget.Raw,
                    "RESTORE_PLANNED");
            }

            return new CpuPowerLimitRestorePlan(
                current.Raw,
                "EXTERNAL_CHANGE_PRESERVED");
        }

        public void Write(
            ulong raw)
        {
            var expectedPhase =
                raw switch
                {
                    AppliedRaw =>
                        CpuPowerJournalPhase.WriteArmed,

                    ReacquiredRaw or
                    Reacquired2Raw =>
                        CpuPowerJournalPhase.ReacquireWriteArmed,

                    _ =>
                        CpuPowerJournalPhase.Restoring
                };

            if (_journal.Current?.Phase !=
                expectedPhase)
            {
                throw new InvalidOperationException(
                    $"hardware write 0x{raw:X} occurred without durable {expectedPhase}");
            }

            if (_journal.Current?.PendingRaw !=
                raw)
            {
                throw new InvalidOperationException(
                    $"hardware write 0x{raw:X} does not match durable PendingRaw");
            }

            WriteCount++;

            if (RejectApply &&
                raw == AppliedRaw)
            {
                return;
            }

            if (RejectReacquire &&
                raw is
                    ReacquiredRaw or
                    Reacquired2Raw)
            {
                return;
            }

            Raw = raw;
        }

        private static CpuPowerLimitSnapshot
            Snapshot(
                ulong raw) =>
            raw switch
            {
                BaselineRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        45,
                        115,
                        false),

                AppliedRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                AppliedMetadataRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                ExternalRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        30,
                        60,
                        false),

                External2Raw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        35,
                        70,
                        false),

                ReacquiredRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                Reacquired2Raw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                LockedAppliedRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        true),

                _ =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        30,
                        60,
                        false)
            };
    }
}
