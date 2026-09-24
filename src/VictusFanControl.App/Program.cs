using System.Diagnostics;

namespace VictusFanControl.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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

        var modulesDirectory = ResolveModulesDirectory(args);
        if (modulesDirectory is null)
        {
            MessageBox.Show(
                "PawnIO modules were not found. Run scripts\setup-pawnio-modules.ps1 from the repository first.",
                "VictusFanControl - modules not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        using var form = new MainForm(modulesDirectory);
        Application.Run(form);
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
