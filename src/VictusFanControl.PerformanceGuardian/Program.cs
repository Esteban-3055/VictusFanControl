using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class Program
{
    public static async Task<int> Main(
        string[] args)
    {
        try
        {
            if (args.Length == 1 &&
                args[0] == "--self-test")
            {
                return PerformanceGuardianAuthoritySelfTest.Run(
                    Console.Out);
            }

            if (args.Length == 2 &&
                args[0] == "--process-fixture")
            {
                return await PerformanceGuardianProcessFixture
                    .RunOuterAsync(
                        args[1])
                    .ConfigureAwait(false);
            }

            if (args.Length == 2 &&
                args[0] == "--fixture-supervisor")
            {
                return await PerformanceGuardianProcessFixture
                    .RunSupervisorAsync(
                        args[1])
                    .ConfigureAwait(false);
            }

            if (args.Length > 0 &&
                args[0] == "--run-fixture")
            {
                if (!TryParseFixtureRun(
                        args,
                        out var options,
                        out var error))
                {
                    Console.Error.WriteLine(
                        error);

                    return 2;
                }

                var domains =
                    new RecordingGuardianDomainLifecycle();

                var host =
                    new PerformanceGuardianHost(
                        options,
                        domains);

                return await host.RunAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Performance Guardian failed: " +
                ex);

            return 99;
        }
    }

    private static bool TryParseFixtureRun(
        string[] args,
        out GuardianHostOptions options,
        out string error)
    {
        options =
            default;

        error =
            "Invalid Performance Guardian fixture arguments.";

        string? target =
            null;

        string? pipe =
            null;

        string? mutex =
            null;

        Guid nonce =
            Guid.Empty;

        int ownerPid =
            0;

        long ownerStartTicks =
            0;

        string? report =
            null;

        string? ready =
            null;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            if (index + 1 >=
                args.Length)
            {
                error =
                    "Incomplete Performance Guardian fixture argument.";

                return false;
            }

            var name =
                args[index];

            var value =
                args[++index];

            switch (name)
            {
                case "--target":
                    target =
                        value;
                    break;

                case "--pipe":
                    pipe =
                        value;
                    break;

                case "--mutex":
                    mutex =
                        value;
                    break;

                case "--nonce":
                    if (!Guid.TryParse(
                            value,
                            out nonce))
                    {
                        error =
                            "Invalid session nonce.";

                        return false;
                    }

                    break;

                case "--owner-pid":
                    if (!int.TryParse(
                            value,
                            out ownerPid))
                    {
                        error =
                            "Invalid owner PID.";

                        return false;
                    }

                    break;

                case "--owner-start-ticks":
                    if (!long.TryParse(
                            value,
                            out ownerStartTicks))
                    {
                        error =
                            "Invalid owner start ticks.";

                        return false;
                    }

                    break;

                case "--report":
                    report =
                        Path.GetFullPath(
                            value);
                    break;

                case "--ready":
                    ready =
                        Path.GetFullPath(
                            value);
                    break;

                default:
                    error =
                        "Unknown Performance Guardian fixture argument: " +
                        name;

                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(
                target) ||
            string.IsNullOrWhiteSpace(
                pipe) ||
            string.IsNullOrWhiteSpace(
                mutex) ||
            nonce ==
                Guid.Empty ||
            ownerPid <= 0 ||
            ownerStartTicks <= 0 ||
            string.IsNullOrWhiteSpace(
                report))
        {
            return false;
        }

        options =
            new GuardianHostOptions(
                target!,
                pipe!,
                mutex!,
                nonce,
                ownerPid,
                ownerStartTicks,
                report!,
                ready);

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --self-test");

        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --process-fixture <directory>");

        Console.WriteLine(
            "Production run mode is intentionally not exposed yet; this step qualifies only software lifecycle/IPC with recording domains.");
    }
}
