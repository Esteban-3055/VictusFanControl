using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class PerformanceGuardianCleanupSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            SourceStopFailureStillReleases(
                output);

            BothFailuresAreAggregated(
                output);

            output.WriteLine(
                "Performance Guardian cleanup self-test: PASS (domain release is attempted even when source Stop fails; no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance Guardian cleanup self-test: FAIL - " +
                ex);

            return 1;
        }
    }

    private static void SourceStopFailureStillReleases(
        TextWriter output)
    {
        var source =
            new FakeSourceRuntime
            {
                ThrowOnStop = true
            };

        var domains =
            new FakeDomains();

        var result =
            PerformanceGuardianHost.AttemptSessionCleanupAsync(
                    source,
                    domains,
                    cpuEnabled: true,
                    gpuEnabled: false,
                    "SELF_TEST_STOP_FAILURE",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        Require(
            source.StopCalls == 1 &&
            domains.ReleaseCalls == 1,
            "release must be attempted after source Stop failure");

        Require(
            result.SourceStopAttempted &&
            result.ReleaseAttempted &&
            result.Failure is InvalidOperationException,
            "single source failure is surfaced after release attempt");

        output.WriteLine(
            "PASS source Stop failure cannot suppress domain release attempt");
    }

    private static void BothFailuresAreAggregated(
        TextWriter output)
    {
        var source =
            new FakeSourceRuntime
            {
                ThrowOnStop = true
            };

        var domains =
            new FakeDomains
            {
                ThrowOnRelease = true
            };

        var result =
            PerformanceGuardianHost.AttemptSessionCleanupAsync(
                    source,
                    domains,
                    cpuEnabled: true,
                    gpuEnabled: true,
                    "SELF_TEST_BOTH_FAILURES",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        Require(
            source.StopCalls == 1 &&
            domains.ReleaseCalls == 1,
            "both cleanup stages must be attempted exactly once");

        Require(
            result.Failure is AggregateException aggregate &&
            aggregate.InnerExceptions.Count == 2,
            "source and release failures must both remain visible");

        output.WriteLine(
            "PASS source+domain cleanup failures are aggregated after both attempts");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                label);
        }
    }

    private sealed class FakeSourceRuntime :
        IGuardianPowerSourceRuntime
    {
        internal int StopCalls;
        internal bool ThrowOnStop;

        public GuardianPowerSourceRuntimeSnapshot Snapshot =>
            default;

        public PerformanceSourceDispatchResult Prime(
            bool cpuEnabled,
            bool gpuEnabled) =>
            throw new NotSupportedException();

        public void ActivateListener() =>
            throw new NotSupportedException();

        public void Start(
            bool cpuEnabled,
            bool gpuEnabled) =>
            throw new NotSupportedException();

        public void Stop(
            string reason)
        {
            StopCalls++;

            if (ThrowOnStop)
            {
                throw new InvalidOperationException(
                    "synthetic source Stop failure");
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDomains :
        IGuardianDomainLifecycle
    {
        internal int ReleaseCalls;
        internal bool ThrowOnRelease;

        public GuardianDomainLifecycleSnapshot Snapshot =>
            default;

        public ValueTask EnableAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            PerformancePowerSourceKind initialSource,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReleaseAsync(
            bool cpuEnabled,
            bool gpuEnabled,
            string reason,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;

            if (ThrowOnRelease)
            {
                throw new InvalidOperationException(
                    "synthetic domain release failure");
            }

            return ValueTask.CompletedTask;
        }
    }
}
