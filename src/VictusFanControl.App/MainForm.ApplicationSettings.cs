namespace VictusFanControl.App;

internal sealed partial class MainForm
{
    internal bool AutomaticPerformanceLimitsRequired { get; set; }
    internal void StartInTray() => HideToTray();

    private System.Windows.Forms.Control BuildApplicationSettings(System.Windows.Forms.Control fanSettings)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var startup = new CheckBox { Text = "Iniciar con Windows (en bandeja)", AutoSize = true, Enabled = false };
        var detail = new Label { Text = "Comprobando inicio automático…", AutoSize = true, MaximumSize = new Size(720, 0) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(18, 10, 18, 0) };
        panel.Controls.Add(startup); panel.Controls.Add(detail);
        var syncing = true;
        startup.CheckedChanged += async (_, _) =>
        {
            if (syncing) return;
            var requested = startup.Checked;
            startup.Enabled = false;
            try
            {
                await WindowsStartupRegistration.SetEnabledAsync(requested, _modulesDirectory);
                detail.Text = requested ? "Inicio automático habilitado. La aplicación arranca en Firmware; los límites requieren Aplicar." : "Inicio automático deshabilitado.";
                AppendEvent(detail.Text);
            }
            catch (Exception ex)
            {
                syncing = true; startup.Checked = !requested; syncing = false;
                detail.Text = "No se pudo actualizar el inicio: " + ex.Message;
                AppendEvent(detail.Text);
            }
            finally { startup.Enabled = true; }
        };
        panel.HandleCreated += async (_, _) =>
        {
            try
            {
                syncing = true;
                startup.Checked = await WindowsStartupRegistration.IsEnabledAsync();
                detail.Text = startup.Checked ? "Inicio automático habilitado en esta ruta." : "Inicio automático deshabilitado.";
            }
            catch (Exception ex) { detail.Text = "No se pudo consultar el inicio: " + ex.Message; }
            finally { syncing = false; startup.Enabled = true; }
        };
        root.Controls.Add(panel, 0, 0); root.Controls.Add(fanSettings, 0, 1);
        return root;
    }
}
