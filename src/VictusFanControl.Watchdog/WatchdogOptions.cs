namespace VictusFanControl.Watchdog;

internal enum WatchdogRunMode
{
    GateAReadOnly,
    GateBRestoreTest,
    GateBSelfTest,
    GateCSelfTest,
    GateDService,
    M2Hp8C40ReadOnly,
    M2Hp8C40SelfTest,
    M3Hp8C40RestoreOnly,
    M3Hp8C40SelfTest
}

internal sealed record WatchdogOptions(
    string ModulesDirectory,
    string ResultPath,
    string LogDirectory,
    string ServiceName,
    WatchdogRunMode Mode,
    string? M3HandoffPath)
{
    public const string GateAServiceName = "VictusFanControlWatchdogGateA";
    public const string GateBServiceName = "VictusFanControlWatchdogGateB";
    public const string GateDServiceName = "VictusFanControlWatchdog";
    public const string M2Hp8C40ServiceName = "VictusFanControlWatchdogM2";
    public const string M3Hp8C40ServiceName = "VictusFanControlWatchdogM3";
    public const string GateBRestoreToken = "88F8-GATEB-RESTORE";

    public static WatchdogOptions Parse(string[] args)
    {
        var commonData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);

        var root = Path.Combine(commonData, "VictusFanControl", "Watchdog");
        var modulesDirectory = Path.Combine(AppContext.BaseDirectory, "modules");
        var resultPath = Path.Combine(root, "state", "gate-a.result.json");
        var logDirectory = Path.Combine(root, "logs");
        var serviceName = GateAServiceName;
        var mode = WatchdogRunMode.GateAReadOnly;
        string? gateBToken = null;
        string? m3HandoffPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--modules-dir":
                    modulesDirectory = Path.GetFullPath(ReadValue(args, ref i));
                    break;

                case "--result-path":
                    resultPath = Path.GetFullPath(ReadValue(args, ref i));
                    break;

                case "--log-dir":
                    logDirectory = Path.GetFullPath(ReadValue(args, ref i));
                    break;

                case "--service-name":
                    serviceName = ReadValue(args, ref i).Trim();
                    if (string.IsNullOrWhiteSpace(serviceName))
                    {
                        throw new ArgumentException("--service-name cannot be empty.");
                    }
                    break;

                case "--gate-b-restore":
                    RequireModeStillGateA(mode, "--gate-b-restore");
                    mode = WatchdogRunMode.GateBRestoreTest;
                    break;

                case "--gate-b-self-test":
                    RequireModeStillGateA(mode, "--gate-b-self-test");
                    mode = WatchdogRunMode.GateBSelfTest;
                    break;

                case "--gate-b-token":
                    gateBToken = ReadValue(args, ref i);
                    break;

                case "--gate-c-self-test":
                    RequireModeStillGateA(mode, "--gate-c-self-test");
                    mode = WatchdogRunMode.GateCSelfTest;
                    break;

                case "--gate-d-service":
                    RequireModeStillGateA(mode, "--gate-d-service");
                    mode = WatchdogRunMode.GateDService;
                    break;

                case "--m2-8c40-read-only":
                    RequireModeStillGateA(mode, "--m2-8c40-read-only");
                    mode = WatchdogRunMode.M2Hp8C40ReadOnly;
                    break;

                case "--m2-8c40-self-test":
                    RequireModeStillGateA(mode, "--m2-8c40-self-test");
                    mode = WatchdogRunMode.M2Hp8C40SelfTest;
                    break;

                case "--m3-8c40-restore-only":
                    RequireModeStillGateA(mode, "--m3-8c40-restore-only");
                    mode = WatchdogRunMode.M3Hp8C40RestoreOnly;
                    break;

                case "--m3-8c40-self-test":
                    RequireModeStillGateA(mode, "--m3-8c40-self-test");
                    mode = WatchdogRunMode.M3Hp8C40SelfTest;
                    break;

                case "--m3-handoff-path":
                    m3HandoffPath =
                        Path.GetFullPath(
                            ReadValue(args, ref i));
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown watchdog argument: {args[i]}");
            }
        }

        if (mode == WatchdogRunMode.GateBRestoreTest)
        {
            if (!string.Equals(
                    serviceName,
                    GateBServiceName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Gate B restore requires --service-name {GateBServiceName}.");
            }

            if (!string.Equals(
                    gateBToken,
                    GateBRestoreToken,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Gate B restore refused: explicit --gate-b-token {GateBRestoreToken} is required.");
            }
        }
        else if (gateBToken is not null)
        {
            throw new ArgumentException(
                "--gate-b-token is valid only with --gate-b-restore.");
        }

        if (mode == WatchdogRunMode.GateDService &&
            !string.Equals(
                serviceName,
                GateDServiceName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Gate D requires --service-name {GateDServiceName}.");
        }

        if (mode == WatchdogRunMode.M2Hp8C40ReadOnly &&
            !string.Equals(
                serviceName,
                M2Hp8C40ServiceName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"M2 HP 8C40 read-only service requires --service-name {M2Hp8C40ServiceName}.");
        }

        if (mode == WatchdogRunMode.M3Hp8C40RestoreOnly)
        {
            if (!string.Equals(
                    serviceName,
                    M3Hp8C40ServiceName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"M3 HP 8C40 restore-only service requires --service-name {M3Hp8C40ServiceName}.");
            }

            if (string.IsNullOrWhiteSpace(
                    m3HandoffPath))
            {
                throw new ArgumentException(
                    "M3 HP 8C40 restore-only service requires --m3-handoff-path.");
            }
        }
        else if (m3HandoffPath is not null)
        {
            throw new ArgumentException(
                "--m3-handoff-path is valid only with --m3-8c40-restore-only.");
        }

        return new WatchdogOptions(
            Path.GetFullPath(modulesDirectory),
            Path.GetFullPath(resultPath),
            Path.GetFullPath(logDirectory),
            serviceName,
            mode,
            m3HandoffPath);
    }

    private static void RequireModeStillGateA(
        WatchdogRunMode mode,
        string option)
    {
        if (mode != WatchdogRunMode.GateAReadOnly)
        {
            throw new ArgumentException(
                $"Only one watchdog execution mode may be selected; conflict at {option}.");
        }
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[index - 1]}.");
        }

        return args[index];
    }
}
