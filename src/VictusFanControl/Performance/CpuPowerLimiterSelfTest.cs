using VictusFanControl.Runtime;

namespace VictusFanControl.Performance;

internal static class CpuPowerLimiterSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            Require(
                CpuPowerConflictPolicySelfTest.Run(output) == 0,
                "bounded external-writer conflict policy");

            Require(
                CpuPowerSessionJournalSelfTest.Run(output) == 0,
                "durable CPU power session journal");

            NormalApplyVerifyRelease(output);
            NonOwnedMutationIsPreservedWithoutConflict(output);
            SuccessfulReacquireRestoresExternalHandoff(output);
            EvolvingExternalValueBecomesLatestHandoff(output);
            FiveRejectedReacquiresYieldWithoutBaselineOverwrite(output);
            LockWhileOwnedFailsWithoutAnotherWrite(output);
            InitialRejectedApplyRestoresBaseline(output);
            UnsupportedBackendNeverWrites(output);

            output.WriteLine("CPU power limiter P2B ownership self-test: PASS (no hardware I/O).");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine("CPU power limiter P2B ownership self-test: FAIL - " + ex.Message);
            return 1;
        }
    }

    private static void NormalApplyVerifyRelease(TextWriter output)
    {
        var backend = new FakeBackend();
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(limiter.State == CpuPowerLimiterState.Disabled, "starts disabled");
        Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "apply 20/40");
        Require(limiter.State == CpuPowerLimiterState.Active, "active after exact readback");
        Require(backend.WriteCount == 1, "single apply write");
        Require(limiter.VerifyActive(), "verify exact active raw");
        Require(backend.WriteCount == 1, "normal verify never rewrites");
        Require(limiter.Release(), "normal release");
        Require(limiter.State == CpuPowerLimiterState.Disabled, "disabled after release");
        Require(backend.WriteCount == 2, "one apply plus one restore");
        Require(backend.Raw == FakeBackend.BaselineRaw, "original baseline restored");

        output.WriteLine("PASS normal CPU limiter apply/verify/release remains single-shot");
    }

    private static void NonOwnedMutationIsPreservedWithoutConflict(TextWriter output)
    {
        var backend = new FakeBackend();
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "metadata fixture apply");

        backend.Raw = FakeBackend.AppliedMetadataRaw;
        Require(limiter.VerifyActive(), "non-owned mutation keeps requested power owned");
        Require(limiter.State == CpuPowerLimiterState.Active, "non-owned mutation stays Active");
        Require(limiter.AppliedRaw == FakeBackend.AppliedMetadataRaw,
            "limiter adopts exact raw containing external non-owned fields");
        Require(backend.WriteCount == 1, "non-owned mutation causes no write");
        Require(limiter.ExternalHandoff is null, "non-owned mutation is not treated as handoff");

        Require(limiter.Release(), "metadata fixture release");
        Require(backend.Raw == FakeBackend.BaselineRaw, "metadata fixture baseline restored");

        output.WriteLine("PASS non-owned raw changes do not trigger a PL1/PL2 conflict");
    }

    private static void SuccessfulReacquireRestoresExternalHandoff(TextWriter output)
    {
        var backend = new FakeBackend();
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "reacquire fixture apply");

        backend.Raw = FakeBackend.ExternalRaw;
        Require(!limiter.VerifyActive(), "external power change detected");
        Require(limiter.State == CpuPowerLimiterState.Contested, "external change enters Contested");
        Require(limiter.ExternalHandoff?.Raw == FakeBackend.ExternalRaw,
            "first external raw becomes handoff candidate");
        Require(backend.WriteCount == 1, "no immediate write on conflict");

        clock.Advance(TimeSpan.FromSeconds(29));
        Require(!limiter.VerifyActive(), "29 s still contested");
        Require(backend.WriteCount == 1, "29 s performs no write");

        clock.Advance(TimeSpan.FromSeconds(1));
        Require(limiter.VerifyActive(), "30 s allows bounded reacquire");
        Require(limiter.State == CpuPowerLimiterState.ReacquiredPendingStability,
            "exact reacquire is provisional");
        Require(limiter.ReacquireAttemptsUsed == 1, "attempt 1 is recorded");
        Require(backend.Raw == FakeBackend.ReacquiredRaw, "reacquire raw is applied");
        Require(backend.WriteCount == 2, "one initial plus one reacquire write");

        clock.Advance(TimeSpan.FromSeconds(59));
        Require(limiter.VerifyActive(), "requested power remains present during stability");
        Require(limiter.State == CpuPowerLimiterState.ReacquiredPendingStability,
            "59 s does not close stability");

        clock.Advance(TimeSpan.FromSeconds(1));
        Require(limiter.VerifyActive(), "60 s closes stability");
        Require(limiter.State == CpuPowerLimiterState.Active, "stable reacquire returns Active");
        Require(limiter.ReacquireAttemptsUsed == 0, "stable episode resets retry budget");
        Require(limiter.ExternalHandoff?.Raw == FakeBackend.ExternalRaw,
            "external handoff survives stability-budget reset");

        Require(limiter.Release(), "release after successful reacquire");
        Require(backend.Raw == FakeBackend.ExternalRaw,
            "release returns control to external handoff, not stale original baseline");
        Require(backend.WriteCount == 3,
            "release after reacquire performs exactly one handoff restore");

        output.WriteLine("PASS successful reacquire later restores the captured external handoff");
    }

    private static void EvolvingExternalValueBecomesLatestHandoff(TextWriter output)
    {
        var backend = new FakeBackend();
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "evolving fixture apply");

        backend.Raw = FakeBackend.ExternalRaw;
        Require(!limiter.VerifyActive(), "first external value detected");

        clock.Advance(TimeSpan.FromSeconds(20));
        backend.Raw = FakeBackend.External2Raw;
        Require(!limiter.VerifyActive(), "second external value observed");
        Require(limiter.ExternalHandoff?.Raw == FakeBackend.External2Raw,
            "latest external value replaces handoff candidate");

        clock.Advance(TimeSpan.FromSeconds(10));
        Require(limiter.VerifyActive(), "original 30 s deadline still permits attempt");
        Require(backend.Raw == FakeBackend.Reacquired2Raw,
            "reacquire plan is based on latest external snapshot");

        Require(limiter.Release(), "release evolving fixture");
        Require(backend.Raw == FakeBackend.External2Raw,
            "latest external value is restored on handoff");

        output.WriteLine("PASS evolving external ownership updates the handoff without postponing retry");
    }

    private static void FiveRejectedReacquiresYieldWithoutBaselineOverwrite(TextWriter output)
    {
        var backend = new FakeBackend { RejectReacquire = true };
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "yield fixture apply");

        backend.Raw = FakeBackend.ExternalRaw;
        Require(!limiter.VerifyActive(), "yield fixture conflict detected");

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Require(!limiter.VerifyActive(), $"reacquire attempt {attempt} rejected");
            Require(limiter.ReacquireAttemptsUsed == attempt, $"attempt {attempt} counted");

            var expected = attempt == 5
                ? CpuPowerLimiterState.Yielded
                : CpuPowerLimiterState.Contested;
            Require(limiter.State == expected, $"attempt {attempt} limiter state");
            Require(backend.Raw == FakeBackend.ExternalRaw,
                $"external raw survives rejected attempt {attempt}");
        }

        Require(backend.WriteCount == 6,
            "initial apply plus exactly five bounded reacquire writes");

        clock.Advance(TimeSpan.FromMinutes(10));
        Require(!limiter.VerifyActive(), "Yielded remains read-only");
        Require(backend.WriteCount == 6, "there is no automatic sixth write");

        Require(limiter.Release(), "yielded session releases without overwriting external owner");
        Require(backend.Raw == FakeBackend.ExternalRaw,
            "release after Yielded preserves external power value");
        Require(backend.WriteCount == 6,
            "yield release does not restore stale 45/115 baseline");

        output.WriteLine("PASS five rejected reacquires yield and preserve external control");
    }

    private static void LockWhileOwnedFailsWithoutAnotherWrite(TextWriter output)
    {
        var backend = new FakeBackend();
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "lock fixture apply");

        backend.Raw = FakeBackend.LockedAppliedRaw;
        Require(!limiter.VerifyActive(), "lock appearance detected");
        Require(limiter.State == CpuPowerLimiterState.Failed,
            "lock while requested PL fields remain present is unresolved Failed");
        Require(backend.WriteCount == 1, "lock path never writes");
        Require(!limiter.Release(), "locked owned value cannot be falsely declared released");
        Require(backend.WriteCount == 1, "release does not try to clear lock or rewrite RAPL");

        output.WriteLine("PASS lock while owned fails closed with no bypass write");
    }

    private static void InitialRejectedApplyRestoresBaseline(TextWriter output)
    {
        var backend = new FakeBackend { RejectApply = true };
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(!limiter.Apply(new CpuPowerLimitRequest(20, 40)), "rejected readback returns false");
        Require(backend.Raw == FakeBackend.BaselineRaw, "rejected apply baseline retained");
        Require(backend.WriteCount == 1, "rejected hardware saw only attempted apply");

        output.WriteLine("PASS initial rejected apply retains baseline behavior");
    }

    private static void UnsupportedBackendNeverWrites(TextWriter output)
    {
        var backend = new FakeBackend { IsSupported = false };
        var clock = new FakeActiveTimeClock();

        using var limiter = new CpuPowerLimiter(backend, clock);
        Require(limiter.State == CpuPowerLimiterState.Unsupported, "unsupported state");
        Require(!limiter.Apply(new CpuPowerLimitRequest(20, 40)), "unsupported apply denied");
        Require(backend.WriteCount == 0, "unsupported path never writes");

        output.WriteLine("PASS unsupported CPU backend remains write-closed");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeActiveTimeClock : IActiveTimeClock
    {
        public ulong Milliseconds { get; private set; }

        internal void Advance(TimeSpan duration)
        {
            Milliseconds = checked(
                Milliseconds + ActiveTimeClock.TimeoutMilliseconds(duration));
        }
    }

    private sealed class FakeBackend : ICpuPowerLimitBackend
    {
        internal const ulong BaselineRaw = 0x1000;
        internal const ulong AppliedRaw = 0x2000;
        internal const ulong AppliedMetadataRaw = 0x2100;
        internal const ulong ExternalRaw = 0x3000;
        internal const ulong External2Raw = 0x3100;
        internal const ulong ReacquiredRaw = 0x4000;
        internal const ulong Reacquired2Raw = 0x4100;
        internal const ulong LockedAppliedRaw = 0xA000;
        internal const ulong LockedExternalRaw = 0xB000;

        internal ulong Raw = BaselineRaw;
        internal int WriteCount;
        internal bool RejectApply;
        internal bool RejectReacquire;

        public bool IsSupported { get; set; } = true;

        public CpuPowerLimitSnapshot Read() => Snapshot(Raw);

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

            return new(AppliedRaw, 20, 40);
        }

        public bool OwnedFieldsMatch(
            ulong expectedRaw,
            CpuPowerLimitSnapshot current)
        {
            var expected = Snapshot(expectedRaw);
            return expected.Pl1Watts == current.Pl1Watts &&
                   expected.Pl2Watts == current.Pl2Watts;
        }

        public CpuPowerLimitApplyPlan BuildReacquirePlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current)
        {
            if (originalBaseline.Raw != BaselineRaw ||
                request.Pl1Watts != 20 ||
                request.Pl2Watts != 40 ||
                current.Locked)
            {
                throw new InvalidOperationException(
                    "unexpected fake reacquire plan request");
            }

            var raw = current.Raw == External2Raw
                ? Reacquired2Raw
                : ReacquiredRaw;

            return new(raw, 20, 40);
        }

        public CpuPowerLimitRestorePlan PlanRestore(
            CpuPowerLimitSnapshot restoreTarget,
            ulong appliedRaw,
            CpuPowerLimitSnapshot current)
        {
            if (current.Raw == restoreTarget.Raw)
                return new(current.Raw, "ALREADY_TARGET");

            if (current.Locked)
                return new(current.Raw, "RESTORE_BLOCKED_LOCK");

            if (OwnedFieldsMatch(appliedRaw, current))
                return new(restoreTarget.Raw, "RESTORE_PLANNED");

            return new(current.Raw, "EXTERNAL_CHANGE_PRESERVED");
        }

        public void Write(ulong raw)
        {
            WriteCount++;

            if (RejectApply && raw == AppliedRaw)
                return;

            if (RejectReacquire &&
                raw is ReacquiredRaw or Reacquired2Raw)
            {
                return;
            }

            Raw = raw;
        }

        private static CpuPowerLimitSnapshot Snapshot(ulong raw) =>
            raw switch
            {
                BaselineRaw => new(raw, 45, 115, false),
                AppliedRaw => new(raw, 20, 40, false),
                AppliedMetadataRaw => new(raw, 20, 40, false),
                ExternalRaw => new(raw, 30, 60, false),
                External2Raw => new(raw, 35, 70, false),
                ReacquiredRaw => new(raw, 20, 40, false),
                Reacquired2Raw => new(raw, 20, 40, false),
                LockedAppliedRaw => new(raw, 20, 40, true),
                LockedExternalRaw => new(raw, 30, 60, true),
                _ => new(raw, 30, 60, false)
            };
    }
}
