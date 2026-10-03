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

            var backend = new FakeBackend();
            using (var limiter = new CpuPowerLimiter(backend))
            {
                Require(limiter.State == CpuPowerLimiterState.Disabled, "starts disabled");
                Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "apply 20/40");
                Require(limiter.State == CpuPowerLimiterState.Active, "active after exact readback");
                Require(backend.WriteCount == 1, "single apply write");
                Require(limiter.VerifyActive(), "verify exact active raw");
                Require(backend.WriteCount == 1, "verify never reapplies");
                Require(limiter.Release(), "release");
                Require(limiter.State == CpuPowerLimiterState.Disabled, "disabled after release");
                Require(backend.WriteCount == 2, "one apply plus one restore");
                Require(backend.Raw == FakeBackend.BaselineRaw, "baseline restored");
            }

            var external = new FakeBackend();
            using (var limiter = new CpuPowerLimiter(external))
            {
                Require(limiter.Apply(new CpuPowerLimitRequest(20, 40)), "external fixture apply");
                external.Raw = FakeBackend.ExternalRaw;
                Require(!limiter.VerifyActive(), "external change detected");
                Require(limiter.State == CpuPowerLimiterState.Failed, "external change fails closed");
                Require(external.WriteCount == 1, "external change is never blindly reapplied");
                Require(!limiter.Release(), "external change not overwritten by restore");
                Require(external.Raw == FakeBackend.ExternalRaw, "external value preserved");
            }

            var rejected = new FakeBackend { RejectApply = true };
            using (var limiter = new CpuPowerLimiter(rejected))
            {
                Require(!limiter.Apply(new CpuPowerLimitRequest(20, 40)), "rejected readback returns false");
                Require(rejected.Raw == FakeBackend.BaselineRaw, "rejected apply baseline retained");
                Require(rejected.WriteCount == 1, "rejected hardware saw only attempted apply");
            }

            var unsupported = new FakeBackend { IsSupported = false };
            using (var limiter = new CpuPowerLimiter(unsupported))
            {
                Require(limiter.State == CpuPowerLimiterState.Unsupported, "unsupported state");
                Require(!limiter.Apply(new CpuPowerLimitRequest(20, 40)), "unsupported apply denied");
                Require(unsupported.WriteCount == 0, "unsupported path never writes");
            }

            output.WriteLine("CPU power limiter P2 foundation self-test: PASS (no hardware I/O).");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine("CPU power limiter P2 foundation self-test: FAIL - " + ex.Message);
            return 1;
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeBackend : ICpuPowerLimitBackend
    {
        internal const ulong BaselineRaw = 0x1000;
        internal const ulong AppliedRaw = 0x2000;
        internal const ulong ExternalRaw = 0x3000;

        internal ulong Raw = BaselineRaw;
        internal int WriteCount;
        internal bool RejectApply;
        public bool IsSupported { get; set; } = true;

        public CpuPowerLimitSnapshot Read() =>
            Raw switch
            {
                BaselineRaw => new(Raw, 45, 115, false),
                AppliedRaw => new(Raw, 20, 40, false),
                _ => new(Raw, 30, 60, false)
            };

        public CpuPowerLimitApplyPlan BuildApplyPlan(
            CpuPowerLimitSnapshot baseline,
            CpuPowerLimitRequest request)
        {
            if (baseline.Raw != BaselineRaw ||
                request.Pl1Watts != 20 ||
                request.Pl2Watts != 40)
                throw new InvalidOperationException("unexpected fake plan request");
            return new(AppliedRaw, 20, 40);
        }

        public CpuPowerLimitRestorePlan PlanRestore(
            CpuPowerLimitSnapshot baseline,
            ulong appliedRaw,
            CpuPowerLimitSnapshot current)
        {
            if (current.Raw == baseline.Raw)
                return new(current.Raw, "ALREADY_BASELINE");
            if (current.Raw == appliedRaw)
                return new(baseline.Raw, "RESTORE_PLANNED");
            return new(current.Raw, "EXTERNAL_CHANGE_PRESERVED");
        }

        public void Write(ulong raw)
        {
            WriteCount++;
            if (RejectApply && raw == AppliedRaw)
                return;
            Raw = raw;
        }
    }
}
