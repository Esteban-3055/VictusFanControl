namespace VictusFanControl.Watchdog;

internal sealed record GateAOptions(
    string ModulesDirectory,
    string ResultPath,
    string LogDirectory)
{
    public static GateAOptions Parse(string[] args)
    {
        var commonData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);

        var root = Path.Combine(commonData, "VictusFanControl", "Watchdog");
        var modulesDirectory = Path.Combine(AppContext.BaseDirectory, "modules");
        var resultPath = Path.Combine(root, "state", "gate-a.result.json");
        var logDirectory = Path.Combine(root, "logs");

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
            }
        }

        return new GateAOptions(
            Path.GetFullPath(modulesDirectory),
            Path.GetFullPath(resultPath),
            Path.GetFullPath(logDirectory));
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
