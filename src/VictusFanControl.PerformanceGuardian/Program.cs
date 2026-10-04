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
                var authorityResult =
                    PerformanceGuardianAuthoritySelfTest.Run(
                        Console.Out);

                var sourceRuntimeResult =
                    PerformanceGuardianPowerSourceRuntimeSelfTest.Run(
                        Console.Out);

                var gpuDomainResult =
                    QualifiedGpuGuardianDomainLifecycleSelfTest.Run(
                        Console.Out);

                var cleanupResult =
                    PerformanceGuardianCleanupSelfTest.Run(
                        Console.Out);

                var cpuBackendResult =
                    PawnIoCpuPowerLimitBackendSelfTest.Run(
                        Console.Out);

                var cpuDomainResult =
                    QualifiedCpuGuardianDomainLifecycleSelfTest.Run(
                        Console.Out);

                return authorityResult == 0 &&
                    sourceRuntimeResult == 0 &&
                    gpuDomainResult == 0 &&
                    cleanupResult == 0 &&
                    cpuBackendResult == 0 &&
                    cpuDomainResult == 0
                    ? 0
                    : 1;
            }

            if (args.Length == 1 &&
                args[0] == "--cpu-gate-6g-physical-self-test")
            {
                return PerformanceGuardianCpuGate6GPhysicalQualification.SelfTest(
                    Console.Out);
            }

            if (args.Length > 0 &&
                args[0] == "--cpu-gate-6g")
            {
                return await PerformanceGuardianCpuGate6GPhysicalQualification
                    .RunOuterAsync(
                        args)
                    .ConfigureAwait(false);
            }

            if (args.Length > 0 &&
                args[0] == "--run-cpu-gate-6g")
            {
                return await PerformanceGuardianCpuGate6GPhysicalQualification
                    .RunGuardianAsync(
                        args)
                    .ConfigureAwait(false);
            }

            if (args.Length == 1 &&
                args[0] == "--cpu-gate-6g-self-test")
            {
                return PerformanceGuardianCpuGate6GQualification.SelfTest(
                    Console.Out);
            }

            if (args.Length > 0 &&
                args[0] == "--cpu-gate-6g-preflight")
            {
                return PerformanceGuardianCpuGate6GQualification
                    .RunPreflight(
                        args);
            }

            if (args.Length == 1 &&
                args[0] == "--gpu-gate-6f-self-test")
            {
                return PerformanceGuardianGpuGate6FQualification.SelfTest(
                    Console.Out);
            }

            if (args.Length > 0 &&
                args[0] == "--gpu-gate-6f-preflight")
            {
                return PerformanceGuardianGpuGate6FQualification
                    .RunPreflight(
                        args);
            }

            if (args.Length > 0 &&
                args[0] == "--gpu-gate-6f")
            {
                return await PerformanceGuardianGpuGate6FQualification
                    .RunOuterAsync(
                        args)
                    .ConfigureAwait(false);
            }

            if (args.Length > 0 &&
                args[0] == "--run-gpu-gate-6f")
            {
                return await PerformanceGuardianGpuGate6FQualification
                    .RunGuardianAsync(
                        args)
                    .ConfigureAwait(false);
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

                var cpuSource =
                    new RecordingGuardianCpuSourceTransitionSink();

                var gpuSource =
                    new RecordingGuardianGpuSourceTransitionSink();

                var sourceRuntime =
                    new GuardianPerformancePowerSourceRuntime(
                        new WindowsPerformancePowerSourceReader(),
                        cpuSource,
                        gpuSource,
                        new WindowsGuardianPowerSourceNotificationListenerFactory());

                var host =
                    new PerformanceGuardianHost(
                        options,
                        domains,
                        sourceRuntime);

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
            "VictusFanControl.PerformanceGuardian --cpu-gate-6g-physical-self-test");

        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --cpu-gate-6g --confirm-target HP-8C40-9D0R1LA-F18 --confirm-cpu-hardware-writes --module <IntelMSR.bin> [--timeout-seconds 15..300] [--output-directory <path>]");

        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --cpu-gate-6g-self-test");

        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --cpu-gate-6g-preflight --confirm-target HP-8C40-9D0R1LA-F18 --module <IntelMSR.bin> [--output <json-path>]");

        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --gpu-gate-6f-self-test");

        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --gpu-gate-6f-preflight --confirm-target HP-8C40-9D0R1LA-F18 [--output <json-path>]");

        Console.WriteLine(
            "VictusFanControl.PerformanceGuardian --gpu-gate-6f --confirm-target HP-8C40-9D0R1LA-F18 [--timeout-seconds 60] [--output-directory <path>]");

        Console.WriteLine(
            "Production run mode remains closed. Step 6F GPU and Step 6G CPU entry points are explicit qualification gates only.");
    }
}
