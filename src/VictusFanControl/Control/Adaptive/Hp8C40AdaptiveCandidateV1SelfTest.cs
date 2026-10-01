namespace VictusFanControl.Control.Adaptive;

public static class Hp8C40AdaptiveCandidateV1SelfTest
{
    public static int Run(TextWriter output)
    {
        var failures = 0;
        failures += TestAuthorizationBoundary(output);
        failures += TestEnvelopeAndSmoothing(output);
        failures += TestIdleDemand(output);
        failures += TestThermalCeilings(output);
        failures += TestUpwardSlew(output);

        output.WriteLine();
        output.WriteLine(
            failures == 0
                ? "HP 8C40 adaptive candidate v1 self-test: PASS"
                : $"HP 8C40 adaptive candidate v1 self-test: FAIL ({failures} case(s))");
        return failures == 0 ? 0 : 35;
    }

    private static int TestAuthorizationBoundary(TextWriter output) =>
        Report(
            output,
            "candidate remains explicitly unvalidated and unauthorized",
            !Hp8C40AdaptiveCandidateV1.PhysicallyValidated &&
            !Hp8C40AdaptiveCandidateV1.AuthorizedForProduction);

    private static int TestEnvelopeAndSmoothing(TextWriter output)
    {
        var config = Hp8C40AdaptiveCandidateV1.Create();
        _ = new AdaptiveFanPolicyEngine(config);

        return Report(
            output,
            "candidate remains equal-envelope 10..50 with conservative fall smoothing",
            config.MinimumLevel == 10 &&
            config.MaximumLevel == 50 &&
            config.MaximumUpStepPerSample == 4 &&
            config.MaximumDownStepPerSample == 1 &&
            config.DecreaseConfirmationSamples == 5 &&
            Math.Abs(config.DecreaseDeadbandLevels - 1.0) < 0.000001 &&
            config.MaximumSampleGap == TimeSpan.FromSeconds(3));
    }

    private static int TestIdleDemand(TextWriter output)
    {
        var engine = new AdaptiveFanPolicyEngine(
            Hp8C40AdaptiveCandidateV1.Create());

        var decision = engine.Evaluate(
            Input(
                DateTimeOffset.UtcNow,
                cpuC: 38,
                cpuPowerW: 5,
                cpuLoad: 5,
                gpuC: 32,
                gpuPowerW: 5,
                gpuLoad: 5));

        return Report(
            output,
            "candidate low-demand epoch starts at equal level 10",
            decision.Accepted &&
            decision.EqualFanLevel == 10 &&
            decision.RawDemandLevel == 10);
    }

    private static int TestThermalCeilings(TextWriter output)
    {
        var cpuEngine = new AdaptiveFanPolicyEngine(
            Hp8C40AdaptiveCandidateV1.Create());
        var gpuEngine = new AdaptiveFanPolicyEngine(
            Hp8C40AdaptiveCandidateV1.Create());

        var now = DateTimeOffset.UtcNow;

        var cpu = cpuEngine.Evaluate(
            Input(now, 90, 10, 5, 35, 5, 5));
        var gpu = gpuEngine.Evaluate(
            Input(now, 40, 10, 5, 84, 5, 5));

        return Report(
            output,
            "candidate reaches level 50 before HP 8C40 hard thermal handoff thresholds",
            cpu.EqualFanLevel == 50 &&
            gpu.EqualFanLevel == 50);
    }

    private static int TestUpwardSlew(TextWriter output)
    {
        var engine = new AdaptiveFanPolicyEngine(
            Hp8C40AdaptiveCandidateV1.Create());
        var now = DateTimeOffset.UtcNow;

        var idle = engine.Evaluate(
            Input(now, 38, 5, 5, 32, 5, 5));
        var hot = engine.Evaluate(
            Input(
                now + TimeSpan.FromSeconds(1),
                90,
                115,
                100,
                84,
                140,
                100));

        return Report(
            output,
            "candidate upward change remains bounded to four levels per sample",
            idle.EqualFanLevel == 10 &&
            hot.EqualFanLevel == 14);
    }

    private static AdaptiveFanPolicyInput Input(
        DateTimeOffset timestamp,
        double cpuC,
        double cpuPowerW,
        double cpuLoad,
        double gpuC,
        double gpuPowerW,
        double gpuLoad) =>
        new(
            timestamp,
            cpuC,
            cpuPowerW,
            cpuLoad,
            gpuC,
            gpuPowerW,
            gpuLoad);

    private static int Report(TextWriter output, string name, bool pass)
    {
        output.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
        return pass ? 0 : 1;
    }
}
