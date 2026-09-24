using VictusFanControl.Cli;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("VictusFanControl v0.2.0-dev - READ-ONLY TELEMETRY");
        Console.WriteLine("Backends: PawnIO DeviceIoControl + NVIDIA NVML. LibreHardwareMonitor is not used.");
        Console.WriteLine("No fan set-point, BIOS fan-mode or EC register writes are performed by this build.");
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
