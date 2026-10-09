namespace VictusFanControl.Control.Adaptive;

public static class AdaptiveFanPolicySelfTest
{
    public static int Run(TextWriter output)
    {
        var failures = 0;

        failures += TestInterpolation(output);
        failures += TestHighestDomainWins(output);
        failures += TestUpwardSlew(output);
        failures += TestDownwardConfirmation(output);
        failures += TestEnvelope(output);
        failures += TestDuplicateTimestampRefused(output);
        failures += TestGapRefused(output);
        failures += TestInvalidTelemetryRefused(output);
        failures += TestInvalidConfigRefused(output);

        output.WriteLine();
        output.WriteLine(
            failures == 0
                ? "Adaptive fan policy self-test: PASS"
                : $"Adaptive fan policy self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 31;
    }

    private static int TestInterpolation(TextWriter output)
    {
        var curve = new[]
        {
            new AdaptiveFanCurvePoint(40, 10),
            new AdaptiveFanCurvePoint(60, 30),
            new AdaptiveFanCurvePoint(80, 50)
        };

        var midpoint =
            AdaptiveFanPolicyEngine.Interpolate(
                curve,
                50);

        return Report(
            output,
            "piecewise-linear interpolation is deterministic",
            Math.Abs(midpoint - 20.0) < 0.000001);
    }

    private static int TestHighestDomainWins(TextWriter output)
    {
        var engine =
            new AdaptiveFanPolicyEngine(
                BuildConfig(
                    maximumUpStep: 50));

        var decision =
            engine.Evaluate(
                Input(
                    DateTimeOffset.UtcNow,
                    cpuC: 55,
                    cpuPowerW: 20,
                    cpuLoad: 20,
                    gpuC: 60,
                    gpuPowerW: 120,
                    gpuLoad: 95));

        return Report(
            output,
            "maximum thermal/load/power demand wins",
            decision.Accepted &&
            decision.RawDemandLevel is >= 45 &&
            decision.EqualFanLevel is >= 45);
    }

    private static int TestUpwardSlew(TextWriter output)
    {
        var engine =
            new AdaptiveFanPolicyEngine(
                BuildConfig(
                    maximumUpStep: 4));

        var now = DateTimeOffset.UtcNow;

        var first =
            engine.Evaluate(
                Input(
                    now,
                    cpuC: 40,
                    cpuPowerW: 5,
                    cpuLoad: 5,
                    gpuC: 35,
                    gpuPowerW: 5,
                    gpuLoad: 5));

        var second =
            engine.Evaluate(
                Input(
                    now + TimeSpan.FromSeconds(1),
                    cpuC: 90,
                    cpuPowerW: 100,
                    cpuLoad: 100,
                    gpuC: 85,
                    gpuPowerW: 120,
                    gpuLoad: 100));

        return Report(
            output,
            "upward slew limits one policy step",
            first.EqualFanLevel == 10 &&
            second.EqualFanLevel == 14);
    }

    private static int TestDownwardConfirmation(TextWriter output)
    {
        var engine =
            new AdaptiveFanPolicyEngine(
                BuildConfig(
                    maximumUpStep: 50,
                    maximumDownStep: 2,
                    decreaseConfirmation: 3));

        var now = DateTimeOffset.UtcNow;

        var high =
            engine.Evaluate(
                Input(
                    now,
                    cpuC: 90,
                    cpuPowerW: 100,
                    cpuLoad: 100,
                    gpuC: 85,
                    gpuPowerW: 120,
                    gpuLoad: 100));

        var low1 =
            engine.Evaluate(
                Input(
                    now + TimeSpan.FromSeconds(1),
                    cpuC: 40,
                    cpuPowerW: 5,
                    cpuLoad: 5,
                    gpuC: 35,
                    gpuPowerW: 5,
                    gpuLoad: 5));

        var low2 =
            engine.Evaluate(
                Input(
                    now + TimeSpan.FromSeconds(2),
                    cpuC: 40,
                    cpuPowerW: 5,
                    cpuLoad: 5,
                    gpuC: 35,
                    gpuPowerW: 5,
                    gpuLoad: 5));

        var low3 =
            engine.Evaluate(
                Input(
                    now + TimeSpan.FromSeconds(3),
                    cpuC: 40,
                    cpuPowerW: 5,
                    cpuLoad: 5,
                    gpuC: 35,
                    gpuPowerW: 5,
                    gpuLoad: 5));

        return Report(
            output,
            "fan decrease requires confirmation and uses slower down-step",
            high.EqualFanLevel == 50 &&
            low1.EqualFanLevel == 50 &&
            low2.EqualFanLevel == 50 &&
            low3.EqualFanLevel == 48);
    }

    private static int TestEnvelope(TextWriter output)
    {
        var engine =
            new AdaptiveFanPolicyEngine(
                BuildConfig(
                    maximumUpStep: 50));

        var now = DateTimeOffset.UtcNow;

        var low =
            engine.Evaluate(
                Input(
                    now,
                    cpuC: 0,
                    cpuPowerW: 0,
                    cpuLoad: 0,
                    gpuC: 0,
                    gpuPowerW: 0,
                    gpuLoad: 0));

        engine.Reset();

        var high =
            engine.Evaluate(
                Input(
                    now + TimeSpan.FromSeconds(1),
                    cpuC: 120,
                    cpuPowerW: 400,
                    cpuLoad: 100,
                    gpuC: 100,
                    gpuPowerW: 250,
                    gpuLoad: 100));

        return Report(
            output,
            "policy output stays inside equal-only 10..50 envelope",
            low.EqualFanLevel == 10 &&
            high.EqualFanLevel == 50);
    }

    private static int TestDuplicateTimestampRefused(TextWriter output)
    {
        var engine =
            new AdaptiveFanPolicyEngine(
                BuildConfig());

        var now = DateTimeOffset.UtcNow;

        var first =
            engine.Evaluate(
                Input(
                    now,
                    cpuC: 60,
                    cpuPowerW: 30,
                    cpuLoad: 30,
                    gpuC: 55,
                    gpuPowerW: 40,
                    gpuLoad: 40));

        var before =
            engine.CurrentLevel;

        var duplicate =
            engine.Evaluate(
                Input(
                    now,
                    cpuC: 90,
                    cpuPowerW: 100,
                    cpuLoad: 100,
                    gpuC: 85,
                    gpuPowerW: 120,
                    gpuLoad: 100));

        return Report(
            output,
            "duplicate telemetry is refused without mutating output",
            first.Accepted &&
            !duplicate.Accepted &&
            duplicate.EqualFanLevel is null &&
            engine.CurrentLevel == before);
    }

    private static int TestGapRefused(TextWriter output)
    {
        var engine =
            new AdaptiveFanPolicyEngine(
                BuildConfig());

        var now = DateTimeOffset.UtcNow;

        _ = engine.Evaluate(
            Input(
                now,
                cpuC: 60,
                cpuPowerW: 30,
                cpuLoad: 30,
                gpuC: 55,
                gpuPowerW: 40,
                gpuLoad: 40));

        var before =
            engine.CurrentLevel;

        var gap =
            engine.Evaluate(
                Input(
                    now + TimeSpan.FromSeconds(10),
                    cpuC: 70,
                    cpuPowerW: 50,
                    cpuLoad: 50,
                    gpuC: 65,
                    gpuPowerW: 60,
                    gpuLoad: 60));

        return Report(
            output,
            "telemetry continuity gap is refused without a command decision",
            !gap.Accepted &&
            gap.EqualFanLevel is null &&
            engine.CurrentLevel == before);
    }

    private static int TestInvalidTelemetryRefused(TextWriter output)
    {
        var engine =
            new AdaptiveFanPolicyEngine(
                BuildConfig());

        var invalid =
            engine.Evaluate(
                Input(
                    DateTimeOffset.UtcNow,
                    cpuC: double.NaN,
                    cpuPowerW: 20,
                    cpuLoad: 20,
                    gpuC: 50,
                    gpuPowerW: 30,
                    gpuLoad: 30));

        return Report(
            output,
            "invalid telemetry is fail-closed at the policy boundary",
            !invalid.Accepted &&
            invalid.EqualFanLevel is null &&
            engine.CurrentLevel is null);
    }

    private static int TestInvalidConfigRefused(TextWriter output)
    {
        var refused = false;

        try
        {
            _ =
                new AdaptiveFanPolicyEngine(
                    BuildConfig() with
                    {
                        MinimumLevel = 0
                    });
        }
        catch (ArgumentOutOfRangeException)
        {
            refused = true;
        }

        return Report(
            output,
            "invalid fan envelope is rejected",
            refused);
    }

    private static AdaptiveFanPolicyConfig BuildConfig(
        int maximumUpStep = 4,
        int maximumDownStep = 2,
        int decreaseConfirmation = 3) =>
        new(
            MinimumLevel: 10,
            MaximumLevel: 50,
            MaximumUpStepPerSample: maximumUpStep,
            MaximumDownStepPerSample: maximumDownStep,
            DecreaseConfirmationSamples: decreaseConfirmation,
            DecreaseDeadbandLevels: 1.0,
            MaximumSampleGap: TimeSpan.FromSeconds(3),
            CpuTemperatureCurve:
            [
                new(40, 10),
                new(60, 20),
                new(75, 35),
                new(90, 50)
            ],
            GpuTemperatureCurve:
            [
                new(35, 10),
                new(55, 20),
                new(70, 35),
                new(85, 50)
            ],
            CpuPowerCurve:
            [
                new(0, 10),
                new(15, 10),
                new(30, 18),
                new(60, 30),
                new(100, 45)
            ],
            GpuPowerCurve:
            [
                new(0, 10),
                new(15, 10),
                new(30, 18),
                new(70, 32),
                new(120, 45)
            ],
            CpuLoadCurve:
            [
                new(0, 10),
                new(20, 10),
                new(50, 20),
                new(100, 30)
            ],
            GpuLoadCurve:
            [
                new(0, 10),
                new(20, 10),
                new(50, 20),
                new(100, 30)
            ]);

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

    private static int Report(
        TextWriter output,
        string name,
        bool pass)
    {
        output.WriteLine(
            $"{(pass ? "PASS" : "FAIL")}  {name}");

        return pass ? 0 : 1;
    }
}
