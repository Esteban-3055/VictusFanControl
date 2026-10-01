using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>
/// P13 user-facing mode selector.
///
/// This control can request a logical mode only through
/// AdaptiveFanProductionController. With the post-M9 Manual/Automatic
/// execution gates closed, those requests return Blocked before the
/// coordinator/backend is touched.
/// </summary>
internal sealed class P13FanControlSurface : UserControl
{
    private readonly AdaptiveFanProductionController _controller;
    private readonly Action<string> _log;

    private readonly Label _modeValue = ValueLabel();
    private readonly Label _authorityValue = ValueLabel();
    private readonly Label _manualGateValue = ValueLabel();
    private readonly Label _automaticGateValue = ValueLabel();
    private readonly Label _candidateValue = ValueLabel();
    private readonly Label _statusValue = new();

    public P13FanControlSurface(
        AdaptiveFanProductionController controller,
        string targetDescription,
        Action<string> log)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Dock = DockStyle.Fill;
        AutoScroll = true;

        Controls.Add(BuildUi(targetDescription));
        RefreshState(
            "Startup mode is Firmware. Manual and Automatic remain blocked by the post-M9 execution gates.");
    }

    public AdaptiveFanProductionMode RequestedMode => _controller.Mode;

    public string ModeStatusText =>
        $"Mode: {_controller.Mode}";

    public void UpdateAuthority(FanAuthority authority)
    {
        _authorityValue.Text = authority.ToString();
    }

    private System.Windows.Forms.Control BuildUi(string targetDescription)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(
            new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Font = new Font(Font, FontStyle.Bold),
                Text =
                    "P13 user-control surface\r\n" +
                    targetDescription
            },
            0,
            0);

        var group = new GroupBox
        {
            Text = "Operating mode",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(3, 12, 3, 8)
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        var firmware = new Button
        {
            Text = "Firmware",
            AutoSize = true
        };
        firmware.Click += async (_, _) =>
            await RequestModeAsync(
                AdaptiveFanProductionMode.Firmware);

        var manual = new Button
        {
            Text = "Manual (locked)",
            AutoSize = true
        };
        manual.Click += async (_, _) =>
            await RequestModeAsync(
                AdaptiveFanProductionMode.Manual);

        var automatic = new Button
        {
            Text = "Automatic (locked)",
            AutoSize = true
        };
        automatic.Click += async (_, _) =>
            await RequestModeAsync(
                AdaptiveFanProductionMode.Automatic);

        buttons.Controls.Add(firmware);
        buttons.Controls.Add(manual);
        buttons.Controls.Add(automatic);

        var state = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 5,
            Margin = new Padding(0, 8, 0, 0)
        };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var value in new[]
                 {
                     _modeValue,
                     _authorityValue,
                     _manualGateValue,
                     _automaticGateValue,
                     _candidateValue
                 })
        {
            value.AutoSize = true;
            value.MaximumSize = new Size(650, 0);
        }

        _manualGateValue.Text =
            _controller.ManualExecutionAuthorized
                ? "OPEN"
                : "CLOSED";
        _automaticGateValue.Text =
            _controller.AutomaticExecutionAuthorized
                ? "OPEN"
                : "CLOSED";
        _candidateValue.Text =
            $"{Hp8C40AdaptiveCandidateV1.Id} — shadow-only / unvalidated";

        state.Controls.Add(new Label { Text = "Requested mode:", AutoSize = true }, 0, 0);
        state.Controls.Add(_modeValue, 1, 0);
        state.Controls.Add(new Label { Text = "Fan authority:", AutoSize = true }, 0, 1);
        state.Controls.Add(_authorityValue, 1, 1);
        state.Controls.Add(new Label { Text = "Manual execution gate:", AutoSize = true }, 0, 2);
        state.Controls.Add(_manualGateValue, 1, 2);
        state.Controls.Add(new Label { Text = "Automatic execution gate:", AutoSize = true }, 0, 3);
        state.Controls.Add(_automaticGateValue, 1, 3);
        state.Controls.Add(new Label { Text = "Candidate curve:", AutoSize = true }, 0, 4);
        state.Controls.Add(_candidateValue, 1, 4);

        content.Controls.Add(buttons, 0, 0);
        content.Controls.Add(state, 0, 1);
        group.Controls.Add(content);

        _statusValue.AutoSize = true;
        _statusValue.MaximumSize = new Size(760, 0);
        _statusValue.Margin = new Padding(3, 12, 3, 3);

        var safetyBoundary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Margin = new Padding(3, 12, 3, 3),
            Text =
                "P13.2 boundary: mode requests are wired only through AdaptiveFanProductionController. " +
                "Manual and Automatic execution remain CLOSED, so selecting them cannot touch the fan backend."
        };

        root.Controls.Add(group, 0, 1);
        root.Controls.Add(_statusValue, 0, 2);
        root.Controls.Add(safetyBoundary, 0, 3);
        return root;
    }

    private async Task RequestModeAsync(
        AdaptiveFanProductionMode mode)
    {
        try
        {
            var result =
                await _controller.SetModeAsync(
                    mode,
                    CancellationToken.None);

            RefreshState(result.Detail);
            _log(
                $"P13 mode request {mode}: action={result.Action}; " +
                $"authorized={result.ExecutionAuthorized}; authority={result.Authority}; {result.Detail}");
        }
        catch (Exception ex)
        {
            RefreshState(
                $"Mode request failed closed: {ex.Message}");
            _log(
                $"P13 mode request {mode} FAILED CLOSED: {ex}");
        }
    }

    private void RefreshState(string detail)
    {
        _modeValue.Text = _controller.Mode.ToString();
        _authorityValue.Text = _controller.Mode == AdaptiveFanProductionMode.Firmware
            ? "Firmware"
            : _authorityValue.Text;
        _statusValue.Text = detail;
    }

    private static Label ValueLabel() =>
        new()
        {
            AutoSize = true,
            Font = new Font(
                SystemFonts.DefaultFont,
                FontStyle.Bold)
        };
}
