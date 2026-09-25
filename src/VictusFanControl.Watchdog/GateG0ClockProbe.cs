namespace VictusFanControl.Watchdog;

internal static class GateG0ClockProbe
{
    private const string ModeArgument = "--gate-g0-clock-probe";
    private const string MinimumSleepArgument = "--minimum-sleep-seconds";
    private const string TimeoutArgument = "--timeout-seconds";
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);

    public static bool IsRequested(string[] args) =>
        args.Any(
            argument =>
                string.Equals(
                    argument,
                    ModeArgument,
                    StringComparison.Ordinal));

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        if (!OperatingSystem.IsWindows())
        {
            await error.WriteLineAsync(
                "Gate G0 physical clock probe requires Windows.");
            return 2;
        }

        int minimumSleepSeconds;
        int timeoutSeconds;

        try
        {
            (minimumSleepSeconds, timeoutSeconds) = Parse(args);
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 2;
        }

        IMonotonicClock clock = new WindowsMonotonicClock();

        var first = clock.Milliseconds;
        await Task.Delay(PollInterval).ConfigureAwait(false);
        var second = clock.Milliseconds;

        if (second < first)
        {
            await error.WriteLineAsync(
                $"Production monotonic clock moved backwards before sleep test: {first} -> {second}");
            return 1;
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync(
            $"Preflight monotonic sample: {first} -> {second} ms");
        await output.WriteLineAsync();
        await output.WriteLineAsync(
            $"After arming, put Windows to Sleep and keep it asleep for at least {minimumSleepSeconds} seconds.");
        await output.WriteLineAsync(
            $"The detector will wait up to {timeoutSeconds} seconds. Do not close this console.");
        await output.WriteLineAsync(
            "After resume, the same .NET 8 process compares UTC wall time with the production unbiased clock.");
        await output.WriteLineAsync();
        await output.WriteAsync("Press Enter to arm the read-only detector: ");

        if (await input.ReadLineAsync().ConfigureAwait(false) is null)
        {
            await error.WriteLineAsync(
                "Gate G0 probe could not read the arming confirmation.");
            return 2;
        }

        var minimumExcludedMs =
            checked((double)minimumSleepSeconds * 1000.0);
        var deadlineUtc =
            DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        var previousWall = DateTimeOffset.UtcNow;
        var previousMonotonic = clock.Milliseconds;

        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            await Task.Delay(PollInterval).ConfigureAwait(false);

            var wallNow = DateTimeOffset.UtcNow;
            var monotonicNow = clock.Milliseconds;

            if (monotonicNow < previousMonotonic)
            {
                await error.WriteLineAsync(
                    $"Production monotonic clock moved backwards: {previousMonotonic} -> {monotonicNow}");
                return 1;
            }

            var wallStepMs =
                (wallNow - previousWall).TotalMilliseconds;
            var monotonicStepMs =
                (double)(monotonicNow - previousMonotonic);
            var excludedStepMs =
                wallStepMs - monotonicStepMs;

            if (excludedStepMs >= minimumExcludedMs)
            {
                await output.WriteLineAsync();
                await output.WriteLineAsync(
                    $"Detected suspend/resume interval: wall +{wallStepMs:N0} ms; unbiased +{monotonicStepMs:N0} ms; excluded ~{excludedStepMs:N0} ms.");
                await output.WriteLineAsync(
                    "Gate G0 physical clock semantics: PASS");
                await output.WriteLineAsync(
                    "Wall time advanced across sleep while the production QueryUnbiasedInterruptTime-based clock did not count the sleep interval.");
                return 0;
            }

            previousWall = wallNow;
            previousMonotonic = monotonicNow;
        }

        await error.WriteLineAsync(
            $"No qualifying sleep interval was detected within {timeoutSeconds} seconds. Re-run and keep the machine asleep for at least {minimumSleepSeconds} seconds.");
        return 1;
    }

    private static (int MinimumSleepSeconds, int TimeoutSeconds)
        Parse(string[] args)
    {
        var minimumSleepSeconds = 10;
        var timeoutSeconds = 300;
        var modeSeen = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case ModeArgument:
                    if (modeSeen)
                    {
                        throw new ArgumentException(
                            $"{ModeArgument} may be specified only once.");
                    }

                    modeSeen = true;
                    break;

                case MinimumSleepArgument:
                    minimumSleepSeconds =
                        ReadBoundedInt(
                            args,
                            ref index,
                            MinimumSleepArgument,
                            5,
                            120);
                    break;

                case TimeoutArgument:
                    timeoutSeconds =
                        ReadBoundedInt(
                            args,
                            ref index,
                            TimeoutArgument,
                            30,
                            900);
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown Gate G0 clock probe argument: {args[index]}");
            }
        }

        if (!modeSeen)
        {
            throw new ArgumentException(
                $"{ModeArgument} is required.");
        }

        return (minimumSleepSeconds, timeoutSeconds);
    }

    private static int ReadBoundedInt(
        string[] args,
        ref int index,
        string option,
        int minimum,
        int maximum)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException(
                $"Missing value for {option}.");
        }

        if (!int.TryParse(args[index], out var value) ||
            value < minimum ||
            value > maximum)
        {
            throw new ArgumentException(
                $"{option} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }
}
