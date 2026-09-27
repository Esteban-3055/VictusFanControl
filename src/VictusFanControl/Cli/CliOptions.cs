namespace VictusFanControl.Cli;

public sealed class CliOptions
{
    public bool ShowHelp { get; private set; }
    public bool ProbeBackends { get; private set; }
    public bool SafetySelfTest { get; private set; }
    public bool Probe88F8EcState { get; private set; }
    public bool Probe88F8Setpoint { get; private set; }
    public bool ControlSelfTest { get; private set; }
    public bool BiosContractSelfTest { get; private set; }
    public bool HpBackendSelfTest { get; private set; }
    public bool RestoreHpAuto { get; private set; }
    public bool SkipEcSnapshots { get; private set; }
    public bool FirstFanWriteTest { get; private set; }
    public string? FirstFanWriteToken { get; private set; }
    public bool IntegratedCoordinatorTest { get; private set; }
    public bool CoreThermalCharacterization { get; private set; }
    public bool Hp8C40FanLevelQualification { get; private set; }
    public string? Hp8C40FanLevelQualificationToken { get; private set; }
    public bool Hp8C40UpperFanLevelQualification { get; private set; }
    public string? Hp8C40UpperFanLevelQualificationToken { get; private set; }
    public bool Hp8C40HigherFanLevelQualification { get; private set; }
    public string? Hp8C40HigherFanLevelQualificationToken { get; private set; }
    public bool Hp8C40FullFanRangeVerification { get; private set; }
    public string? Hp8C40FullFanRangeVerificationToken { get; private set; }
    public bool Hp8C40ExtendedFanRangeQualification { get; private set; }
    public string? Hp8C40ExtendedFanRangeQualificationToken { get; private set; }
    public string? IntegratedCoordinatorToken { get; private set; }
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

                case "--probe-88f8-setpoint":
                    options.Probe88F8Setpoint = true;
                    break;

                case "--control-self-test":
                    options.ControlSelfTest = true;
                    break;

                case "--bios-contract-self-test":
                    options.BiosContractSelfTest = true;
                    break;

                case "--hp-backend-self-test":
                    options.HpBackendSelfTest = true;
                    break;

                case "--restore-hp-auto":
                    options.RestoreHpAuto = true;
                    break;

                case "--skip-ec-snapshots":
                    options.SkipEcSnapshots = true;
                    break;

                case "--first-fan-write-test":
                    options.FirstFanWriteTest = true;
                    break;

                case "--write-token":
                    options.FirstFanWriteToken = ReadValue(args, ref i);
                    break;

                case "--integrated-coordinator-test":
                    options.IntegratedCoordinatorTest = true;
                    break;

                case "--core-thermal-characterization":
                    options.CoreThermalCharacterization = true;
                    break;

                case "--8c40-fan-level-qualification":
                    options.Hp8C40FanLevelQualification = true;
                    break;

                case "--8c40-qualification-token":
                    options.Hp8C40FanLevelQualificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-upper-fan-level-qualification":
                    options.Hp8C40UpperFanLevelQualification = true;
                    break;

                case "--8c40-upper-qualification-token":
                    options.Hp8C40UpperFanLevelQualificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-higher-fan-level-qualification":
                    options.Hp8C40HigherFanLevelQualification = true;
                    break;

                case "--8c40-higher-qualification-token":
                    options.Hp8C40HigherFanLevelQualificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-full-range-verification":
                    options.Hp8C40FullFanRangeVerification = true;
                    break;

                case "--8c40-full-range-token":
                    options.Hp8C40FullFanRangeVerificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-extended-range-qualification":
                    options.Hp8C40ExtendedFanRangeQualification = true;
                    break;

                case "--8c40-extended-range-token":
                    options.Hp8C40ExtendedFanRangeQualificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--coordinator-write-token":
                    options.IntegratedCoordinatorToken = ReadValue(args, ref i);
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

        var exclusiveActions =
            (options.ProbeBackends ? 1 : 0) +
            (options.SafetySelfTest ? 1 : 0) +
            (options.Probe88F8EcState ? 1 : 0) +
            (options.Probe88F8Setpoint ? 1 : 0) +
            (options.ControlSelfTest ? 1 : 0) +
            (options.BiosContractSelfTest ? 1 : 0) +
            (options.HpBackendSelfTest ? 1 : 0) +
            (options.RestoreHpAuto ? 1 : 0) +
            (options.FirstFanWriteTest ? 1 : 0) +
            (options.IntegratedCoordinatorTest ? 1 : 0) +
            (options.CoreThermalCharacterization ? 1 : 0) +
            (options.Hp8C40FanLevelQualification ? 1 : 0) +
            (options.Hp8C40UpperFanLevelQualification ? 1 : 0) +
            (options.Hp8C40HigherFanLevelQualification ? 1 : 0) +
            (options.Hp8C40FullFanRangeVerification ? 1 : 0) +
            (options.Hp8C40ExtendedFanRangeQualification ? 1 : 0) +
            (options.HealthTestMinutes > 0 ? 1 : 0);

        if (exclusiveActions > 1)
        {
            throw new ArgumentException(
                "Choose only one probe/test/write operation per invocation.");
        }

        if (options.SkipEcSnapshots && !options.RestoreHpAuto)
        {
            throw new ArgumentException(
                "--skip-ec-snapshots is valid only with --restore-hp-auto.");
        }

        if (options.FirstFanWriteToken is not null && !options.FirstFanWriteTest)
        {
            throw new ArgumentException(
                "--write-token is valid only with --first-fan-write-test.");
        }

        if (options.IntegratedCoordinatorToken is not null &&
            !options.IntegratedCoordinatorTest)
        {
            throw new ArgumentException(
                "--coordinator-write-token is valid only with --integrated-coordinator-test.");
        }

        if (options.Hp8C40FanLevelQualificationToken is not null &&
            !options.Hp8C40FanLevelQualification)
        {
            throw new ArgumentException(
                "--8c40-qualification-token is valid only with --8c40-fan-level-qualification.");
        }

        if (options.Hp8C40UpperFanLevelQualificationToken is not null &&
            !options.Hp8C40UpperFanLevelQualification)
        {
            throw new ArgumentException(
                "--8c40-upper-qualification-token is valid only with --8c40-upper-fan-level-qualification.");
        }

        if (options.Hp8C40HigherFanLevelQualificationToken is not null &&
            !options.Hp8C40HigherFanLevelQualification)
        {
            throw new ArgumentException(
                "--8c40-higher-qualification-token is valid only with --8c40-higher-fan-level-qualification.");
        }

        if (options.Hp8C40FullFanRangeVerificationToken is not null &&
            !options.Hp8C40FullFanRangeVerification)
        {
            throw new ArgumentException(
                "--8c40-full-range-token is valid only with --8c40-full-range-verification.");
        }

        if (options.Hp8C40ExtendedFanRangeQualificationToken is not null &&
            !options.Hp8C40ExtendedFanRangeQualification)
        {
            throw new ArgumentException(
                "--8c40-extended-range-token is valid only with --8c40-extended-range-qualification.");
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
        Console.WriteLine("  --probe-88f8-setpoint     Read only 88F8 ownership setpoints 0x34/0x35 (read-only).");
        Console.WriteLine("  --control-self-test       Test authority/fallback coordinator with fake backend.");
        Console.WriteLine("  --bios-contract-self-test Validate 88F8 + 8C40 BIOS/WMI request envelopes.");
        Console.WriteLine("  --hp-backend-self-test    Test the 88F8 + 8C40 backend boundaries with synthetic hardware.");
        Console.WriteLine("  --restore-hp-auto         EXPERIMENTAL: restore HP FanMode=LegacyDefault via WMI.");
        Console.WriteLine("  --skip-ec-snapshots       Skip before/after EC snapshots for restore test.");
        Console.WriteLine("  --first-fan-write-test    EXPERIMENTAL: fixed 30,30 for 15 s, monitored, then restore.");
        Console.WriteLine("  --write-token <token>     Required acknowledgement token for the first write test.");
        Console.WriteLine("  --integrated-coordinator-test  HARDWARE GATE: SafetyGate -> coordinator -> exact-target HP backend.");
        Console.WriteLine("  --core-thermal-characterization  READ-ONLY fan path: sequential per-physical-core CPU thermal characterization.");
        Console.WriteLine("  --8c40-fan-level-qualification  ACTIVE GATE: qualify equal HP 8C40 levels 30,31,32 with restore after every step.");
        Console.WriteLine("  --8c40-qualification-token <token>  Required exact token: 8C40-QUAL32.");
        Console.WriteLine("  --8c40-upper-fan-level-qualification  ACTIVE GATE: historical/resume qualification for equal HP 8C40 levels 33..36.");
        Console.WriteLine("  --8c40-upper-qualification-token <token>  Required exact token: 8C40-QUAL36.");
        Console.WriteLine("  --8c40-higher-fan-level-qualification  ACTIVE GATE: qualify equal HP 8C40 levels 37..40 with restore after every step.");
        Console.WriteLine("  --8c40-higher-qualification-token <token>  Required exact token: 8C40-QUAL40.");
        Console.WriteLine("  --8c40-full-range-verification  ACTIVE GATE: verify every equal HP 8C40 level 30..40 with restore after every step.");
        Console.WriteLine("  --8c40-full-range-token <token>  Required exact token: 8C40-VERIFY40.");
        Console.WriteLine("  --8c40-extended-range-qualification  ACTIVE GATE: guarded characterization of equal HP 8C40 levels 10..50.");
        Console.WriteLine("  --8c40-extended-range-token <token>  Required exact token: 8C40-QUAL10-50.");
        Console.WriteLine("  --coordinator-write-token <token>  Exact token: 88F8-COORD30, 8C40-COORD30, 8C40-COORD32 or 8C40-COORD36.");
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
