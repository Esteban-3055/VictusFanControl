namespace VictusFanControl.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLog.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLog.Write($"UNHANDLED PROCESS EXCEPTION: {eventArgs.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Write($"UNOBSERVED TASK EXCEPTION: {eventArgs.Exception}");
            eventArgs.SetObserved();
        };

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\VictusFanControl.App",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "VictusFanControl is already running in this Windows session.",
                "VictusFanControl",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, eventArgs) =>
        {
            AppLog.Write($"UI THREAD EXCEPTION: {eventArgs.Exception}");
            MessageBox.Show(
                "VictusFanControl encountered an unexpected UI error. The safety supervisor will restore HP firmware authority if custom fan control is active. See the persistent application log for details.",
                "VictusFanControl",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        var suspendHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--suspend-custom-test",
                StringComparison.OrdinalIgnoreCase));

        var suspendHardwareTestToken = ReadOptionValue(
            args,
            "--suspend-test-token");

        var gateDHardwareTest = args.Any(
            arg => string.Equals(
                arg,
                "--gate-d-custom-test",
                StringComparison.OrdinalIgnoreCase));

        var gateDHardwareTestToken = ReadOptionValue(
            args,
            "--gate-d-test-token");

        if (suspendHardwareTest && gateDHardwareTest)
        {
            AppLog.Write(
                "Startup refused: suspend/custom and Gate D hardware-test modes are mutually exclusive.");
            Environment.ExitCode = 60;
            return;
        }

        if (suspendHardwareTest &&
            !string.Equals(
                suspendHardwareTestToken,
                "88F8-SUSPEND30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Suspend/custom hardware test refused: explicit --suspend-test-token 88F8-SUSPEND30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!suspendHardwareTest && suspendHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --suspend-test-token is valid only with --suspend-custom-test.");
            Environment.ExitCode = 60;
            return;
        }

        if (gateDHardwareTest &&
            !string.Equals(
                gateDHardwareTestToken,
                "88F8-GATED30",
                StringComparison.Ordinal))
        {
            AppLog.Write(
                "Gate D hardware test refused: explicit --gate-d-test-token 88F8-GATED30 is required.");
            Environment.ExitCode = 60;
            return;
        }

        if (!gateDHardwareTest && gateDHardwareTestToken is not null)
        {
            AppLog.Write(
                "Startup refused: --gate-d-test-token is valid only with --gate-d-custom-test.");
            Environment.ExitCode = 60;
            return;
        }

        var modulesDirectory = ResolveModulesDirectory(args);
        if (modulesDirectory is null)
        {
            AppLog.Write("Startup failed: required PawnIO modules were not found.");
            MessageBox.Show(
                "PawnIO modules were not found. Run scripts/setup-pawnio-modules.ps1 from the repository first.",
                "VictusFanControl - modules not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        AppLog.Write($"Starting GUI. Modules={modulesDirectory}");

        using var form = new MainForm(
            modulesDirectory,
            suspendHardwareTest,
            gateDHardwareTest);
        Application.Run(form);

        AppLog.Write("GUI exited.");
    }

    private static string? ReadOptionValue(string[] args, string option)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string? ResolveModulesDirectory(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--modules-dir", StringComparison.OrdinalIgnoreCase))
            {
                var explicitPath = Path.GetFullPath(args[i + 1]);
                return HasRequiredModules(explicitPath) ? explicitPath : null;
            }
        }

        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "modules"),
            Path.Combine(AppContext.BaseDirectory, "modules")
        };

        AddParentCandidates(candidates, Environment.CurrentDirectory);
        AddParentCandidates(candidates, AppContext.BaseDirectory);

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(HasRequiredModules);
    }

    private static void AddParentCandidates(List<string> candidates, string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "modules"));
        }
    }

    private static bool HasRequiredModules(string path) =>
        File.Exists(Path.Combine(path, "IntelMSR.bin")) &&
        File.Exists(Path.Combine(path, "LpcACPIEC.bin"));
}
