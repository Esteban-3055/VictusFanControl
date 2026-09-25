namespace VictusFanControl.Watchdog;

internal enum WatchdogRunMode
{
    GateAReadOnly,
    GateBRestoreTest,
    GateBSelfTest
}

internal sealed record WatchdogOptions(
    string ModulesDirectory,
    string ResultPath,
    string LogDirectory,
    string ServiceName,
    WatchdogRunMode Mode)
{
    public const string GateAServiceName = "VictusFanControlWatchdogGateA";
    public const string GateBServiceName = "VictusFanControlWatchdogGateB";
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

        return new WatchdogOptions(
            Path.GetFullPath(modulesDirectory),
            Path.GetFullPath(resultPath),
            Path.GetFullPath(logDirectory),
            serviceName,
            mode);
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
