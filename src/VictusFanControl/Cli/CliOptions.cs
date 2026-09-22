namespace VictusFanControl.Cli;

public sealed class CliOptions
{
    public bool ShowHelp { get; private set; }
    public bool ListSensors { get; private set; }
    public int IntervalMs { get; private set; } = 1000;
    public int DurationSeconds { get; private set; }
    public string? OutputPath { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;

                case "--list-sensors":
                    options.ListSensors = true;
                    break;

                case "--interval-ms":
                    options.IntervalMs = ParsePositiveInt(ReadValue(args, ref i), "--interval-ms");
                    if (options.IntervalMs < 250)
                    {
                        throw new ArgumentException("--interval-ms must be at least 250 ms.");
                    }
                    break;

                case "--duration-seconds":
                    options.DurationSeconds = ParseNonNegativeInt(ReadValue(args, ref i), "--duration-seconds");
                    break;

                case "--output":
                    options.OutputPath = ReadValue(args, ref i);
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  VictusFanControl [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --list-sensors             Print all sensors and exit.");
        Console.WriteLine("  --interval-ms <n>          Sampling interval. Default: 1000 ms.");
        Console.WriteLine("  --duration-seconds <n>     Stop after N seconds. 0 = until Ctrl+C.");
        Console.WriteLine("  --output <path>            CSV output path.");
        Console.WriteLine("  -h, --help                 Show help.");
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[index - 1]}.");
        }

        return args[index];
    }

    private static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, out var result) || result <= 0)
        {
            throw new ArgumentException($"{name} must be a positive integer.");
        }

        return result;
    }

    private static int ParseNonNegativeInt(string value, string name)
    {
        if (!int.TryParse(value, out var result) || result < 0)
        {
            throw new ArgumentException($"{name} must be zero or a positive integer.");
        }

        return result;
    }
}
