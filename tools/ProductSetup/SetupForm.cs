using System.Diagnostics;
using System.Security.Principal;

namespace VictusSetup;

internal sealed partial class SetupForm : Form
{
    private enum Page { Welcome, Options, Ready, Installing, Failed, Finished }
    private sealed record Options(bool EnableStartupAutomatic, bool DesktopShortcut);

    private static string InstallationRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl");

    private readonly Label _heading = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _message = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
    private readonly TextBox _errorDetails = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        AccessibleName = "Detalles de la instalación"
    };
    private readonly CheckBox _startupAutomatic = new() { AutoSize = true };
    private readonly CheckBox _desktopShortcut = new() { Text = "Crear un acceso directo en el escritorio", AutoSize = true };
    private readonly CheckBox _launch = new() { Text = "Abrir VictusFanControl", Checked = true, AutoSize = true };
    private readonly ProgressBar _progress = new() { Width = 550, Height = 20, Style = ProgressBarStyle.Marquee };
    private readonly Button _back = new() { Text = "&Atrás", Size = new(100, 32) };
    private readonly Button _next = new() { Size = new(110, 32) };
    private readonly Button _cancel = new() { Text = "&Cancelar", Size = new(100, 32) };
    private readonly bool _hasProfiles;
    private readonly int? _ownerPid;
    private readonly long? _ownerStart;
    private readonly Func<Options, Task<string>> _install;
    private readonly Action<string> _open;
    private Page _page;
    private string? _installedDirectory;

    internal SetupForm(string[] args) : this(args, Path.Combine(InstallationRoot, "product-profiles.json")) { }

    private SetupForm(string[] args, string profilesPath, Func<Options, Task<string>>? install = null, Action<string>? open = null)
    {
        if (args.Length != 0)
        {
            if (args.Length != 6 || args[0] != "--wait-pid" || args[2] != "--wait-start" || args[4] != "--owner-sid" ||
                !int.TryParse(args[1], out var pid) || pid <= 0 || !long.TryParse(args[3], out var ticks) || ticks <= 0)
                throw new ArgumentException("Argumentos del instalador no válidos.");
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.Value != args[5])
                throw new IOException("Abre el instalador con la misma cuenta de Windows que ejecuta VictusFanControl.");
            _ownerPid = pid;
            _ownerStart = ticks;
        }

        _install = install ?? InstallPackageAsync;
        _open = open ?? OpenInstalledApp;
        _hasProfiles = File.Exists(profilesPath);
        _startupAutomatic.Checked = !_hasProfiles;
        _startupAutomatic.Text = _hasProfiles
            ? "Activar inicio con Windows y modo Automático"
            : "Iniciar con Windows y activar Automático";

        SuspendLayout();
        Text = $"Instalación de VictusFanControl {Package.Version}";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AcceptButton = _next;
        CancelButton = _cancel;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 77));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        _heading.BackColor = SystemColors.Window;
        _heading.ForeColor = SystemColors.WindowText;
        _heading.Padding = new Padding(26, 0, 26, 0);
        _heading.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        _heading.Margin = Padding.Empty;
        layout.Controls.Add(_heading, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(26, 18, 26, 14) };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _message.AutoSize = true;
        _message.MaximumSize = new Size(550, 0);
        body.Controls.Add(_message, 0, 0);
        var choices = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        choices.Controls.AddRange([_startupAutomatic, _desktopShortcut, _progress, _launch]);
        body.Controls.Add(choices, 0, 1);
        body.Controls.Add(_errorDetails, 0, 2);
        layout.Controls.Add(body, 0, 1);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(20, 6, 20, 0) };
        footer.Controls.AddRange([_cancel, _next, _back]);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        _back.Click += (_, _) => GoBack();
        _next.Click += async (_, _) => await NextAsync();
        _cancel.Click += (_, _) => Close();
        FormClosing += (_, e) => e.Cancel = _page == Page.Installing;
        ShowPage(Page.Welcome);
        ResumeLayout(performLayout: true);
    }

    private void ShowPage(Page page, string? details = null)
    {
        _page = page;
        (_heading.Text, _message.Text) = page switch
        {
            Page.Welcome => ("Asistente de instalación de VictusFanControl",
                $"Versión {Package.Version} preliminar para HP 8C40 con BIOS F.18.\n\nSi VictusFanControl está abierto, ciérralo con «Salir» desde la bandeja. El asistente conserva tus perfiles y la versión anterior.\n\nSe requiere .NET Desktop Runtime 8 x64 y PawnIO instalado."),
            Page.Options => ("Opciones de instalación",
                _hasProfiles ? "El asistente conservará tu configuración de inicio con Windows. Puedes activar el inicio con Automático si lo deseas."
                    : "Elige si quieres iniciar VictusFanControl con Windows y activar Automático."),
            Page.Ready => ("Listo para instalar",
                $"Carpeta de versiones:\n{Path.Combine(InstallationRoot, "releases")}\n\n" +
                (_startupAutomatic.Checked ? "Inicio con Windows y Automático: activados." :
                    _hasProfiles ? "Inicio con Windows y Automático: conservar preferencias." : "Inicio con Windows y Automático: desactivados.") +
                (_desktopShortcut.Checked ? "\nAcceso directo en el escritorio." : "") + "\n\nPulsa «Instalar» para continuar."),
            Page.Installing => ("Instalando VictusFanControl", "Preparando la instalación…"),
            Page.Failed => ("Instalación detenida", "Revisa el detalle y pulsa «Reintentar»."),
            Page.Finished => ("Instalación completada", "VictusFanControl está disponible en el menú Inicio.\n\nPulsa «Finalizar» para cerrar el asistente."),
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };
        _startupAutomatic.Visible = _desktopShortcut.Visible = page == Page.Options;
        _progress.Visible = page == Page.Installing;
        _launch.Visible = page == Page.Finished;
        _errorDetails.Visible = page == Page.Failed;
        _errorDetails.Text = details ?? "";
        _back.Enabled = page is Page.Options or Page.Ready or Page.Failed;
        _cancel.Enabled = _next.Enabled = page != Page.Installing;
        _cancel.Visible = page != Page.Finished;
        _next.Text = page switch
        {
            Page.Ready => "&Instalar",
            Page.Failed => "&Reintentar",
            Page.Finished => "&Finalizar",
            _ => "&Siguiente"
        };
    }

    private void GoBack()
    {
        if (_page == Page.Options) ShowPage(Page.Welcome);
        else if (_page is Page.Ready or Page.Failed) ShowPage(Page.Options);
    }

    private async Task NextAsync()
    {
        switch (_page)
        {
            case Page.Welcome:
                ShowPage(Page.Options);
                break;
            case Page.Options:
                ShowPage(Page.Ready);
                break;
            case Page.Ready:
            case Page.Failed:
                ShowPage(Page.Installing);
                try
                {
                    _installedDirectory = await _install(new(_startupAutomatic.Checked, _desktopShortcut.Checked));
                    Environment.ExitCode = 0;
                    ShowPage(Page.Finished);
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 1;
                    ShowPage(Page.Failed, ex.Message);
                }
                break;
            case Page.Finished:
                try { if (_launch.Checked && _installedDirectory is { } installed) _open(installed); }
                catch (Exception ex)
                {
                    var message = "VictusFanControl quedó instalado. Puedes abrirlo desde el menú Inicio.\n\n" + ex.Message;
                    MessageBox.Show(this, message, "No se pudo abrir VictusFanControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                Close();
                break;
        }
    }

    private static void OpenInstalledApp(string appDirectory)
    {
        var start = new ProcessStartInfo(Path.Combine(appDirectory, "VictusFanControl.App.exe"))
        {
            WorkingDirectory = appDirectory, UseShellExecute = true
        };
        start.ArgumentList.Add("--modules-dir");
        start.ArgumentList.Add(Path.Combine(appDirectory, "modules"));
        using var process = Process.Start(start) ?? throw new IOException("Windows no abrió la aplicación.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _heading.Font.Dispose();
        base.Dispose(disposing);
    }
}
