using System.Runtime.InteropServices;
using System.IO.Compression;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestDisplayChanges(Action<bool, string> require)
    {
        var display = new ProductDisplayMetrics("fixture", new(0, 0, 3840, 2160), new(0, 0, 3840, 2080), 96);
        var runtime = new RecordingRuntime();
        using var form = new ProductForm("fixture://display", fixture: runtime, fixtureProfiles: new(), displayMetricsInFixture: () => display);
        form.Show(); Application.DoEvents();
        Console.WriteLine($"Display fixture open: size={form.Size}; dpi={form.DeviceDpi}; nativeDesktop={Screen.FromControl(form).Bounds}; diagnostic={System.Text.Json.JsonSerializer.Serialize(form.DisplayDiagnostic())}");
        var directory = Path.Combine("logs", "product-gui-self-test"); Directory.CreateDirectory(directory);
        void Render(string name)
        {
            form.Canvas.Refresh(); using var bitmap = new Bitmap(form.Canvas.Width, form.Canvas.Height);
            form.Canvas.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, name + ".png"));
        }
        // Feed the actual WinForms DPI message, including the suggested native RECT.
        var rect = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(rect, 0, 576); Marshal.WriteInt32(rect, 4, 284);
            Marshal.WriteInt32(rect, 8, 3264); Marshal.WriteInt32(rect, 12, 1796);
            display = display with { Dpi = 192 };
            SendMessage(form.Handle, 0x2E0, (IntPtr)(192 | 192 << 16), rect);
            Application.DoEvents();
            require(form.Size == new Size(2688, 1512), "Native DPI notification applied a second scale or failed to refresh: " + System.Text.Json.JsonSerializer.Serialize(form.DisplayDiagnostic()));
        }
        finally { Marshal.FreeHGlobal(rect); }
        Render("display-4k-200-before");
        void Resolution(int width, int height, int workHeight)
        {
            display = display with { Bounds = new(0, 0, width, height), WorkingArea = new(0, 0, width, workHeight) };
            SendMessage(form.Handle, 0x7E, (IntPtr)32, (IntPtr)(width | height << 16)); Application.DoEvents();
        }
        Resolution(2560, 1440, 1392);
        require(form.Size == new Size(1792, 1008) && display.WorkingArea.Contains(form.Bounds), "Real WM_DISPLAYCHANGE did not reduce the live window at unchanged DPI.");
        Render("display-1440-200-after");
        Resolution(3840, 2160, 2080);
        require(form.Size == new Size(2688, 1512), "Returning to 4K did not restore the preferred window size.");
        Render("display-4k-200-restored");
        form.Hide(); Resolution(2560, 1440, 1392);
        require(!form.Visible && form.Size == new Size(1792, 1008), "A hidden resolution change showed the app or retained the old size.");
        form.Show(); Application.DoEvents();
        form.WindowState = FormWindowState.Minimized; Application.DoEvents(); Resolution(3840, 2160, 2080);
        require(form.WindowState == FormWindowState.Minimized, "Display fitting stole focus from the game by restoring a minimized window.");
        form.WindowState = FormWindowState.Normal; Application.DoEvents();
        require(form.Size == new Size(2688, 1512), "Minimized window restored using stale resolution bounds.");
        form.WindowState = FormWindowState.Maximized; Application.DoEvents(); Resolution(2560, 1440, 1392);
        require(form.WindowState == FormWindowState.Maximized && form.Bounds == display.WorkingArea, "Maximized resolution change lost its state or covered the taskbar.");
        form.WindowState = FormWindowState.Normal; Application.DoEvents();
        require(form.Size == new Size(1792, 1008), "Restoring a maximized window lost the normal-size reference.");
        display = display with { WorkingArea = new(0, 80, 2560, 1312) };
        SendMessage(form.Handle, 0x1A, (IntPtr)47, IntPtr.Zero); Application.DoEvents();
        require(display.WorkingArea.Contains(form.Bounds), "Working-area notification left the window behind the taskbar.");
        var path = Path.Combine(Path.GetTempPath(), "vfc-display-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var export = form.ExportDiagnosticsAsync(path); PumpUntil(() => export.IsCompleted, "Display diagnostic export hung.");
            using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("gui-state.json")!.Open();
            using var json = System.Text.Json.JsonDocument.Parse(stream);
            require(json.RootElement.GetProperty("window").GetProperty("display").GetProperty("Dpi").GetInt32() == 192,
                "Diagnostic export lost current display DPI and geometry.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        require(runtime.Commands == 0 && runtime.Fences == 0, "Resolution or DPI changes altered hardware authority or performance settings.");
        var exit = form.RequestExitAsync(); PumpUntil(() => exit.IsCompleted, "Display fixture shutdown hung.");
        Console.WriteLine("PASS live window display messages: native DPI, resolution round trip, hidden/minimized/maximized, taskbar, diagnostic geometry; zero hardware commands.");
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
