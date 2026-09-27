using VictusFanControl.Cli;
using VictusFanControl.Control;
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
                    "8C40-COORD30" => 30,
                    "8C40-COORD32" => 32,
                    _ => null
                };

                if (!hp8C40TestLevel.HasValue)
                {
                    Console.Error.WriteLine(
                        "Integrated coordinator test refused for HP 8C40: explicit " +
                        "--coordinator-write-token 8C40-COORD30 or 8C40-COORD32 is required.");
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
