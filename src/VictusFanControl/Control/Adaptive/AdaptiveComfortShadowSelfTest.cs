using VictusFanControl.Hardware.Hp;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

public static class AdaptiveComfortShadowSelfTest
{
    public static int Run(TextWriter output)
    {
        var failures = 0;

        failures += TestFiveSnapshotMedian(output);
        failures += TestSustainedRiseTrend(output);
        failures += TestDuplicateAndGapDoNotFakeWindow(output);
        failures += TestPostCoolingRequiresQualifiedHeavyLoad(output);
        failures += TestPostCoolingReentryAndLifecycleReset(output);
        failures += TestCurveEditorModel(output);

        output.WriteLine();
        output.WriteLine(
            failures == 0
                ? "Adaptive comfort shadow self-test: PASS"
                : "Adaptive comfort shadow self-test: FAIL (" +
                  failures +
                  " case(s))");

        return failures == 0 ? 0 : 34;
    }

    private static int TestFiveSnapshotMedian(TextWriter output)
    {
        var filter =
            new CpuTemperatureComfortFilter(
                TimeSpan.FromSeconds(3));

        var start =
            new DateTimeOffset(
                2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        var effective =
            new[] { 60.0, 61.0, 95.0, 62.0, 63.0 };

        CpuTemperatureComfortFilterResult? result = null;

        for (var index = 0; index < effective.Length; index++)
        {
            var timestamp =
                start + TimeSpan.FromSeconds(index);

            result =
                filter.Evaluate(
                    BuildSnapshot(
                        timestamp,
                        packageC: effective[index] - 3,
                        hottestCoreC: effective[index]),
                    timestamp);
        }

        return Report(
            output,
            "five-snapshot temporal median rejects a single raw spike without spatial core averaging",
            result is
            {
                Accepted: true,
                Ready: true,
                SamplesCollected: 5,
                FilteredTemperatureC: 62.0,
                InstantaneousEffectiveTemperatureC: 63.0
            });
    }

    private static int TestSustainedRiseTrend(TextWriter output)
    {
        var filter =
            new CpuTemperatureComfortFilter(
                TimeSpan.FromSeconds(3));

        var start = DateTimeOffset.UtcNow;
        CpuTemperatureComfortFilterResult? result = null;

        for (var index = 0; index < 5; index++)
        {
            var timestamp =
                start + TimeSpan.FromSeconds(index);
            var temperature = 60 + (index * 5);

            result =
                filter.Evaluate(
                    BuildSnapshot(
                        timestamp,
                        temperature - 1,
                        temperature),
                    timestamp);
        }

        return Report(
            output,
            "sustained rise produces a positive trend while the normal curve receives the five-sample median",
            result is
            {
                Ready: true,
                FilteredTemperatureC: 70.0
            } &&
            result.TrendCPerSecond is > 4.9 and < 5.1);
    }

    private static int TestDuplicateAndGapDoNotFakeWindow(
        TextWriter output)
    {
        var filter =
            new CpuTemperatureComfortFilter(
                TimeSpan.FromSeconds(2));

        var start = DateTimeOffset.UtcNow;

        var first =
            filter.Evaluate(
                BuildSnapshot(start, 50, 55),
                start);

        var duplicate =
            filter.Evaluate(
                BuildSnapshot(start, 50, 90),
                start);

        var afterGapTimestamp =
            start + TimeSpan.FromSeconds(10);

        var afterGap =
            filter.Evaluate(
                BuildSnapshot(
                    afterGapTimestamp,
                    50,
                    60),
                afterGapTimestamp);

        return Report(
            output,
            "duplicate timestamps do not count and continuity gaps restart the five-snapshot window",
            first.Accepted &&
            !duplicate.Accepted &&
            afterGap.Accepted &&
            !afterGap.Ready &&
            afterGap.SamplesCollected == 1);
    }

    private static int TestPostCoolingRequiresQualifiedHeavyLoad(
        TextWriter output)
    {
        var machine = BuildPostCooling();
        var start = DateTimeOffset.UtcNow;

        var light =
            machine.Evaluate(
                Input(start, heavy: false, filtered: 70, trend: -1));

        _ =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(1),
                    heavy: true,
                    filtered: 75,
                    trend: 2));

        var briefEnd =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(2),
                    heavy: false,
                    filtered: 70,
                    trend: -1));

        _ =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(10),
                    heavy: true,
                    filtered: 80,
                    trend: 3));

        _ =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(13),
                    heavy: true,
                    filtered: 82,
                    trend: 2));

        var qualifiedEnd =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(14),
                    heavy: false,
                    filtered: 75,
                    trend: -0.2));

        var minimumHold =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(16),
                    heavy: false,
                    filtered: 60,
                    trend: -2));

        var exit =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(20),
                    heavy: false,
                    filtered: 60,
                    trend: -1));

        return Report(
            output,
            "post-cooling occurs only after qualified heavy load and never as a light-load express pulse",
            !light.PostCoolingActive &&
            !briefEnd.PostCoolingActive &&
            qualifiedEnd.PostCoolingActive &&
            minimumHold.PostCoolingActive &&
            !exit.PostCoolingActive &&
            exit.State == TimedPostCoolingShadowState.Idle);
    }

    private static int TestPostCoolingReentryAndLifecycleReset(
        TextWriter output)
    {
        var machine = BuildPostCooling();
        var start = DateTimeOffset.UtcNow;

        _ =
            machine.Evaluate(
                Input(start, heavy: true, filtered: 80, trend: 2));

        _ =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(3),
                    heavy: true,
                    filtered: 82,
                    trend: 1));

        _ =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(4),
                    heavy: false,
                    filtered: 75,
                    trend: -0.2));

        var reentry =
            machine.Evaluate(
                Input(
                    start + TimeSpan.FromSeconds(5),
                    heavy: true,
                    filtered: 78,
                    trend: 1));

        var lifecycleLoss =
            machine.Evaluate(
                new TimedPostCoolingShadowInput(
                    start + TimeSpan.FromSeconds(6),
                    LifecycleReady: false,
                    HeavyLoadActive: false,
                    FilteredCpuTemperatureC: 70,
                    CpuTrendCPerSecond: -1));

        return Report(
            output,
            "heavy-load re-entry cancels post-cooling and lifecycle loss resets all shadow state",
            reentry.State == TimedPostCoolingShadowState.HeavyLoad &&
            !reentry.PostCoolingActive &&
            lifecycleLoss.State == TimedPostCoolingShadowState.Idle &&
            !lifecycleLoss.PostCoolingActive);
    }

    private static int TestCurveEditorModel(TextWriter output)
    {
        var model =
            new FanCurveEditorModel(
                minimumTemperatureC: 35,
                maximumTemperatureC: 100,
                minimumLevel: 10,
                maximumLevel: 50,
                points:
                [
                    new(40, 10),
                    new(60, 20),
                    new(80, 40),
                    new(95, 50)
                ]);

        var midpoint = model.Interpolate(70);

        var moved =
            model.MovePoint(
                index: 1,
                temperatureC: 90,
                level: 48);

        return Report(
            output,
            "curve editor interpolates linearly and clamps dragged points to monotonic neighbours",
            Math.Abs(midpoint - 30) < 0.000001 &&
            moved.Input <= 79 &&
            moved.Level <= 40 &&
            model.Points[0].Level <= model.Points[1].Level);
    }

    private static TimedPostCoolingShadowStateMachine BuildPostCooling() =>
        new(
            new TimedPostCoolingShadowConfig(
                HeavyLoadQualificationDuration:
                    TimeSpan.FromSeconds(3),
                MinimumPostCoolingDuration:
                    TimeSpan.FromSeconds(5),
                MaximumPostCoolingDuration:
                    TimeSpan.FromSeconds(20),
                ExitFilteredCpuTemperatureC: 65,
                ExitTrendCPerSecond: 0,
                EqualFanLevelFloor: 25));

    private static TimedPostCoolingShadowInput Input(
        DateTimeOffset timestamp,
        bool heavy,
        double filtered,
        double trend) =>
        new(
            timestamp,
            LifecycleReady: true,
            HeavyLoadActive: heavy,
            FilteredCpuTemperatureC: filtered,
            CpuTrendCPerSecond: trend);

    private static TelemetrySnapshot BuildSnapshot(
        DateTimeOffset timestamp,
        double packageC,
        double hottestCoreC)
    {
        var cores =
            Enumerable.Range(
                    0,
                    Hp8C40TargetProfile.Instance
                        .ExpectedPhysicalCoreCount)
                .Select(index =>
                    new CpuCoreTemperatureSample(
                        CoreIndex: index,
                        LogicalProcessorIndex: index,
                        CoreType:
                            index < 6
                                ? "Performance"
                                : "Efficiency",
                        TemperatureC:
                            index == 0
                                ? hottestCoreC
                                : Math.Min(packageC, hottestCoreC)))
                .ToArray();

        return new TelemetrySnapshot(
            Timestamp: timestamp,
            CpuName: "Intel Core i7-13700H",
            CpuTemperatureC: packageC,
            CpuPackagePowerW: 45,
            CpuLoadPercent: 50,
            GpuName: Hp8C40TargetProfile.ExpectedGpuName,
            GpuTemperatureC: 65,
            GpuPowerW: 60,
            GpuLoadPercent: 80,
            CpuFanRpm: 3000,
            GpuFanRpm: 3000)
        {
            CpuCoreTemperatures = cores,
            CpuExpectedPhysicalCoreCount =
                Hp8C40TargetProfile.Instance
                    .ExpectedPhysicalCoreCount
        };
    }

    private static int Report(
        TextWriter output,
        string name,
        bool pass)
    {
        output.WriteLine(
            (pass ? "PASS  " : "FAIL  ") + name);

        return pass ? 0 : 1;
    }
}
