using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

public static class AdaptiveFanPolicyShadowSelfTest
{
    public static int Run(
        TextWriter output)
    {
        var failures = 0;

        failures +=
            TestPlannerSuppressesRetransmission(
                output);

        failures +=
            TestSafeShadowRecommendation(
                output);

        failures +=
            TestCpuTemporalThermalRelease(
                output);

        failures +=
            TestGpuImmediateThermalRelease(
                output);

        failures +=
            TestDuplicateEpochReleasesNotionalCustom(
                output);

        failures +=
            TestWrongTargetNeverRecommendsCustom(
                output);

        failures +=
            TestConfigAuthorizationGuards(
                output);

        failures +=
            TestCsvReplayParser(
                output);

        failures +=
            TestEndToEndOfflineReplay(
                output);

        output.WriteLine();
        output.WriteLine(
            failures == 0
                ? "Adaptive fan policy shadow self-test: PASS"
                : $"Adaptive fan policy shadow self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 33;
    }

    private static int TestPlannerSuppressesRetransmission(
        TextWriter output)
    {
        var planner =
            new AdaptiveFanControlIntentPlanner();

        var level30 =
            new AdaptiveFanPolicyDecision(
                Accepted: true,
                EqualFanLevel: 30,
                RawDemandLevel: 29.2,
                Detail: "synthetic");

        var level34 =
            level30 with
            {
                EqualFanLevel = 34,
                RawDemandLevel = 33.1
            };

        var enter =
            planner.Plan(
                true,
                level30);

        var hold =
            planner.Plan(
                true,
                level30);

        var change =
            planner.Plan(
                true,
                level34);

        var release =
            planner.Plan(
                false,
                null);

        var firmwareHold =
            planner.Plan(
                false,
                null);

        return Report(
            output,
            "shadow planner suppresses redundant writes and models release",
            enter.Kind ==
                AdaptiveFanControlIntentKind.EnterCustomAndApply &&
            hold.Kind ==
                AdaptiveFanControlIntentKind.HoldCustom &&
            change.Kind ==
                AdaptiveFanControlIntentKind.ApplyChangedLevel &&
            release.Kind ==
                AdaptiveFanControlIntentKind.ReleaseToFirmware &&
            firmwareHold.Kind ==
                AdaptiveFanControlIntentKind.HoldFirmware);
    }

    private static int TestSafeShadowRecommendation(
        TextWriter output)
    {
        var evaluator =
            BuildEvaluator();

        var now =
            DateTimeOffset.UtcNow;

        var result =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    now,
                    cpuC: 70,
                    gpuC: 65),
                now);

        return Report(
            output,
            "safe exact-target telemetry produces shadow-only equal recommendation",
            result.SafetyPreconditionsReady &&
            result.PolicyAccepted &&
            result.RecommendedEqualLevel is >= 10 and <= 50 &&
            result.Intent.Kind ==
                AdaptiveFanControlIntentKind.EnterCustomAndApply &&
            !result.HardwareWriteCapable);
    }

    private static int TestCpuTemporalThermalRelease(
        TextWriter output)
    {
        var evaluator =
            BuildEvaluator();

        var start =
            DateTimeOffset.UtcNow;

        var initial =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    start,
                    cpuC: 70,
                    gpuC: 65),
                start);

        var firstFourRemainAdmitted =
            initial.PolicyAccepted;

        AdaptiveFanPolicyShadowEvaluation? current =
            null;

        for (var ordinal = 1;
             ordinal <= 4;
             ordinal++)
        {
            var timestamp =
                start +
                TimeSpan.FromSeconds(
                    ordinal);

            current =
                evaluator.Evaluate(
                    SystemState.Healthy,
                    BuildSnapshot(
                        timestamp,
                        cpuC: 95,
                        gpuC: 70),
                    timestamp);

            firstFourRemainAdmitted &=
                current.SafetyPreconditionsReady &&
                !current.EffectiveThermalEmergency &&
                current.PolicyAccepted;
        }

        var fifthTimestamp =
            start +
            TimeSpan.FromSeconds(5);

        var fifth =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    fifthTimestamp,
                    cpuC: 95,
                    gpuC: 70),
                fifthTimestamp);

        return Report(
            output,
            "shadow path mirrors HP 8C40 CPU 95 C x5 thermal release",
            firstFourRemainAdmitted &&
            current is not null &&
            !fifth.SafetyPreconditionsReady &&
            fifth.EffectiveThermalEmergency &&
            !fifth.PolicyAccepted &&
            fifth.Intent.Kind ==
                AdaptiveFanControlIntentKind.ReleaseToFirmware);
    }

    private static int TestGpuImmediateThermalRelease(
        TextWriter output)
    {
        var evaluator =
            BuildEvaluator();

        var start =
            DateTimeOffset.UtcNow;

        _ =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    start,
                    cpuC: 70,
                    gpuC: 65),
                start);

        var timestamp =
            start +
            TimeSpan.FromSeconds(1);

        var emergency =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    timestamp,
                    cpuC: 70,
                    gpuC: 87),
                timestamp);

        return Report(
            output,
            "shadow path mirrors immediate GPU 87 C thermal release",
            emergency.EffectiveThermalEmergency &&
            !emergency.SafetyPreconditionsReady &&
            emergency.Intent.Kind ==
                AdaptiveFanControlIntentKind.ReleaseToFirmware);
    }

    private static int TestDuplicateEpochReleasesNotionalCustom(
        TextWriter output)
    {
        var evaluator =
            BuildEvaluator();

        var timestamp =
            DateTimeOffset.UtcNow;

        var first =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    timestamp,
                    cpuC: 70,
                    gpuC: 65),
                timestamp);

        var duplicate =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    timestamp,
                    cpuC: 75,
                    gpuC: 70),
                timestamp);

        return Report(
            output,
            "duplicate telemetry epoch produces no command recommendation",
            first.PolicyAccepted &&
            !duplicate.PolicyAccepted &&
            duplicate.RecommendedEqualLevel is null &&
            duplicate.Intent.Kind ==
                AdaptiveFanControlIntentKind.ReleaseToFirmware);
    }

    private static int TestWrongTargetNeverRecommendsCustom(
        TextWriter output)
    {
        var config =
            AdaptiveFanPolicyShadowConfig.Parse(
                BuildConfigJson());

        var wrongHardware =
            BuildHardware() with
            {
                BoardProduct = "FFFF"
            };

        var evaluator =
            new AdaptiveFanPolicyShadowEvaluator(
                wrongHardware,
                config);

        var now =
            DateTimeOffset.UtcNow;

        var result =
            evaluator.Evaluate(
                SystemState.Healthy,
                BuildSnapshot(
                    now,
                    cpuC: 70,
                    gpuC: 65),
                now);

        return Report(
            output,
            "wrong hardware target remains firmware-only in shadow",
            !result.SafetyPreconditionsReady &&
            !result.PolicyAccepted &&
            result.Intent.Kind ==
                AdaptiveFanControlIntentKind.HoldFirmware);
    }

    private static int TestConfigAuthorizationGuards(
        TextWriter output)
    {
        var valid =
            false;

        var productionRejected =
            false;

        var wrongPurposeRejected =
            false;

        try
        {
            var config =
                AdaptiveFanPolicyShadowConfig.Parse(
                    BuildConfigJson());

            valid =
                config.MinimumLevel == 10 &&
                config.MaximumLevel == 50;
        }
        catch
        {
            valid = false;
        }

        try
        {
            _ =
                AdaptiveFanPolicyShadowConfig.Parse(
                    BuildConfigJson()
                        .Replace(
                            "\"authorizedForProduction\": false",
                            "\"authorizedForProduction\": true",
                            StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            productionRejected = true;
        }

        try
        {
            _ =
                AdaptiveFanPolicyShadowConfig.Parse(
                    BuildConfigJson()
                        .Replace(
                            "\"purpose\": \"shadow-only\"",
                            "\"purpose\": \"production\"",
                            StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            wrongPurposeRejected = true;
        }

        return Report(
            output,
            "shadow config requires exact target and explicit non-production authorization",
            valid &&
            productionRejected &&
            wrongPurposeRejected);
    }

    private static int TestCsvReplayParser(
        TextWriter output)
    {
        const string header =
            "timestamp_utc,cpu_name,cpu_package_temp_c,cpu_core_max_temp_c," +
            "cpu_core_avg_temp_c,cpu_core_temps_c,cpu_package_power_w,cpu_load_pct," +
            "gpu_name,gpu_temp_c,gpu_power_w,gpu_load_pct,cpu_fan_rpm,gpu_fan_rpm";

        var coreText =
            string.Join(
                "|",
                Enumerable.Range(
                        0,
                        Hp8C40TargetProfile.Instance
                            .ExpectedPhysicalCoreCount)
                    .Select(index =>
                        $"C{index}:Performance:70.0"));

        const char quote = '"';

        var line =
            $"2026-09-30T00:00:00.0000000+00:00,{quote}Intel, CPU{quote},70,70,70," +
            $"{quote}{coreText}{quote},45,50,{quote}{Hp8C40TargetProfile.ExpectedGpuName}{quote}," +
            "65,60,80,3000,3000";

        var snapshot =
            AdaptiveFanPolicyShadowReplay
                .ParseSnapshotForSelfTest(
                    header,
                    line);

        return Report(
            output,
            "existing telemetry CSV format can be replayed deterministically",
            snapshot.IsComplete &&
            snapshot.CpuName == "Intel, CPU" &&
            snapshot.CpuCoreTemperatures.Count ==
                Hp8C40TargetProfile.Instance
                    .ExpectedPhysicalCoreCount &&
            snapshot.GpuName ==
                Hp8C40TargetProfile.ExpectedGpuName);
    }

    private static int TestEndToEndOfflineReplay(
        TextWriter output)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "VictusFanControl-adaptive-shadow-" +
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(
            root);

        try
        {
            var configPath =
                Path.Combine(
                    root,
                    "shadow.json");

            var inputPath =
                Path.Combine(
                    root,
                    "telemetry.csv");

            var outputPath =
                Path.Combine(
                    root,
                    "shadow-output.csv");

            File.WriteAllText(
                configPath,
                BuildConfigJson());

            const string header =
                "timestamp_utc,cpu_name,cpu_package_temp_c,cpu_core_max_temp_c," +
                "cpu_core_avg_temp_c,cpu_core_temps_c,cpu_package_power_w,cpu_load_pct," +
                "gpu_name,gpu_temp_c,gpu_power_w,gpu_load_pct,cpu_fan_rpm,gpu_fan_rpm";

            var coreText =
                string.Join(
                    "|",
                    Enumerable.Range(
                            0,
                            Hp8C40TargetProfile.Instance
                                .ExpectedPhysicalCoreCount)
                        .Select(index =>
                            $"C{index}:Performance:70.0"));

            const char quote = '"';

            string Row(
                DateTimeOffset timestamp,
                int cpuTemperature,
                int gpuTemperature) =>
                $"{timestamp:O},{quote}Intel, CPU{quote},{cpuTemperature}," +
                $"{cpuTemperature},{cpuTemperature},{quote}{coreText}{quote}," +
                $"45,50,{quote}{Hp8C40TargetProfile.ExpectedGpuName}{quote}," +
                $"{gpuTemperature},60,80,3000,3000";

            File.WriteAllLines(
                inputPath,
                [
                    header,
                    Row(
                        new DateTimeOffset(
                            2026,
                            9,
                            30,
                            0,
                            0,
                            0,
                            TimeSpan.Zero),
                        70,
                        65),
                    Row(
                        new DateTimeOffset(
                            2026,
                            9,
                            30,
                            0,
                            0,
                            1,
                            TimeSpan.Zero),
                        72,
                        66)
                ]);

            using var console =
                new StringWriter();

            var exitCode =
                AdaptiveFanPolicyShadowReplay.RunAsync(
                        configPath,
                        inputPath,
                        outputPath,
                        console,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            var replay =
                File.Exists(outputPath)
                    ? File.ReadAllText(outputPath)
                    : string.Empty;

            var log =
                console.ToString();

            return Report(
                output,
                "offline shadow replay runs end-to-end without hardware authority",
                exitCode == 0 &&
                replay.Contains(
                    "EnterCustomAndApply",
                    StringComparison.Ordinal) &&
                replay.Contains(
                    "HoldCustom",
                    StringComparison.Ordinal) &&
                log.Contains(
                    "hardware writes       : 0",
                    StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
            catch
            {
                // A temporary-file cleanup failure must not change the
                // functional replay assertion.
            }
        }
    }

    private static AdaptiveFanPolicyShadowEvaluator
        BuildEvaluator() =>
        new(
            BuildHardware(),
            AdaptiveFanPolicyShadowConfig.Parse(
                BuildConfigJson()));

    private static HardwareIdentity BuildHardware() =>
        new(
            Hp8C40TargetProfile.BoardManufacturer,
            Hp8C40TargetProfile.BoardProduct,
            Hp8C40TargetProfile.BoardVersion,
            Hp8C40TargetProfile.SystemManufacturer,
            Hp8C40TargetProfile.SystemProductName,
            $"{Hp8C40TargetProfile.SystemSkuPrefix}#AKH",
            Hp8C40TargetProfile.ValidatedBiosVersion);

    private static TelemetrySnapshot BuildSnapshot(
        DateTimeOffset timestamp,
        double cpuC,
        double gpuC)
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
                        TemperatureC: cpuC))
                .ToArray();

        return new TelemetrySnapshot(
            Timestamp: timestamp,
            CpuName: "Intel Core i7-13700H",
            CpuTemperatureC: cpuC,
            CpuPackagePowerW: 45,
            CpuLoadPercent: 50,
            GpuName:
                Hp8C40TargetProfile.ExpectedGpuName,
            GpuTemperatureC: gpuC,
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

    private static string BuildConfigJson() =>
        """
        {
          "schemaVersion": 1,
          "targetProfileId": "HP-8C40-9D0R1LA-F18",
          "purpose": "shadow-only",
          "authorizedForProduction": false,
          "minimumLevel": 10,
          "maximumLevel": 50,
          "maximumUpStepPerSample": 4,
          "maximumDownStepPerSample": 2,
          "decreaseConfirmationSamples": 3,
          "decreaseDeadbandLevels": 1.0,
          "maximumSampleGapSeconds": 3.0,
          "cpuTemperatureCurve": [
            { "input": 40, "level": 10 },
            { "input": 60, "level": 20 },
            { "input": 75, "level": 35 },
            { "input": 90, "level": 50 }
          ],
          "gpuTemperatureCurve": [
            { "input": 35, "level": 10 },
            { "input": 55, "level": 20 },
            { "input": 70, "level": 35 },
            { "input": 85, "level": 50 }
          ],
          "cpuPowerCurve": [
            { "input": 0, "level": 10 },
            { "input": 15, "level": 10 },
            { "input": 30, "level": 18 },
            { "input": 60, "level": 30 },
            { "input": 100, "level": 45 }
          ],
          "gpuPowerCurve": [
            { "input": 0, "level": 10 },
            { "input": 15, "level": 10 },
            { "input": 30, "level": 18 },
            { "input": 70, "level": 32 },
            { "input": 120, "level": 45 }
          ],
          "cpuLoadCurve": [
            { "input": 0, "level": 10 },
            { "input": 20, "level": 10 },
            { "input": 50, "level": 20 },
            { "input": 100, "level": 30 }
          ],
          "gpuLoadCurve": [
            { "input": 0, "level": 10 },
            { "input": 20, "level": 10 },
            { "input": 50, "level": 20 },
            { "input": 100, "level": 30 }
          ]
        }
        """;

    private static int Report(
        TextWriter output,
        string name,
        bool pass)
    {
        output.WriteLine(
            $"{(pass ? "PASS" : "FAIL")}  {name}");

        return pass
            ? 0
            : 1;
    }
}
