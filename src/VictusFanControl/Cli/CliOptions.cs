namespace VictusFanControl.Cli;

public sealed class CliOptions
{
    public bool ShowHelp { get; private set; }
    public bool ProbeBackends { get; private set; }
    public bool SafetySelfTest { get; private set; }
    public bool Probe88F8EcState { get; private set; }
    public bool ControlSelfTest { get; private set; }
    public bool BiosContractSelfTest { get; private set; }
    public bool RestoreHpAuto { get; private set; }
    public bool SkipEcSnapshots { get; private set; }
    public int HealthTestMinutes { get; private set; }
    public int IntervalMs { get; private set; } = 1000;
    public int DurationSeconds { get; private set; }
    public string? OutputPath { get; private set; }
    public string ModulesDirectory { get; private set; } = Path.Combine(Environment.CurrentDirectory, "modules");

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

                case "--probe-backends":
                case "--list-sensors":
                    options.ProbeBackends = true;
                    break;

                case "--safety-self-test":
                    options.SafetySelfTest = true;
                    break;

                case "--probe-88f8-ec-state":
                    options.Probe88F8EcState = true;
                    break;

                case "--control-self-test":
                    options.ControlSelfTest = true;
                    break;

                case "--bios-contract-self-test":
                    options.BiosContractSelfTest = true;
                    break;

                case "--restore-hp-auto":
                    options.RestoreHpAuto = true;
                    break;

                case "--skip-ec-snapshots":
                    options.SkipEcSnapshots = true;
                    break;

                case "--health-test-minutes":
                    options.HealthTestMinutes = ParsePositiveInt(
                        ReadValue(args, ref i),
                        "--health-test-minutes");
                    break;

                case "--modules-dir":
                    options.ModulesDirectory = Path.GetFullPath(ReadValue(args, ref i));
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
        Console.WriteLine("  --probe-backends           Probe PawnIO, Intel MSR/EC and NVIDIA NVML.");
        Console.WriteLine("  --list-sensors             Compatibility alias for --probe-backends.");
        Console.WriteLine("  --safety-self-test         Run synthetic SafetyGate fail-closed tests.");
        Console.WriteLine("  --probe-88f8-ec-state     Read known 88F8 fan-control EC state (read-only).");
        Console.WriteLine("  --control-self-test       Test authority/fallback coordinator with fake backend.");
        Console.WriteLine("  --bios-contract-self-test Validate the 88F8 LegacyDefault WMI request envelope.");
        Console.WriteLine("  --restore-hp-auto         EXPERIMENTAL: restore HP FanMode=LegacyDefault via WMI.");
        Console.WriteLine("  --skip-ec-snapshots       Skip before/after EC snapshots for restore test.");
        Console.WriteLine("  --health-test-minutes <n>  Strict telemetry soak test; zero misses required.");
        Console.WriteLine("  --modules-dir <path>       PawnIO signed module directory. Default: .\\modules");
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
