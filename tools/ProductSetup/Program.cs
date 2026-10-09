using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;

namespace VictusSetup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" })) { Environment.ExitCode = SelfTest.Run(); return; }
        ApplicationConfiguration.Initialize();
        try { Application.Run(new SetupForm(args)); }
        catch (Exception ex) { Environment.ExitCode = 1; MessageBox.Show(ex.Message, "Instalación no completada"); }
    }
}

internal sealed class SetupForm : Form
{
    private readonly Label _status = new() { AutoSize = false, Left = 25, Top = 65, Width = 550, Height = 160 };
    private readonly Button _install = new() { Text = "Instalar / actualizar", Left = 320, Top = 250, Width = 180, Height = 38 };
    private readonly Button _cancel = new() { Text = "Cerrar", Left = 510, Top = 250, Width = 75, Height = 38 };
    private bool _working;
    private readonly int? _ownerPid;
    private readonly long? _ownerStart;
    internal SetupForm(string[] args)
    {
        if (args.Length != 0)
        {
            if (args.Length != 6 || args[4] != "--owner-sid" || args[0] != "--wait-pid" || args[2] != "--wait-start" ||
                !int.TryParse(args[1], out var pid) || pid <= 0 || !long.TryParse(args[3], out var ticks) || ticks <= 0)
                throw new ArgumentException("Argumentos del instalador no válidos.");
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.Value != args[5]) throw new IOException("Actualiza con la misma cuenta de Windows. No uses las credenciales de otra cuenta para elevar el instalador.");
            _ownerPid = pid; _ownerStart = ticks;
        }
        Text = "VictusFanControl v" + Package.Version + " · Instalador";
        ClientSize = new(610, 315); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Text = "Instalar VictusFanControl", Left = 25, Top = 20, AutoSize = true, Font = new Font(Font.FontFamily, 16, FontStyle.Bold) });
        _status.Text = "Equipo: HP 8C40 / BIOS F.18.\n\nCierra VictusFanControl desde la bandeja. Se conservarán tus perfiles, registros y preferencias de inicio.\n\nPrimera instalación: inicio con Windows, minimizado y Automático. Se requiere .NET Desktop Runtime 8 x64 y PawnIO instalado. Usa tu misma cuenta de Windows.";
        Controls.AddRange(new Control[] { _status, _install, _cancel });
        _cancel.Click += (_, _) => Close(); _install.Click += async (_, _) => await InstallAsync();
        FormClosing += (_, e) => { if (_working) e.Cancel = true; };
    }
    private async Task InstallAsync()
    {
        if (_working) return;
        _working = true; _install.Enabled = _cancel.Enabled = false;
        string? directory = null;
        try
        {
            _status.Text = "Esperando el cierre normal de la sesión anterior…";
            if (_ownerPid is int pid)
            {
                Process? owner = null;
                try { owner = Process.GetProcessById(pid); } catch (ArgumentException) { }
                if (owner is not null)
                {
                    using (owner)
                    {
                        if (owner.StartTime.ToUniversalTime().Ticks != _ownerStart) throw new IOException("El identificador pertenece a otro proceso. Cierra el instalador y vuelve a intentarlo.");
                        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        await owner.WaitForExitAsync(deadline.Token);
                    }
                }
            }
            await RequireDesktopRuntimeAsync();
            _status.Text = "Verificando el contenido e instalando…";
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "setup", Guid.NewGuid().ToString("N"));
            var assembly = Assembly.GetExecutingAssembly();
            using var payload = assembly.GetManifestResourceStream("payload.zip") ?? throw new IOException("Este instalador no contiene un paquete de release.");
            using var digest = new StreamReader(assembly.GetManifestResourceStream("payload.sha256") ?? throw new IOException("Falta el SHA-256 del paquete."));
            var expected = (await digest.ReadToEndAsync()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() != expected) throw new InvalidDataException("SHA-256 del paquete incorrecto.");
            payload.Position = 0;
            await Task.Run(() => Package.Extract(payload, directory));
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(directory, "Install-VictusFanControl.ps1"), "-NoOpen", "-InstallerProcessId", Environment.ProcessId.ToString() }) start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new IOException("No se pudo abrir el instalador interno.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var message = (await output) + (await error);
            if (process.ExitCode != 0) throw new IOException(message);
            _status.Text = "Instalación completada.\n\nAbre VictusFanControl desde el menú Inicio. Tus preferencias anteriores se conservaron.\n\n" + message;
            _install.Visible = false;
        }
        catch (Exception ex) { Environment.ExitCode = 1; _status.Text = "Instalación no completada.\n\n" + ex.Message; MessageBox.Show(this, ex.Message, "Instalación no completada", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _working = false; _install.Enabled = _cancel.Enabled = true;
            if (directory is not null && Directory.Exists(directory)) { try { Directory.Delete(directory, true); } catch { } }
        }
    }
    private static async Task RequireDesktopRuntimeAsync()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(dotnet))
        {
            using var process = Process.Start(new ProcessStartInfo(dotnet, "--list-runtimes") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var text = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
            if (process.ExitCode == 0 && text.Split('\n').Any(l => l.StartsWith("Microsoft.WindowsDesktop.App 8.", StringComparison.Ordinal)) &&
                text.Split('\n').Any(l => l.StartsWith("Microsoft.NETCore.App 8.", StringComparison.Ordinal))) return;
        }
        throw new IOException("Instala .NET Desktop Runtime 8 x64 y vuelve a abrir este EXE: https://dotnet.microsoft.com/download/dotnet/8.0");
    }
}
