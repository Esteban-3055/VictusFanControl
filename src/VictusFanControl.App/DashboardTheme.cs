using System.Drawing.Drawing2D;

namespace VictusFanControl.App;

internal static class DashboardTheme
{
    internal static readonly Color Background = Color.FromArgb(18,18,21);
    internal static readonly Color Surface = Color.FromArgb(29,29,34);
    internal static readonly Color Field = Color.FromArgb(40,40,47);
    internal static readonly Color Text = Color.FromArgb(233,233,240);
    internal static readonly Color Muted = Color.FromArgb(163,166,180);
    internal static readonly Color Accent = Color.FromArgb(227,69,154);
    internal static readonly Color Cyan = Color.FromArgb(50,200,222);
    internal static void Apply(System.Windows.Forms.Control root)
    {
        root.BackColor = root is TextBoxBase or NumericUpDown or ComboBox or ListBox ? Field : Surface;
        root.ForeColor = Text;
        if (root is Form) root.BackColor = Background;
        if (root is Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(74,74,84);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56,37,53);
            b.Padding = new Padding(12,6,12,6);
            b.MinimumSize = new Size(85,36);
        }
        if (root is GroupBox g) g.Padding = new Padding(16,24,16,14);
        if (root is DataGridView grid)
        {
            grid.BackgroundColor = Surface;
            grid.DefaultCellStyle.BackColor = Field;
            grid.DefaultCellStyle.ForeColor = Text;
        }
        foreach (System.Windows.Forms.Control child in root.Controls) Apply(child);
        if (root is DashboardShell shell) shell.RefreshSelection();
    }
}

/// <summary>Real WinForms navigation; switching pages does not change hardware mode.</summary>
internal sealed class DashboardShell : UserControl
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(14) };
    private readonly List<Button> _navigation = [];
    private readonly List<System.Windows.Forms.Control> _pages = [];
    private Button? _selected;
    internal DashboardShell(params (string Title, System.Windows.Forms.Control View)[] pages)
    {
        Dock = DockStyle.Fill;
        Font = new Font("Segoe UI", 10);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute,72));
        root.RowStyles.Add(new(SizeType.Absolute,60));
        root.RowStyles.Add(new(SizeType.Percent,100));
        var heading = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24,16,24,10) };
        heading.Controls.Add(new Label { Text = "VICTUS  /  FAN CONTROL", AutoSize = true,
            Font = new Font("Segoe UI",16,FontStyle.Bold), Location = new Point(24,17) });
        var badge = new Label { Text = "CPU + GPU  ·  TELEMETRÍA EN VIVO", Dock = DockStyle.Right,
            AutoSize = false, Width = 330, TextAlign = ContentAlignment.MiddleRight, ForeColor = DashboardTheme.Muted };
        heading.Controls.Add(badge);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18,6,0,6), WrapContents = false };
        foreach (var page in pages)
        {
            _pages.Add(page.View);
            var button = new Button { Text = page.Title, Width = 155, Height = 42,
                AccessibleName = "Navegación: " + page.Title };
            _navigation.Add(button);
            button.Click += (_,_) => Select(page.View,button);
            bar.Controls.Add(button);
        }
        heading.Controls.Add(new AccentStrip { Dock = DockStyle.Bottom, Height = 3 });
        root.Controls.Add(heading,0,0);root.Controls.Add(bar,0,1);root.Controls.Add(_content,0,2);
        Controls.Add(root);
        DashboardTheme.Apply(this);
        if (pages.Length > 0) Select(pages[0].View,_navigation[0]);
    }
    private void Select(System.Windows.Forms.Control view, Button selected)
    {
        _content.Controls.Clear(); // Detach, do not dispose pages or their observers.
        view.Dock = DockStyle.Fill; _content.Controls.Add(view);
        DashboardTheme.Apply(view);
        _selected = selected;
        RefreshSelection();
    }
    internal void RefreshSelection()
    {
        foreach (var b in _navigation)
        {
            b.BackColor = b == _selected ? Color.FromArgb(58,36,52) : DashboardTheme.Surface;
            b.FlatAppearance.BorderColor = b == _selected ? DashboardTheme.Accent : Color.FromArgb(55,55,64);
            b.ForeColor = b == _selected ? DashboardTheme.Text : DashboardTheme.Muted;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var page in _pages) page.Dispose();
        base.Dispose(disposing);
    }
    private sealed class AccentStrip : System.Windows.Forms.Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) return;
            using var brush = new LinearGradientBrush(ClientRectangle, Color.OrangeRed, DashboardTheme.Accent, 0f);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }
}
