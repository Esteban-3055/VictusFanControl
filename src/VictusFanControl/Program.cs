using VictusFanControl.Cli;
using VictusFanControl.Telemetry;

namespace VictusFanControl;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("VictusFanControl v0.1.0 - READ-ONLY TELEMETRY");
        Console.WriteLine("No fan, EC or BIOS writes are performed by this build.");
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

        using var reader = new LibreHardwareMonitorReader();
        reader.Open();

        if (options.ListSensors)
        {
            foreach (var line in reader.GetSensorInventory())
            {
                Console.WriteLine(line);
            }

            return 0;
        }

        var outputPath = options.OutputPath ?? BuildDefaultLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

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
        return 0;
    }

    private static string BuildDefaultLogPath()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        return Path.Combine("logs", $"telemetry_{stamp}.csv");
    }
}
