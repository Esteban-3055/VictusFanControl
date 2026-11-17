using System.Diagnostics;
using System.Globalization;
using System.Text;
using VictusFanControl.Recovery;

namespace VictusSetup;

internal sealed partial class SetupForm
{
    private async Task<string> InstallPackageAsync(Options options)
    {
        _message.Text = "Esperando el cierre de la sesión anterior…";
        await WaitForPreviousSessionAsync();

        var stagingRoot = Path.GetFullPath(Path.Combine(InstallationRoot, "setup"));
        var staging = Path.GetFullPath(Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")));
        if (!string.Equals(Path.GetDirectoryName(staging), stagingRoot, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(staging))
            throw new IOException("La carpeta de preparación no está disponible.");

        try
        {
            // The named installation lock is owned and released on the UI thread.
            using var gate = ProductRecoveryInstallationGate.EnterInstaller();
            _message.Text = "Comprobando .NET Desktop Runtime 8 x64…";
            await RequireDesktopRuntimeAsync();
            _message.Text = "Verificando el contenido del paquete…";
            var sourceHead = await Task.Run(() => Package.ExtractEmbedded(staging));
            await RecoverPreviousSessionAsync(staging);
            _message.Text = "Copiando los archivos y preparando los accesos directos…";
            await RunInstallScriptAsync(staging, options, gate);
            return Path.Combine(InstallationRoot, "releases", $"{Package.Version}-{sourceHead}", "app");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task WaitForPreviousSessionAsync()
    {
        if (_ownerPid is not int pid) return;
        Process owner;
        try { owner = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (owner)
        {
            if (owner.StartTime.ToUniversalTime().Ticks != _ownerStart)
                throw new IOException("El identificador pertenece a otro proceso. Cierra el instalador y vuelve a intentarlo.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await owner.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new IOException("VictusFanControl sigue abierto. Usa «Salir» en la bandeja y vuelve a intentar.");
            }
        }
    }

    private async Task RecoverPreviousSessionAsync(string staging)
    {
        var pending = await Task.Run(() => ProductRecoverySelection.Read());
        if (!pending.Pending) return;
        var consent = MessageBox.Show(this, "Hay sesiones anteriores pendientes de recuperación.\n\n" + pending.Summary +
            "\n\nCierra otros controladores de CPU/GPU/ventiladores y aplicaciones de VictusFanControl. Se guardarán respaldos antes de liberar. No se activará Automático durante la recuperación. Si una llamada conserva estado incierto, se detendrá la instalación y se conservará la evidencia hasta confirmar su retorno.\n\n¿Recuperar y continuar la instalación?",
            "Recuperar sesión anterior", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (consent != DialogResult.Yes)
            throw new IOException("Instalación pausada. Los registros pendientes se conservaron.");

        _message.Text = "Recuperando la sesión anterior y guardando evidencia…";
        var app = Path.Combine(staging, "app");
        var recovered = await ProductRecoveryClient.RunAsync(app, Path.Combine(app, "modules"), pending);
        if (!recovered.Succeeded)
            throw new IOException(recovered.Detail + "\n\nEvidencia: " + recovered.EvidenceDirectory);
    }

    private static async Task RunInstallScriptAsync(string staging, Options options, ProductRecoveryInstallationGate gate)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(staging, "Install-VictusFanControl.ps1"),
            "-NoOpen", "-InstallerProcessId", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add(options.EnableStartupAutomatic ? "-EnableStartupAutomatic" : "-PreserveStartupPreferences");
        if (options.DesktopShortcut) start.ArgumentList.Add("-DesktopShortcut");
        gate.HandFileToScript();
        using var process = Process.Start(start) ?? throw new IOException("No se pudo abrir el instalador interno.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var messages = await output + await error;
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(messages) ? "El instalador interno no confirmó la instalación." : messages.Trim());
    }

    private static async Task RequireDesktopRuntimeAsync()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(dotnet))
        {
            using var process = Process.Start(new ProcessStartInfo(dotnet, "--list-runtimes")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && text.Split('\n').Any(l => l.StartsWith("Microsoft.WindowsDesktop.App 8.", StringComparison.Ordinal)) &&
                text.Split('\n').Any(l => l.StartsWith("Microsoft.NETCore.App 8.", StringComparison.Ordinal))) return;
        }
        throw new IOException("Instala .NET Desktop Runtime 8 x64 y vuelve a abrir este EXE. https://dotnet.microsoft.com/download/dotnet/8.0");
    }
}
