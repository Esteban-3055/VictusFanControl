using VictusFanControl.Runtime;

namespace VictusFanControl.Performance;

internal static class CpuPowerConflictPolicySelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            FirstAttemptWaitsThirtySeconds(output);
            RepeatedObservationDoesNotPostponeRetry(output);
            RequestedValueCanReturnWithoutWrite(output);
            SuccessfulReacquireNeedsSixtyStableSeconds(output);
            ConflictDuringStabilityKeepsBudget(output);
            FiveFailedAttemptsYield(output);
            FifthSuccessThenConflictYields(output);
            ExplicitUserResetCreatesNewEpisode(output);
            ForceYieldBlocksAutomaticRetry(output);

            output.WriteLine(
                "CPU power conflict policy self-test: PASS (5 attempts / 30 s / 60 s, no hardware I/O).");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "CPU power conflict policy self-test: FAIL - " + ex.Message);
            return 1;
        }
    }

    private static void FirstAttemptWaitsThirtySeconds(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);

        policy.ObserveExternalChange();
        Require(policy.State == CpuPowerConflictState.Contested, "first conflict enters Contested");
        Require(!policy.TryBeginReacquire(), "no immediate reacquire");

        clock.Advance(TimeSpan.FromMilliseconds(29_999));
        Require(!policy.TryBeginReacquire(), "29.999 s is still blocked");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Require(policy.TryBeginReacquire(), "30 s permits attempt 1");
        Require(policy.AttemptsUsed == 1, "attempt 1 consumed exactly once");
        policy.CompleteReacquire(exactReadback: false);

        output.WriteLine("PASS conflict attempt 1 is delayed by 30 active seconds");
    }

    private static void RepeatedObservationDoesNotPostponeRetry(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);

        policy.ObserveExternalChange();
        clock.Advance(TimeSpan.FromSeconds(20));
        policy.ObserveExternalChange();
        clock.Advance(TimeSpan.FromSeconds(10));

        Require(policy.TryBeginReacquire(),
            "repeated observation while Contested must not move the original deadline");
        policy.CompleteReacquire(exactReadback: false);

        output.WriteLine("PASS repeated external observations do not starve the retry deadline");
    }

    private static void RequestedValueCanReturnWithoutWrite(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);

        policy.ObserveExternalChange();
        clock.Advance(TimeSpan.FromSeconds(10));
        policy.ObserveRequestedValuePresent();

        Require(policy.State == CpuPowerConflictState.ReacquiredPendingStability,
            "returned requested value starts stability without a write");
        Require(policy.AttemptsUsed == 0,
            "returned requested value does not consume an attempt");

        clock.Advance(TimeSpan.FromSeconds(60));
        Require(policy.TryCompleteStableWindow(),
            "returned requested value can become stable");
        Require(policy.State == CpuPowerConflictState.Inactive,
            "stable return closes episode");

        output.WriteLine("PASS requested value may return without consuming a write attempt");
    }

    private static void SuccessfulReacquireNeedsSixtyStableSeconds(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);

        policy.ObserveExternalChange();
        clock.Advance(TimeSpan.FromSeconds(30));
        Require(policy.TryBeginReacquire(), "begin successful attempt");
        policy.CompleteReacquire(exactReadback: true);

        Require(policy.State == CpuPowerConflictState.ReacquiredPendingStability,
            "successful readback starts stability window");
        clock.Advance(TimeSpan.FromSeconds(59));
        Require(!policy.TryCompleteStableWindow(), "59 s does not reset the conflict budget");
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(policy.TryCompleteStableWindow(), "60 s stable closes the conflict episode");
        Require(policy.State == CpuPowerConflictState.Inactive, "stable episode returns Inactive");
        Require(policy.AttemptsUsed == 0, "stable episode resets attempt budget");

        output.WriteLine("PASS successful reacquisition requires 60 active seconds of stability");
    }

    private static void ConflictDuringStabilityKeepsBudget(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);

        policy.ObserveExternalChange();
        clock.Advance(TimeSpan.FromSeconds(30));
        Require(policy.TryBeginReacquire(), "attempt 1 begins");
        policy.CompleteReacquire(exactReadback: true);

        clock.Advance(TimeSpan.FromSeconds(10));
        policy.ObserveExternalChange();
        Require(policy.State == CpuPowerConflictState.Contested,
            "new conflict during stability returns Contested");
        Require(policy.AttemptsUsed == 1,
            "successful but unstable attempt stays consumed");

        clock.Advance(TimeSpan.FromSeconds(30));
        Require(policy.TryBeginReacquire(), "attempt 2 begins after renewed 30 s delay");
        Require(policy.AttemptsUsed == 2, "attempt budget is not reset early");
        policy.CompleteReacquire(exactReadback: false);

        output.WriteLine("PASS renewed conflict keeps the same five-attempt episode");
    }

    private static void FiveFailedAttemptsYield(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);
        policy.ObserveExternalChange();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Require(policy.TryBeginReacquire(), $"attempt {attempt} begins");
            Require(policy.AttemptsUsed == attempt, $"attempt {attempt} counted");
            policy.CompleteReacquire(exactReadback: false);

            var expected = attempt == 5
                ? CpuPowerConflictState.Yielded
                : CpuPowerConflictState.Contested;
            Require(policy.State == expected, $"attempt {attempt} final state");
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        Require(!policy.TryBeginReacquire(), "Yielded never starts attempt 6 automatically");
        Require(policy.AttemptsUsed == 5, "attempt budget remains capped at five");

        output.WriteLine("PASS five failed reacquisitions yield permanently for the episode");
    }

    private static void FifthSuccessThenConflictYields(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);
        policy.ObserveExternalChange();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Require(policy.TryBeginReacquire(), $"pre-yield attempt {attempt}");
            policy.CompleteReacquire(exactReadback: false);
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        Require(policy.TryBeginReacquire(), "fifth attempt begins");
        policy.CompleteReacquire(exactReadback: true);
        Require(policy.State == CpuPowerConflictState.ReacquiredPendingStability,
            "fifth exact readback is provisionally accepted");

        clock.Advance(TimeSpan.FromSeconds(10));
        policy.ObserveExternalChange();
        Require(policy.State == CpuPowerConflictState.Yielded,
            "external change after fifth provisional success yields without attempt 6");

        output.WriteLine("PASS fifth provisional success cannot create a hidden sixth attempt");
    }

    private static void ExplicitUserResetCreatesNewEpisode(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);
        policy.ObserveExternalChange();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Require(policy.TryBeginReacquire(), "consume retry budget");
            policy.CompleteReacquire(exactReadback: false);
        }

        Require(policy.State == CpuPowerConflictState.Yielded, "fixture reached Yielded");
        policy.ResetByUser();
        Require(policy.State == CpuPowerConflictState.Inactive, "explicit reset returns Inactive");
        Require(policy.AttemptsUsed == 0, "explicit reset clears retry budget");

        policy.ObserveExternalChange();
        clock.Advance(TimeSpan.FromSeconds(30));
        Require(policy.TryBeginReacquire(), "new explicit episode gets a new attempt 1");
        Require(policy.AttemptsUsed == 1, "new episode starts at attempt 1");
        policy.CompleteReacquire(exactReadback: false);

        output.WriteLine("PASS only explicit user reset rearms a yielded episode");
    }

    private static void ForceYieldBlocksAutomaticRetry(TextWriter output)
    {
        var clock = new FakeActiveTimeClock();
        var policy = new CpuPowerConflictPolicy(clock);

        policy.ObserveExternalChange();
        policy.ForceYield();
        clock.Advance(TimeSpan.FromMinutes(10));

        Require(policy.State == CpuPowerConflictState.Yielded, "forced safety yield state");
        Require(!policy.TryBeginReacquire(), "forced safety yield blocks automatic writes");

        output.WriteLine("PASS safety/lifecycle can force immediate yield without a write");
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
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));

            Milliseconds = checked(
                Milliseconds + ActiveTimeClock.TimeoutMilliseconds(duration));
        }
    }
}
