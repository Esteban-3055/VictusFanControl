namespace VictusFanControl.Cli;

public sealed class CliOptions
{
    public bool ShowHelp { get; private set; }
    public bool ProbeBackends { get; private set; }
    public bool SafetySelfTest { get; private set; }
    public bool Probe88F8EcState { get; private set; }
    public bool Probe88F8Setpoint { get; private set; }
    public bool Probe8C40Setpoint { get; private set; }
    public bool RaplProbe { get; private set; }
    public bool RaplSelfTest { get; private set; }
    public bool ControlSelfTest { get; private set; }
    public bool BiosContractSelfTest { get; private set; }
    public bool HpBackendSelfTest { get; private set; }
    public bool Hp8C40M8PreflightProbe { get; private set; }
    public bool Hp8C40M8ARepresentativeLoad { get; private set; }
    public bool Hp8C40M8ASelfTest { get; private set; }
    public string? Hp8C40M8AResultPath { get; private set; }
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
    public bool Hp8C40TransitionQualification { get; private set; }
    public string? Hp8C40TransitionQualificationToken { get; private set; }
    public bool Hp8C40EndpointCoordinatorQualification { get; private set; }
    public string? Hp8C40EndpointCoordinatorQualificationToken { get; private set; }
    public bool Hp8C40M3Arm { get; private set; }
    public string? Hp8C40M3ArmToken { get; private set; }
    public string? Hp8C40M3HandoffPath { get; private set; }
    public string? Hp8C40M3ResultPath { get; private set; }
    public bool Hp8C40M4LeaseQualification { get; private set; }
    public int? Hp8C40M4LeaseQualificationLevel { get; private set; }
    public string? Hp8C40M4LeaseQualificationToken { get; private set; }
    public bool Hp8C40M5AControllerDeathArm { get; private set; }
    public string? Hp8C40M5AControllerDeathToken { get; private set; }
    public string? Hp8C40M5AReadyPath { get; private set; }
    public bool Hp8C40M5BWatchdogDeathController { get; private set; }
    public string? Hp8C40M5BWatchdogDeathToken { get; private set; }
    public string? Hp8C40M5BReadyPath { get; private set; }
    public string? Hp8C40M5BLocalRestorePath { get; private set; }
    public string? Hp8C40M5BCompletionPath { get; private set; }
    public bool Hp8C40M5DWriteArmedCrashController { get; private set; }
    public string? Hp8C40M5DWriteArmedCrashToken { get; private set; }
    public string? Hp8C40M5DReadyPath { get; private set; }
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

                case "--probe-8c40-setpoint":
                    options.Probe8C40Setpoint = true;
                    break;

                case "--rapl-probe":
                    options.RaplProbe = true;
                    break;

                case "--rapl-self-test":
                    options.RaplSelfTest = true;
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

                case "--8c40-m8-preflight-probe":
                    options.Hp8C40M8PreflightProbe = true;
                    break;
                case "--8c40-m8a-representative-load":
                    options.Hp8C40M8ARepresentativeLoad = true;
                    break;

                case "--8c40-m8a-self-test":
                    options.Hp8C40M8ASelfTest = true;
                    break;

                case "--8c40-m8a-result-path":
                    options.Hp8C40M8AResultPath =
                        Path.GetFullPath(ReadValue(args, ref i));
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

                case "--8c40-transition-qualification":
                    options.Hp8C40TransitionQualification = true;
                    break;

                case "--8c40-transition-token":
                    options.Hp8C40TransitionQualificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-endpoint-coordinator-qualification":
                    options.Hp8C40EndpointCoordinatorQualification = true;
                    break;

                case "--8c40-endpoint-coordinator-token":
                    options.Hp8C40EndpointCoordinatorQualificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-m3-arm":
                    options.Hp8C40M3Arm = true;
                    break;

                case "--8c40-m3-arm-token":
                    options.Hp8C40M3ArmToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-m3-handoff-path":
                    options.Hp8C40M3HandoffPath =
                        Path.GetFullPath(ReadValue(args, ref i));
                    break;

                case "--8c40-m3-result-path":
                    options.Hp8C40M3ResultPath =
                        Path.GetFullPath(ReadValue(args, ref i));
                    break;

                case "--8c40-m4-lease10":
                    SetM4LeaseQualificationLevel(options, 10);
                    break;

                case "--8c40-m4-lease30":
                    SetM4LeaseQualificationLevel(options, 30);
                    break;

                case "--8c40-m4-lease50":
                    SetM4LeaseQualificationLevel(options, 50);
                    break;

                case "--8c40-m4-lease-token":
                    options.Hp8C40M4LeaseQualificationToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-m5a-controller-death-arm":
                    options.Hp8C40M5AControllerDeathArm = true;
                    break;

                case "--8c40-m5a-token":
                    options.Hp8C40M5AControllerDeathToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-m5a-ready-path":
                    options.Hp8C40M5AReadyPath =
                        Path.GetFullPath(
                            ReadValue(args, ref i));
                    break;

                case "--8c40-m5b-watchdog-death-controller":
                    options.Hp8C40M5BWatchdogDeathController = true;
                    break;

                case "--8c40-m5b-token":
                    options.Hp8C40M5BWatchdogDeathToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-m5b-ready-path":
                    options.Hp8C40M5BReadyPath =
                        Path.GetFullPath(
                            ReadValue(args, ref i));
                    break;

                case "--8c40-m5b-local-restore-path":
                    options.Hp8C40M5BLocalRestorePath =
                        Path.GetFullPath(
                            ReadValue(args, ref i));
                    break;

                case "--8c40-m5b-completion-path":
                    options.Hp8C40M5BCompletionPath =
                        Path.GetFullPath(
                            ReadValue(args, ref i));
                    break;

                case "--8c40-m5d-write-armed-crash-controller":
                    options.Hp8C40M5DWriteArmedCrashController = true;
                    break;

                case "--8c40-m5d-token":
                    options.Hp8C40M5DWriteArmedCrashToken =
                        ReadValue(args, ref i);
                    break;

                case "--8c40-m5d-ready-path":
                    options.Hp8C40M5DReadyPath =
                        Path.GetFullPath(
                            ReadValue(args, ref i));
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
            (options.Probe8C40Setpoint ? 1 : 0) +
            (options.RaplProbe ? 1 : 0) +
            (options.RaplSelfTest ? 1 : 0) +
            (options.ControlSelfTest ? 1 : 0) +
            (options.BiosContractSelfTest ? 1 : 0) +
            (options.HpBackendSelfTest ? 1 : 0) +
            (options.Hp8C40M8PreflightProbe ? 1 : 0) +
            (options.Hp8C40M8ARepresentativeLoad ? 1 : 0) +
            (options.Hp8C40M8ASelfTest ? 1 : 0) +
            (options.RestoreHpAuto ? 1 : 0) +
            (options.FirstFanWriteTest ? 1 : 0) +
            (options.IntegratedCoordinatorTest ? 1 : 0) +
            (options.CoreThermalCharacterization ? 1 : 0) +
            (options.Hp8C40FanLevelQualification ? 1 : 0) +
            (options.Hp8C40UpperFanLevelQualification ? 1 : 0) +
            (options.Hp8C40HigherFanLevelQualification ? 1 : 0) +
            (options.Hp8C40FullFanRangeVerification ? 1 : 0) +
            (options.Hp8C40ExtendedFanRangeQualification ? 1 : 0) +
            (options.Hp8C40TransitionQualification ? 1 : 0) +
            (options.Hp8C40EndpointCoordinatorQualification ? 1 : 0) +
            (options.Hp8C40M3Arm ? 1 : 0) +
            (options.Hp8C40M4LeaseQualification ? 1 : 0) +
            (options.Hp8C40M5AControllerDeathArm ? 1 : 0) +
            (options.Hp8C40M5BWatchdogDeathController ? 1 : 0) +
            (options.Hp8C40M5DWriteArmedCrashController ? 1 : 0) +
            (options.HealthTestMinutes > 0 ? 1 : 0);

        if (exclusiveActions > 1)
        {
            throw new ArgumentException(
                "Choose only one probe/test/write operation per invocation.");
        }

        if (options.Hp8C40M8AResultPath is not null &&
            !options.Hp8C40M8ARepresentativeLoad)
        {
            throw new ArgumentException(
                "--8c40-m8a-result-path is valid only with --8c40-m8a-representative-load.");
        }

        if (options.Hp8C40M8ARepresentativeLoad &&
            string.IsNullOrWhiteSpace(options.Hp8C40M8AResultPath))
        {
            throw new ArgumentException(
                "--8c40-m8a-representative-load requires --8c40-m8a-result-path.");
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

        if (options.Hp8C40M3ArmToken is not null &&
            !options.Hp8C40M3Arm)
        {
            throw new ArgumentException(
                "--8c40-m3-arm-token is valid only with --8c40-m3-arm.");
        }

        if ((options.Hp8C40M3HandoffPath is not null ||
             options.Hp8C40M3ResultPath is not null) &&
            !options.Hp8C40M3Arm)
        {
            throw new ArgumentException(
                "--8c40-m3-handoff-path/--8c40-m3-result-path are valid only with --8c40-m3-arm.");
        }

        if (options.Hp8C40M3Arm &&
            (string.IsNullOrWhiteSpace(options.Hp8C40M3HandoffPath) ||
             string.IsNullOrWhiteSpace(options.Hp8C40M3ResultPath)))
        {
            throw new ArgumentException(
                "--8c40-m3-arm requires both --8c40-m3-handoff-path and --8c40-m3-result-path.");
        }

        if (options.Hp8C40M4LeaseQualificationToken is not null &&
            !options.Hp8C40M4LeaseQualification)
        {
            throw new ArgumentException(
                "--8c40-m4-lease-token is valid only with --8c40-m4-lease10/30/50.");
        }

        if (options.Hp8C40M4LeaseQualification &&
            options.Hp8C40M4LeaseQualificationLevel is not (10 or 30 or 50))
        {
            throw new ArgumentException(
                "HP 8C40 M4 lease qualification requires exactly one endpoint switch: --8c40-m4-lease10, --8c40-m4-lease30 or --8c40-m4-lease50.");
        }

        if ((options.Hp8C40M5AControllerDeathToken is not null ||
             options.Hp8C40M5AReadyPath is not null) &&
            !options.Hp8C40M5AControllerDeathArm)
        {
            throw new ArgumentException(
                "--8c40-m5a-token/--8c40-m5a-ready-path are valid only with --8c40-m5a-controller-death-arm.");
        }

        if (options.Hp8C40M5AControllerDeathArm &&
            string.IsNullOrWhiteSpace(
                options.Hp8C40M5AReadyPath))
        {
            throw new ArgumentException(
                "--8c40-m5a-controller-death-arm requires --8c40-m5a-ready-path.");
        }

        if ((options.Hp8C40M5BWatchdogDeathToken is not null ||
             options.Hp8C40M5BReadyPath is not null ||
             options.Hp8C40M5BLocalRestorePath is not null ||
             options.Hp8C40M5BCompletionPath is not null) &&
            !options.Hp8C40M5BWatchdogDeathController)
        {
            throw new ArgumentException(
                "--8c40-m5b-token/ready/local-restore/completion paths are valid only with --8c40-m5b-watchdog-death-controller.");
        }

        if (options.Hp8C40M5BWatchdogDeathController &&
            (string.IsNullOrWhiteSpace(options.Hp8C40M5BReadyPath) ||
             string.IsNullOrWhiteSpace(options.Hp8C40M5BLocalRestorePath) ||
             string.IsNullOrWhiteSpace(options.Hp8C40M5BCompletionPath)))
        {
            throw new ArgumentException(
                "--8c40-m5b-watchdog-death-controller requires ready, local-restore and completion paths.");
        }

        if ((options.Hp8C40M5DWriteArmedCrashToken is not null ||
             options.Hp8C40M5DReadyPath is not null) &&
            !options.Hp8C40M5DWriteArmedCrashController)
        {
            throw new ArgumentException(
                "--8c40-m5d-token/--8c40-m5d-ready-path are valid only with --8c40-m5d-write-armed-crash-controller.");
        }

        if (options.Hp8C40M5DWriteArmedCrashController &&
            string.IsNullOrWhiteSpace(
                options.Hp8C40M5DReadyPath))
        {
            throw new ArgumentException(
                "--8c40-m5d-write-armed-crash-controller requires --8c40-m5d-ready-path.");
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
        Console.WriteLine("  --probe-8c40-setpoint     Read only 8C40 ownership setpoints 0x34/0x35 (read-only).");
        Console.WriteLine("  --rapl-probe              READ-ONLY: decode Intel RAPL 0x606/0x610/0x614 and observe 0x610 stability.");
        Console.WriteLine("  --rapl-self-test          Synthetic self-test for Intel RAPL bitfield decoding.");
        Console.WriteLine("  --control-self-test       Test authority/fallback coordinator with fake backend.");
        Console.WriteLine("  --bios-contract-self-test Validate 88F8 + 8C40 BIOS/WMI request envelopes.");
        Console.WriteLine("  --hp-backend-self-test    Test the 88F8 + 8C40 backend boundaries with synthetic hardware.");
        Console.WriteLine("  --8c40-m8-preflight-probe READ-ONLY: exact-target telemetry + SafetyGate readiness for M8.");
        Console.WriteLine("  --8c40-m8a-representative-load  READ-ONLY: observe the fixed 60 s M8A representative gaming/3D load window.");
        Console.WriteLine("  --8c40-m8a-result-path <path>   Required durable JSON evidence path for M8A.");
        Console.WriteLine("  --8c40-m8a-self-test            Synthetic self-test for M8A load/window classification.");
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
        Console.WriteLine("  --8c40-transition-qualification  ACTIVE GATE: firmware -> 10 -> 30 -> 50 -> 30 -> 10 -> firmware.");
        Console.WriteLine("  --8c40-transition-token <token>  Required exact token: 8C40-TRANSITION10-50.");
        Console.WriteLine("  --8c40-endpoint-coordinator-qualification  ACTIVE GATE: coordinator/backend qualification at equal endpoints 10 and 50.");
        Console.WriteLine("  --8c40-endpoint-coordinator-token <token>  Required exact token: 8C40-ENDPOINT10-50.");
        Console.WriteLine("  --8c40-m3-arm             ACTIVE M3 GATE: arm one VFC-owned 30/30 and wait for LocalSystem restore.");
        Console.WriteLine("  --8c40-m3-arm-token <token>  Required exact token: 8C40-M3-RESTORE30.");
        Console.WriteLine("  --8c40-m3-handoff-path <path>  Durable one-shot M3 handoff path.");
        Console.WriteLine("  --8c40-m3-result-path <path>   M3 LocalSystem service result path.");
        Console.WriteLine("  --8c40-m4-lease10          ACTIVE M4B GATE: real target-bound watchdog lease at equal 10/10.");
        Console.WriteLine("  --8c40-m4-lease30          ACTIVE M4A GATE: real target-bound watchdog lease at equal 30/30.");
        Console.WriteLine("  --8c40-m4-lease50          ACTIVE M4C GATE: real target-bound watchdog lease at equal 50/50.");
        Console.WriteLine("  --8c40-m4-lease-token <token>  Exact token matching the selected level: 8C40-M4-LEASE10/30/50.");
        Console.WriteLine("  --8c40-m5a-controller-death-arm  ACTIVE M5A CHILD: hold watchdog-owned 30/30 until parent force-kills this process.");
        Console.WriteLine("  --8c40-m5a-token <token>   Required exact token: 8C40-M5A-CONTROLLER-DEATH30.");
        Console.WriteLine("  --8c40-m5a-ready-path <path>  Durable READY marker written only after EC+tachs+watchdog OWNED acknowledgement.");
        Console.WriteLine("  --8c40-m5b-watchdog-death-controller  ACTIVE M5B CHILD: hold OWNED 30/30 and locally restore on watchdog IPC loss.");
        Console.WriteLine("  --8c40-m5b-token <token>   Required exact token: 8C40-M5B-WATCHDOG-DEATH30.");
        Console.WriteLine("  --8c40-m5b-ready-path <path>  READY marker after EC+tachs+watchdog OWNED acknowledgement.");
        Console.WriteLine("  --8c40-m5b-local-restore-path <path>  Marker written only after WATCHDOG_IPC_LOSS local FF/FF restore.");
        Console.WriteLine("  --8c40-m5b-completion-path <path>  Parent signal allowing the live controller to exit after restart recovery proof.");
        Console.WriteLine("  --8c40-m5d-write-armed-crash-controller  ACTIVE M5D CHILD: pause after real 30/30 WMI+EC+tach ACK while watchdog journal is still WRITE_ARMED.");
        Console.WriteLine("  --8c40-m5d-token <token>   Required exact token: 8C40-M5D-WRITE-ARMED-CRASH30.");
        Console.WriteLine("  --8c40-m5d-ready-path <path>  Durable marker written from the qualification hook before watchdog Commit.");
        Console.WriteLine("  --coordinator-write-token <token>  Exact token: 88F8-COORD30 or HP 8C40 production tokens 8C40-COORD10/30/32/36/50.");
        Console.WriteLine("  --health-test-minutes <n>  Strict telemetry soak test; zero misses required.");
        Console.WriteLine("  --modules-dir <path>       PawnIO signed module directory. Default: .\\modules");
        Console.WriteLine("  --interval-ms <n>          Sampling interval. Default: 1000 ms.");
        Console.WriteLine("  --duration-seconds <n>     Stop after N seconds. 0 = until Ctrl+C.");
        Console.WriteLine("  --output <path>            CSV output path.");
        Console.WriteLine("  -h, --help                 Show help.");
    }

    private static void SetM4LeaseQualificationLevel(
        CliOptions options,
        int level)
    {
        if (options.Hp8C40M4LeaseQualification)
        {
            throw new ArgumentException(
                "Choose only one HP 8C40 M4 lease endpoint per invocation.");
        }

        options.Hp8C40M4LeaseQualification = true;
        options.Hp8C40M4LeaseQualificationLevel = level;
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
