using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;

namespace VictusFanControl.App;

internal static class WindowsStartupRegistration
{
    internal static string BuildXml(string sid, string executable, string modulesDirectory, bool startMinimized = true)
    {
        if (string.IsNullOrWhiteSpace(sid) || !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(modulesDirectory) ||
            modulesDirectory.Contains('"')) throw new ArgumentException("Invalid startup identity/paths.");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("Triggers", new XElement(ns + "LogonTrigger", E("Enabled", "true"), E("UserId", sid), E("Delay", "PT10S"))),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"), E("UserId", sid),
                E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("ExecutionTimeLimit", "PT0S"), E("AllowHardTerminate", "false") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), new XElement(ns + "Exec",
                E("Command", executable), E("Arguments", (startMinimized ? "--start-minimized " : "") + "--modules-dir \"" + modulesDirectory + "\""),
                E("WorkingDirectory", Path.GetDirectoryName(executable)!))))).ToString();
    }

    private static string TaskName
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return "VictusFanControl-" + identity.User!.Value; }
    }

    internal static Task<bool> IsEnabledAsync(string? modulesDirectory = null) => QueryEnabledAsync(true,modulesDirectory);
    internal static Task<bool> IsRegisteredEnabledAsync() => QueryEnabledAsync(false);
    private static async Task<bool> QueryEnabledAsync(bool requireCurrentExecutable,string? modulesDirectory = null)
    {
        var result = await RunAsync("/Query", "/TN", TaskName, "/XML").ConfigureAwait(false);
        if (result.ExitCode != 0) return false;
        var document = XDocument.Parse(result.Output);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        if(!document.Descendants(ns+"Enabled").All(e=>e.Value=="true"))return false;
        if(!requireCurrentExecutable)return true; // Preserve an enabled user's intent when installing another version.
        using var identity=WindowsIdentity.GetCurrent();
        return HasCurrentContract(document,identity.User!.Value,Environment.ProcessPath!,modulesDirectory??Path.Combine(AppContext.BaseDirectory,"modules"));
    }

    internal static bool HasCurrentContract(XDocument document,string sid,string executable,string modules)
    {
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root=document.Root;if(root?.Name!=ns+"Task")return false;
        string? Value(string group,string name)=>root.Element(ns+group)?.Element(ns+name)?.Value;
        var trigger=root.Element(ns+"Triggers")?.Elements().SingleOrDefault();
        var principal=root.Element(ns+"Principals")?.Elements().SingleOrDefault();
        var action=root.Element(ns+"Actions")?.Elements().SingleOrDefault();
        var arguments=action?.Element(ns+"Arguments")?.Value;
        return document.Descendants(ns+"Enabled").All(e=>e.Value=="true") &&
            trigger?.Name==ns+"LogonTrigger" && trigger.Element(ns+"UserId")?.Value==sid &&
            principal?.Name==ns+"Principal" && principal.Element(ns+"UserId")?.Value==sid &&
            principal.Element(ns+"LogonType")?.Value=="InteractiveToken" && principal.Element(ns+"RunLevel")?.Value=="HighestAvailable" &&
            action?.Name==ns+"Exec" && string.Equals(action.Element(ns+"Command")?.Value,executable,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Element(ns+"WorkingDirectory")?.Value,Path.GetDirectoryName(executable),StringComparison.OrdinalIgnoreCase) &&
            (arguments=="--modules-dir \""+modules+"\"" || arguments=="--start-minimized --modules-dir \""+modules+"\"") &&
            Value("Settings","DisallowStartIfOnBatteries")=="false" && Value("Settings","StopIfGoingOnBatteries")=="false" &&
            Value("Settings","ExecutionTimeLimit")=="PT0S" && Value("Settings","AllowHardTerminate")=="false" &&
            Value("Settings","MultipleInstancesPolicy")=="IgnoreNew";
    }

    internal static async Task SetEnabledAsync(bool enabled, string modulesDirectory, bool startMinimized = true)
    {
        if (enabled)
        {
            if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "VictusFanControl.App.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Abre VictusFanControl.App.exe para configurar el inicio automático.");
            using var identity = WindowsIdentity.GetCurrent();
            var xml = BuildXml(identity.User!.Value, Environment.ProcessPath ?? throw new IOException("Executable unavailable."), modulesDirectory, startMinimized);
            var temporary = Path.Combine(Path.GetTempPath(), "vfc-startup-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                await File.WriteAllTextAsync(temporary, xml, System.Text.Encoding.Unicode).ConfigureAwait(false);
                var result = await RunAsync("/Create", "/TN", TaskName, "/XML", temporary, "/F").ConfigureAwait(false);
                if (result.ExitCode != 0) throw new IOException(result.Output);
            }
            finally { File.Delete(temporary); }
        }
        else
        {
            var result = await RunAsync("/Delete", "/TN", TaskName, "/F").ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException(result.Output);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Task Scheduler could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return (process.ExitCode, (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false)));
    }
}
