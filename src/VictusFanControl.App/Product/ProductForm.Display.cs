using System.Runtime.InteropServices;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly ProductWindowLayout _windowLayout = new();
    private Func<ProductDisplayMetrics>? _displayMetricsInFixture;
    private bool _displayRefreshQueued, _applyingDisplayLayout;
    private FormWindowState _displayWindowState;
    private ProductDisplayMetrics? _lastDisplay;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var display = ReadDisplayMetrics();
        // Windows may already have clamped the opening window to a smaller desktop.
        // Keep the design reference, rather than treating that automatic clamp as a user resize.
        var dpiScale = display.Dpi / 96d;
        var size = new Size((int)Math.Round(DesignWindowSize.Width * dpiScale), (int)Math.Round(DesignWindowSize.Height * dpiScale));
        var reference = new Rectangle(Bounds.Left + (Bounds.Width - size.Width) / 2,
            Bounds.Top + (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        _windowLayout.Capture(reference, display, initial: true);
        RefreshDisplayLayout("open");
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // WinForms applies its suggested bounds after this callback. Apply our final
        // placement on the UI queue, once all DPI/resolution messages have completed.
        QueueDisplayRefresh("dpi");
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_displayWindowState == WindowState) return;
        _displayWindowState = WindowState;
        QueueDisplayRefresh("window-state");
    }
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!_applyingDisplayLayout && _windowLayout.Initialized && WindowState == FormWindowState.Normal)
            _windowLayout.Capture(Bounds, ReadDisplayMetrics());
    }
    private void QueueDisplayRefresh(string reason)
    {
        if (!_windowLayout.Initialized || _displayRefreshQueued || _closing || IsDisposed || !IsHandleCreated) return;
        _displayRefreshQueued = true;
        BeginInvoke(() =>
        {
            _displayRefreshQueued = false;
            if (!_closing && !IsDisposed && IsHandleCreated) RefreshDisplayLayout(reason);
        });
    }
    private void RefreshDisplayLayout(string reason)
    {
        if (!_windowLayout.Initialized || _applyingDisplayLayout) return;
        var display = ReadDisplayMetrics(); var placement = _windowLayout.Calculate(display);
        var before = Bounds; var changed = _lastDisplay != display;
        _applyingDisplayLayout = true;
        try
        {
            _canvas.MinimumSize = Size.Empty;
            MinimumSize = placement.MinimumSize;
            MaximizedBounds = display.WorkingArea;
            if (WindowState == FormWindowState.Normal || WindowState == FormWindowState.Maximized)
            {
                var area = WindowState == FormWindowState.Normal ? placement.Bounds : display.WorkingArea;
                // Form.SetBoundsCore caps against SystemInformation.MaxWindowTrackSize,
                // which can still describe the old desktop. Use the fresh monitor rectangle
                // without restoring, activating or changing the window's z-order.
                if (Bounds != area && !SetWindowPos(Handle, IntPtr.Zero, area.Left, area.Top, area.Width, area.Height, 0x14))
                    AppLog.Write("Product display fit: " + Marshal.GetLastWin32Error());
            }
            _lastDisplay = display;
            PerformLayout(); _canvas.Invalidate();
            if (changed || before != Bounds)
                AppLog.WriteStructured("PRODUCT DISPLAY LAYOUT", new { reason, display, windowState = WindowState.ToString(),
                    before, after = Bounds, minimum = MinimumSize, placement.Scale, preferred = _windowLayout.PreferredSize });
        }
        finally { _applyingDisplayLayout = false; }
    }
    private ProductDisplayMetrics ReadDisplayMetrics()
    {
        if (_displayMetricsInFixture is not null) return _displayMetricsInFixture();
        var monitor = MonitorFromWindow(Handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new DisplayMonitorInfo { Size = Marshal.SizeOf<DisplayMonitorInfo>() };
        // Read current monitor rectangles directly: Screen's cached topology may still
        // describe the previous resolution while WM_DISPLAYCHANGE is being dispatched.
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return new(info.Device, info.Monitor.ToRectangle(), info.Work.ToRectangle(), DeviceDpi);
        var screen = Screen.FromControl(this);
        return new(screen.DeviceName, screen.Bounds, screen.WorkingArea, DeviceDpi);
    }
    private void ApplyFixtureTrackingLimits(IntPtr pointer)
    {
        if (_displayMetricsInFixture is null || pointer == IntPtr.Zero) return;
        // The isolated display fixture supplies both monitor geometry and its native
        // tracking limits. Hosted CI otherwise constrains a simulated 4K window to
        // its physical 1024x768 desktop, before any product layout can be observed.
        var display = _displayMetricsInFixture();
        var minimum = _windowLayout.Initialized ? _windowLayout.Calculate(display).MinimumSize : new Size(1, 1);
        Marshal.WriteInt32(pointer, 8, display.WorkingArea.Width);
        Marshal.WriteInt32(pointer, 12, display.WorkingArea.Height);
        Marshal.WriteInt32(pointer, 16, display.WorkingArea.Left - display.Bounds.Left);
        Marshal.WriteInt32(pointer, 20, display.WorkingArea.Top - display.Bounds.Top);
        Marshal.WriteInt32(pointer, 24, minimum.Width);
        Marshal.WriteInt32(pointer, 28, minimum.Height);
        Marshal.WriteInt32(pointer, 32, display.Bounds.Width);
        Marshal.WriteInt32(pointer, 36, display.Bounds.Height);
    }
    internal object DisplayDiagnostic() => new { deviceDpi = DeviceDpi, display = ReadDisplayMetrics(),
        bounds = Bounds, restoreBounds = RestoreBounds, minimumSize = MinimumSize,
        windowState = WindowState.ToString(), preferred = _windowLayout.PreferredSize };
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMonitorInfo
    {
        internal int Size;
        internal DisplayRect Monitor, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref DisplayMonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
