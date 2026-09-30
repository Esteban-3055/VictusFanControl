using VictusFanControl.Cli;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("VictusFanControl v0.4.0-dev / 8C40 port");
        Console.WriteLine("Telemetry: PawnIO Intel MSR (package + physical cores) + ACPI EC + NVIDIA NVML.");
        Console.WriteLine("Exact HP 88F8/8C40 targets are resolved fail-closed; automatic fan policy remains OFF.");
        Console.WriteLine();

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            CliOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            CliOptions.PrintHelp();
            return 0;
        }

        if (options.SafetySelfTest)
        {
            return SafetyGateSelfTest.Run(Console.Out);
        }

        if (options.ControlSelfTest)
        {
            return await FanControlCoordinatorSelfTest.RunAsync(Console.Out);
        }

        if (options.AdaptivePolicySelfTest)
        {
            return AdaptiveFanPolicySelfTest.Run(Console.Out);
        }

        if (options.BiosContractSelfTest)
        {
            var oldTarget = Hp88F8BiosContractSelfTest.Run(Console.Out);
            var newTarget = Hp8C40BiosContractSelfTest.Run(Console.Out);
            return oldTarget == 0 && newTarget == 0 ? 0 : 8;
        }

        if (options.HpBackendSelfTest)
        {
            var oldTarget = await Hp88F8FanControlBackendSelfTest.RunAsync(Console.Out);
            var newTarget = await Hp8C40FanControlBackendSelfTest.RunAsync(Console.Out);
            return oldTarget == 0 && newTarget == 0 ? 0 : 12;
        }

        if (options.Hp8C40M8PreflightProbe)
        {
            using var m8PreflightCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m8PreflightCts.Cancel();
            };

            try
            {
                return await Hp8C40M8ReadOnlyPreflightProbe.RunAsync(
                    options.ModulesDirectory,
                    Console.Out,
                    m8PreflightCts.Token);
            }
            catch (OperationCanceledException) when (m8PreflightCts.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("M8 read-only preflight probe cancelled.");
                return 130;
            }
        }
        if (options.Hp8C40M8ASelfTest)
        {
            return Hp8C40M8RepresentativeLoadQualificationTest
                .RunClassifierSelfTest(Console.Out);
        }

        if (options.Hp8C40M8CSelfTest)
        {
            return await Hp8C40M8CThermalPreemptionSelfTest
                .RunAsync(Console.Out);
        }

        if (options.Hp8C40M8ARepresentativeLoad)
        {
            using var m8aCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m8aCts.Cancel();
            };

            return await Hp8C40M8RepresentativeLoadQualificationTest.RunAsync(
                options.ModulesDirectory,
                options.Hp8C40M8AResultPath!,
                m8aCts.Token);
        }

        if (options.Hp8C40M8CPhysicalThermal)
        {
            if (!string.Equals(
                    options.Hp8C40M8CPhysicalToken,
                    Hp8C40M8CPhysicalThermalPreemptionQualificationTest.RequiredToken,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"HP 8C40 M8C physical refused: explicit --8c40-m8c-physical-token " +
                    $"{Hp8C40M8CPhysicalThermalPreemptionQualificationTest.RequiredToken} is required.");
                return 222;
            }

            if (!Hp8C40M8CPhysicalThermalPreemptionQualificationTest.TryParseCase(
                    options.Hp8C40M8CPhysicalCase!,
                    out var m8cCase))
            {
                Console.Error.WriteLine(
                    "HP 8C40 M8C physical refused: --8c40-m8c-physical-case must be cpu or gpu.");
                return 222;
            }

            using var m8cPhysicalCts =
                new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m8cPhysicalCts.Cancel();
            };

            return await Hp8C40M8CPhysicalThermalPreemptionQualificationTest.RunAsync(
                options.ModulesDirectory,
                m8cCase,
                options.Hp8C40M8CPhysicalReadyPath!,
                options.Hp8C40M8CPhysicalContinuePath!,
                options.Hp8C40M8CPhysicalResultPath!,
                m8cPhysicalCts.Token);
        }

        if (options.Hp8C40M8BWatchdogLoad)
        {
            if (!string.Equals(
                    options.Hp8C40M8BToken,
                    Hp8C40M8BWatchdogLoadQualificationTest.RequiredToken,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"HP 8C40 M8B refused: explicit --8c40-m8b-token " +
                    $"{Hp8C40M8BWatchdogLoadQualificationTest.RequiredToken} is required.");
                return 210;
            }

            using var m8bCts =
                new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m8bCts.Cancel();
            };

            return await Hp8C40M8BWatchdogLoadQualificationTest.RunAsync(
                options.ModulesDirectory,
                options.Hp8C40M8BReadyPath!,
                options.Hp8C40M8BResultPath!,
                m8bCts.Token);
        }

        if (options.RestoreHpAuto)
        {
            try
            {
                Hp88F8EcControlState? before = null;
                if (!options.SkipEcSnapshots)
                {
                    before = new Hp88F8EcControlStateProbe(options.ModulesDirectory).Read();
                    Console.WriteLine($"Before: {before}");
                }

                if (before is not null && before.Manual == 0x06)
                {
                    Console.WriteLine(
                        "Note: EC manual flag is already ON (0x06). An external HP/OMEN component may be " +
                        "maintaining the manual/countdown state; VictusFanControl will not modify that flag.");
                }

                Console.WriteLine("Sending HP BIOS/WMI fan-level release FF,FF + FanMode=LegacyDefault...");
                new Hp88F8BiosFanControl().RestoreFirmwareAuto();
                Console.WriteLine("BIOS returned success for release + LegacyDefault.");

                if (!options.SkipEcSnapshots)
                {
                    Hp88F8EcControlState? after = null;
                    var restoreStarted = DateTimeOffset.UtcNow;
                    do
                    {
                        await Task.Delay(500);
                        after = new Hp88F8EcControlStateProbe(options.ModulesDirectory).Read();
                    }
                    while ((after.CpuSetpoint != byte.MaxValue ||
                            after.GpuSetpoint != byte.MaxValue) &&
                           DateTimeOffset.UtcNow - restoreStarted < TimeSpan.FromSeconds(5));

                    Console.WriteLine($"After : {after}");

                    if (after.CpuSetpoint != byte.MaxValue ||
                        after.GpuSetpoint != byte.MaxValue)
                    {
                        Console.Error.WriteLine(
                            "HP-auto restore failed verification: EC fan setpoints did not return to FF,FF.");
                        return 11;
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"HP-auto restore failed: {ex.Message}");
                return 9;
            }
        }

        if (options.FirstFanWriteTest)
        {
            if (!string.Equals(
                    options.FirstFanWriteToken,
                    "88F8-FAN30",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "First fan-write test refused: explicit --write-token 88F8-FAN30 is required.");
                return 10;
            }

            using var writeTestCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                writeTestCts.Cancel();
            };

            return await Hp88F8FirstFanWriteTest.RunAsync(
                options.ModulesDirectory,
                writeTestCts.Token);
        }

        if (options.Hp8C40M5DWriteArmedCrashController)
        {
            if (!string.Equals(
                    options.Hp8C40M5DWriteArmedCrashToken,
                    Hp8C40M5DWriteArmedCrashTest.RequiredToken,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"HP 8C40 M5D refused: explicit --8c40-m5d-token " +
                    $"{Hp8C40M5DWriteArmedCrashTest.RequiredToken} is required.");
                return 199;
            }

            using var m5dCts =
                new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m5dCts.Cancel();
            };

            return await Hp8C40M5DWriteArmedCrashTest.RunAsync(
                options.ModulesDirectory,
                options.Hp8C40M5DReadyPath!,
                m5dCts.Token);
        }

        if (options.Hp8C40M5BWatchdogDeathController)
        {
            if (!string.Equals(
                    options.Hp8C40M5BWatchdogDeathToken,
                    Hp8C40M5BWatchdogDeathControllerTest.RequiredToken,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"HP 8C40 M5B refused: explicit --8c40-m5b-token " +
                    $"{Hp8C40M5BWatchdogDeathControllerTest.RequiredToken} is required.");
                return 189;
            }

            using var m5bCts =
                new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m5bCts.Cancel();
            };

            return await Hp8C40M5BWatchdogDeathControllerTest.RunAsync(
                options.ModulesDirectory,
                options.Hp8C40M5BReadyPath!,
                options.Hp8C40M5BLocalRestorePath!,
                options.Hp8C40M5BCompletionPath!,
                m5bCts.Token);
        }

        if (options.Hp8C40M5AControllerDeathArm)
        {
            if (!string.Equals(
                    options.Hp8C40M5AControllerDeathToken,
                    Hp8C40M5AControllerDeathArmTest.RequiredToken,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"HP 8C40 M5A refused: explicit --8c40-m5a-token " +
                    $"{Hp8C40M5AControllerDeathArmTest.RequiredToken} is required.");
                return 179;
            }

            using var m5aCts =
                new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m5aCts.Cancel();
            };

            return await Hp8C40M5AControllerDeathArmTest.RunAsync(
                options.ModulesDirectory,
                options.Hp8C40M5AReadyPath!,
                m5aCts.Token);
        }

        if (options.Hp8C40M4LeaseQualification)
        {
            var qualificationLevel =
                options.Hp8C40M4LeaseQualificationLevel ??
                throw new InvalidOperationException(
                    "M4 lease qualification level was not resolved.");

            var requiredToken =
                Hp8C40M4LeaseQualificationTest.GetRequiredToken(
                    qualificationLevel);

            var gate =
                Hp8C40M4LeaseQualificationTest.GetGateName(
                    qualificationLevel);

            if (!string.Equals(
                    options.Hp8C40M4LeaseQualificationToken,
                    requiredToken,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"HP 8C40 {gate} refused: explicit --8c40-m4-lease-token " +
                    $"{requiredToken} is required.");
                return 169;
            }

            using var m4Cts =
                new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m4Cts.Cancel();
            };

            return await Hp8C40M4LeaseQualificationTest.RunAsync(
                options.ModulesDirectory,
                qualificationLevel,
                m4Cts.Token);
        }

        if (options.Hp8C40M3Arm)
        {
            if (!string.Equals(
                    options.Hp8C40M3ArmToken,
                    Hp8C40M3ArmTest.RequiredToken,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"HP 8C40 M3 armer refused: explicit --8c40-m3-arm-token " +
                    $"{Hp8C40M3ArmTest.RequiredToken} is required.");
                return 140;
            }

            using var m3Cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                m3Cts.Cancel();
            };

            return await Hp8C40M3ArmTest.RunAsync(
                options.ModulesDirectory,
                options.Hp8C40M3HandoffPath!,
                options.Hp8C40M3ResultPath!,
                m3Cts.Token);
        }

        if (options.Hp8C40EndpointCoordinatorQualification)
        {
            if (!string.Equals(
                    options.Hp8C40EndpointCoordinatorQualificationToken,
                    "8C40-ENDPOINT10-50",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "8C40 endpoint coordinator qualification refused: explicit token 8C40-ENDPOINT10-50 is required.");
                return 121;
            }

            using var endpointCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                endpointCts.Cancel();
            };

            return await Hp8C40EndpointCoordinatorQualificationTest.RunAsync(
                options.ModulesDirectory,
                endpointCts.Token);
        }

        if (options.Hp8C40TransitionQualification)
        {
            if (!string.Equals(
                    options.Hp8C40TransitionQualificationToken,
                    "8C40-TRANSITION10-50",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "8C40 transition qualification refused: explicit token " +
                    "8C40-TRANSITION10-50 is required.");
                return 120;
            }

            using var transitionCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                transitionCts.Cancel();
            };

            return await Hp8C40TransitionQualificationTest.RunAsync(
                options.ModulesDirectory,
                transitionCts.Token);
        }

        if (options.Hp8C40ExtendedFanRangeQualification)
        {
            if (!string.Equals(
                    options.Hp8C40ExtendedFanRangeQualificationToken,
                    "8C40-QUAL10-50",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "8C40 extended fan-range qualification refused: explicit token 8C40-QUAL10-50 is required.");
                return 109;
            }

            using var extendedRangeCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                extendedRangeCts.Cancel();
            };

            return await Hp8C40ExtendedFanRangeQualificationTest.RunAsync(
                options.ModulesDirectory,
                extendedRangeCts.Token);
        }

        if (options.Hp8C40FullFanRangeVerification)
        {
            if (!string.Equals(
                    options.Hp8C40FullFanRangeVerificationToken,
                    "8C40-VERIFY40",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "8C40 full-range verification refused: explicit token 8C40-VERIFY40 is required.");
                return 100;
            }

            using var fullRangeCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                fullRangeCts.Cancel();
            };

            return await Hp8C40FullFanRangeVerificationTest.RunAsync(
                options.ModulesDirectory,
                fullRangeCts.Token);
        }

        if (options.Hp8C40HigherFanLevelQualification)
        {
            if (!string.Equals(
                    options.Hp8C40HigherFanLevelQualificationToken,
                    "8C40-QUAL40",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "8C40 higher fan-level qualification refused: explicit token 8C40-QUAL40 is required.");
                return 90;
            }

            using var higherQualificationCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                higherQualificationCts.Cancel();
            };

            return await Hp8C40HigherFanLevelQualificationTest.RunAsync(
                options.ModulesDirectory,
                higherQualificationCts.Token);
        }

        if (options.Hp8C40UpperFanLevelQualification)
        {
            if (!string.Equals(
                    options.Hp8C40UpperFanLevelQualificationToken,
                    "8C40-QUAL36",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "8C40 upper fan-level qualification refused: explicit " +
                    "--8c40-upper-qualification-token 8C40-QUAL36 is required.");
                return 80;
            }

            using var upperQualificationCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                upperQualificationCts.Cancel();
            };

            return await Hp8C40UpperFanLevelQualificationTest.RunAsync(
                options.ModulesDirectory,
                upperQualificationCts.Token);
        }

        if (options.Hp8C40FanLevelQualification)
        {
            if (!string.Equals(
                    options.Hp8C40FanLevelQualificationToken,
                    "8C40-QUAL32",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "8C40 fan-level qualification refused: explicit " +
                    "--8c40-qualification-token 8C40-QUAL32 is required.");
                return 70;
            }

            using var qualificationCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                qualificationCts.Cancel();
            };

            return await Hp8C40FanLevelQualificationTest.RunAsync(
                options.ModulesDirectory,
                qualificationCts.Token);
        }

        if (options.IntegratedCoordinatorTest)
        {
            var hardware = HardwareIdentityReader.ReadCurrent();
            var target = HpHardwareTargetResolver.Resolve(hardware, out var targetReason);

            if (target is null)
            {
                Console.Error.WriteLine(
                    $"Integrated coordinator test refused: {targetReason}");
                return 40;
            }

            int? hp8C40TestLevel = null;
            string? expectedToken = null;

            if (target.Id == Hp8C40TargetProfile.Instance.Id)
            {
                hp8C40TestLevel = options.IntegratedCoordinatorToken switch
                {
                    "8C40-COORD10" => 10,
                    "8C40-COORD30" => 30,
                    "8C40-COORD32" => 32,
                    "8C40-COORD36" => 36,
                    "8C40-COORD50" => 50,
                    _ => null
                };

                if (!hp8C40TestLevel.HasValue)
                {
                    Console.Error.WriteLine(
                        "Integrated coordinator test refused for HP 8C40: explicit " +
                        "--coordinator-write-token 8C40-COORD10, 8C40-COORD30, 8C40-COORD32, " +
                        "8C40-COORD36 or 8C40-COORD50 is required.");
                    return 40;
                }
            }
            else if (target.Id == Hp88F8TargetProfile.Instance.Id)
            {
                expectedToken = "88F8-COORD30";
                if (!string.Equals(
                        options.IntegratedCoordinatorToken,
                        expectedToken,
                        StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        $"Integrated coordinator test refused for {target.Id}: explicit " +
                        $"--coordinator-write-token {expectedToken} is required.");
                    return 40;
                }
            }
            else
            {
                Console.Error.WriteLine(
                    $"Integrated coordinator test refused for unsupported target {target.Id}.");
                return 40;
            }

            using var coordinatorTestCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                coordinatorTestCts.Cancel();
            };

            return target.Id == Hp8C40TargetProfile.Instance.Id
                ? await Hp8C40IntegratedCoordinatorTest.RunAsync(
                    options.ModulesDirectory,
                    hp8C40TestLevel!.Value,
                    coordinatorTestCts.Token)
                : await Hp88F8IntegratedCoordinatorTest.RunAsync(
                    options.ModulesDirectory,
                    coordinatorTestCts.Token);
        }

        if (options.Probe88F8Setpoint)
        {
            try
            {
                var setpoint =
                    new Hp88F8EcControlStateProbe(options.ModulesDirectory)
                        .ReadSetpoint();

                Console.WriteLine("HP 88F8 EC setpoint probe (READ-ONLY)");
                Console.WriteLine(
                    $"setpoint CPU={setpoint.CpuSetpoint} GPU={setpoint.GpuSetpoint}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"88F8 EC-setpoint probe failed: {ex.Message}");
                return 6;
            }
        }

        if (options.Probe88F8EcState)
        {
            try
            {
                var state = new Hp88F8EcControlStateProbe(options.ModulesDirectory).Read();
                Console.WriteLine("HP 88F8 EC control-state probe (READ-ONLY)");
                Console.WriteLine(state);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"88F8 EC-state probe failed: {ex.Message}");
                return 6;
            }
        }

        if (options.Probe8C40Setpoint)
        {
            try
            {
                var setpoint =
                    new Hp8C40EcControlStateProbe(options.ModulesDirectory)
                        .ReadSetpoint();

                Console.WriteLine("HP 8C40 EC setpoint probe (READ-ONLY)");
                Console.WriteLine(
                    $"setpoint CPU={setpoint.CpuSetpoint} GPU={setpoint.GpuSetpoint}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"8C40 EC-setpoint probe failed: {ex.Message}");
                return 16;
            }
        }

        using var reader = new HardwareTelemetryReader(options.ModulesDirectory);

        if (options.CoreThermalCharacterization)
        {
            using var coreThermalCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                coreThermalCts.Cancel();
            };

            try
            {
                return await CpuCoreThermalCharacterizationTest.RunAsync(
                    reader,
                    coreThermalCts.Token);
            }
            catch (OperationCanceledException) when (coreThermalCts.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("CPU core thermal characterization cancelled.");
                return 130;
            }
        }

        if (options.ProbeBackends)
        {
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.WriteLine(line);
            }

            Console.WriteLine();
            Console.WriteLine("Warming differential counters (CPU power/load)...");
            _ = reader.ReadSnapshot();
            await Task.Delay(1000);

            Console.WriteLine("One live sample:");
            var sample = reader.ReadSnapshot();
            ConsoleTelemetryPrinter.Print(sample);

            foreach (var line in reader.GetReadDiagnostics())
            {
                Console.WriteLine(line);
            }

            return reader.IsReadyForBaseline ? 0 : 3;
        }

        if (!reader.BackendsInitialized)
        {
            Console.Error.WriteLine("Required telemetry backends are not initialized.");
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.Error.WriteLine(line);
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("Run .\\scripts\\setup-pawnio-modules.ps1, then .\\scripts\\probe-backends.ps1.");
            return 3;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        if (options.HealthTestMinutes > 0)
        {
            try
            {
                return await TelemetryHealthTest.RunAsync(
                    reader,
                    options.HealthTestMinutes,
                    options.IntervalMs,
                    cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("Health test cancelled.");
                return 130;
            }
        }

        Console.WriteLine("Warming differential telemetry counters...");
        _ = reader.ReadSnapshot();
        reader.ResetHealthWindow();
        await Task.Delay(options.IntervalMs, cts.Token);

        var outputPath = options.OutputPath ?? BuildDefaultLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        await using var logger = new CsvTelemetryLogger(outputPath);
        await logger.WriteHeaderAsync(cts.Token);

        Console.WriteLine($"Logging to: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"Interval:   {options.IntervalMs} ms");
        Console.WriteLine(options.DurationSeconds > 0
            ? $"Duration:   {options.DurationSeconds} s"
            : "Duration:   until Ctrl+C");
        Console.WriteLine();

        var started = DateTimeOffset.UtcNow;

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var snapshot = reader.ReadSnapshot();
                await logger.WriteAsync(snapshot, cts.Token);
                ConsoleTelemetryPrinter.Print(snapshot);

                if (options.DurationSeconds > 0 &&
                    (DateTimeOffset.UtcNow - started).TotalSeconds >= options.DurationSeconds)
                {
                    break;
                }

                await Task.Delay(options.IntervalMs, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Normal Ctrl+C shutdown.
        }
        finally
        {
            await logger.FlushAsync(CancellationToken.None);
        }

        Console.WriteLine();
        Console.WriteLine("Capture finished.");
        foreach (var line in reader.GetHealthSummary())
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static string BuildDefaultLogPath()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        return Path.Combine("logs", $"telemetry_{stamp}.csv");
    }
}
