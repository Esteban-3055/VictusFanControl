using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using System.Runtime.InteropServices;

namespace VictusFanControl.App;

internal enum StartupRegistrationState { Enabled, Disabled, Missing, DifferentContract, Unknown }
internal sealed record StartupRegistrationResult(StartupRegistrationState State,string Detail);

internal static class WindowsStartupRegistration
{
    private static readonly object QuerySync=new();
    private static Task<string?>? _xmlRead;
    private static Task? _change;
    internal static Task PendingChange {get{lock(QuerySync)return _change??Task.CompletedTask;}}
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
                E("StopIfGoingOnBatteries", "false"), E("ExecutionTimeLimit", "PT0S"), E("AllowHardTerminate", "false"),
                new XElement(ns+"RestartOnFailure",E("Interval","PT1M"),E("Count","3")) }),
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
        var result=await QueryAsync(requireCurrentExecutable,modulesDirectory).ConfigureAwait(false);
        if(result.State==StartupRegistrationState.Unknown)throw new IOException(result.Detail);
        return result.State==StartupRegistrationState.Enabled;
    }

    internal static async Task<StartupRegistrationResult> QueryAsync(bool requireCurrentExecutable=true,string? modulesDirectory=null,Func<Task<string?>>? readFixture=null)
    {
        try
        {
        var xml=await (readFixture?.Invoke()??ReadTaskXmlAsync()).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if(xml is null)return new(StartupRegistrationState.Missing,"La tarea no está registrada.");
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        if(document.Root?.Name!=ns+"Task")throw new InvalidDataException("Respuesta de tarea no válida.");
        if(!document.Descendants(ns+"Enabled").All(e=>e.Value=="true"))return new(StartupRegistrationState.Disabled,"La tarea está deshabilitada.");
        if(!requireCurrentExecutable)return new(StartupRegistrationState.Enabled,"Intención de inicio habilitada.");
        using var identity=WindowsIdentity.GetCurrent();
        return HasCurrentContract(document,identity.User!.Value,Environment.ProcessPath!,modulesDirectory??Path.Combine(AppContext.BaseDirectory,"modules"),identity.Name)
            ?new(StartupRegistrationState.Enabled,"Tarea verificada."):new(StartupRegistrationState.DifferentContract,"La tarea requiere actualizar su configuración.");
        }
        catch(Exception error){return new(StartupRegistrationState.Unknown,"No se pudo consultar el inicio con Windows: "+error.Message);}
    }

    private static Task<string?> ReadTaskXmlAsync()
    {
        lock(QuerySync)
        {
            if(_xmlRead is null||_xmlRead.IsCompleted)_xmlRead=Task.Factory.StartNew(ReadTaskXml,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            return _xmlRead;
        }
    }
    private static string? ReadTaskXml()
    {
        object? service=null,folder=null,task=null;
        try
        {
            service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",throwOnError:true)!);
            ((dynamic)service!).Connect();folder=((dynamic)service).GetFolder("\\");
            try{task=((dynamic)folder).GetTask(TaskName);}
            catch(COMException error) when(error.HResult==unchecked((int)0x80070002)){return null;}
            return (string)((dynamic)task).Xml;
        }
        finally
        {
            foreach(var value in new[]{task,folder,service})if(value is not null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);
        }
    }

    internal static bool HasCurrentContract(XDocument document,string sid,string executable,string modules,string? currentAccountName=null)
    {
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root=document.Root;if(root?.Name!=ns+"Task")return false;
        string? Value(string group,string name)=>root.Element(ns+group)?.Element(ns+name)?.Value;
        var trigger=root.Element(ns+"Triggers")?.Elements().SingleOrDefault();
        var principal=root.Element(ns+"Principals")?.Elements().SingleOrDefault();
        var action=root.Element(ns+"Actions")?.Elements().SingleOrDefault();
        var arguments=action?.Element(ns+"Arguments")?.Value;
        // Task Scheduler exports a logon UserId as DOMAIN\account even when
        // registration used its SID. Accept only the current OS identity's
        // exact SID or canonical account, never an arbitrary matching username.
        bool SameIdentity(string? value)=>string.Equals(value,sid,StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(currentAccountName)&&string.Equals(value,currentAccountName,StringComparison.OrdinalIgnoreCase);
        return document.Descendants(ns+"Enabled").All(e=>e.Value=="true") &&
            trigger?.Name==ns+"LogonTrigger" && SameIdentity(trigger.Element(ns+"UserId")?.Value) &&
            principal?.Name==ns+"Principal" && SameIdentity(principal.Element(ns+"UserId")?.Value) &&
            principal.Element(ns+"LogonType")?.Value=="InteractiveToken" && principal.Element(ns+"RunLevel")?.Value=="HighestAvailable" &&
            action?.Name==ns+"Exec" && string.Equals(action.Element(ns+"Command")?.Value,executable,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Element(ns+"WorkingDirectory")?.Value,Path.GetDirectoryName(executable),StringComparison.OrdinalIgnoreCase) &&
            (arguments=="--modules-dir \""+modules+"\"" || arguments=="--start-minimized --modules-dir \""+modules+"\"") &&
            Value("Settings","DisallowStartIfOnBatteries")=="false" && Value("Settings","StopIfGoingOnBatteries")=="false" &&
            Value("Settings","ExecutionTimeLimit")=="PT0S" && Value("Settings","AllowHardTerminate")=="false" &&
            Value("Settings","MultipleInstancesPolicy")=="IgnoreNew"&&
            root.Element(ns+"Settings")?.Element(ns+"RestartOnFailure")?.Element(ns+"Interval")?.Value=="PT1M"&&
            root.Element(ns+"Settings")?.Element(ns+"RestartOnFailure")?.Element(ns+"Count")?.Value=="3";
    }

    internal static async Task SetEnabledAsync(bool enabled, string modulesDirectory, bool startMinimized = true)
    {
        var change=BeginChange(enabled,modulesDirectory,startMinimized);
        try{await change.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);}
        catch(TimeoutException){throw new IOException("El cambio de inicio sigue pendiente. Se conserva su respaldo; no se enviarán modificaciones paralelas.");}
    }
    internal static Task SetEnabledForInstallationAsync(bool enabled,string modulesDirectory,bool startMinimized=true)
        =>BeginChange(enabled,modulesDirectory,startMinimized);
    private static Task BeginChange(bool enabled,string modulesDirectory,bool startMinimized)
    {
        lock(QuerySync)
        {
            if(_change is {IsCompleted:false})throw new IOException("Hay una modificación de inicio pendiente; consulta su resultado antes de reintentar.");
            return _change=Task.Run(()=>SetEnabledCoreAsync(enabled,modulesDirectory,startMinimized));
        }
    }

    private static async Task SetEnabledCoreAsync(bool enabled,string modulesDirectory,bool startMinimized)
    {
        var previous=await ReadTaskXmlAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var backupDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VictusFanControl","startup-backups",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        var backupPath=Path.Combine(backupDirectory,previous is null?"previous-absent.txt":"previous-task.xml");
        using(var backup=new FileStream(backupPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        {
            await backup.WriteAsync(System.Text.Encoding.UTF8.GetBytes(previous??"Confirmed absent before change.")).ConfigureAwait(false);
            backup.Flush(flushToDisk:true);
        }
        var mutationIssued=false;
        try
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
                mutationIssued=true;
                var result = await RunAsync("/Create", "/TN", TaskName, "/XML", temporary, "/F").ConfigureAwait(false);
                if (result.ExitCode != 0) throw new IOException(result.Output);
            }
            finally { File.Delete(temporary); }
        }
        else
        {
            if(previous is null)return;
            mutationIssued=true;
            var result = await RunAsync("/Delete", "/TN", TaskName, "/F").ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException(result.Output);
        }
        if(await QueryEnabledAsync(enabled,modulesDirectory).ConfigureAwait(false)!=enabled)throw new IOException("El resultado de la tarea no coincide con el cambio solicitado.");
        }
        catch(Exception original)
        {
            if(!mutationIssued)throw;
            var rollbackPath=Path.Combine(backupDirectory,"rollback.xml");
            try
            {
                if(previous is not null)await File.WriteAllTextAsync(rollbackPath,previous,System.Text.Encoding.Unicode).ConfigureAwait(false);
                var rollback=previous is null?await RunAsync("/Delete","/TN",TaskName,"/F").ConfigureAwait(false):await RunAsync("/Create","/TN",TaskName,"/XML",rollbackPath,"/F").ConfigureAwait(false);
                var restored=await ReadTaskXmlAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if(previous is null?restored is not null:restored is null||!XNode.DeepEquals(XDocument.Parse(previous),XDocument.Parse(restored)))
                    throw new IOException("No se verificó el respaldo restaurado. "+rollback.Output);
            }
            catch(Exception failedRollback){throw new AggregateException("No se confirmó la restauración del inicio. Respaldo: "+backupDirectory,original,failedRollback);}
            throw new IOException("No se completó el cambio; configuración anterior restaurada. Respaldo: "+backupDirectory,original);
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
        // Retain one pending mutation until the actual process returns. The UI
        // wait has a separate budget and cannot issue overlapping writes.
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false)));
    }
}
