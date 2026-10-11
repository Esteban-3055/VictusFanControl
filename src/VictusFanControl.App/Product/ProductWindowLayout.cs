using System.Drawing;

namespace VictusFanControl.App;

internal sealed record ProductDisplayMetrics(string Device, Rectangle Bounds, Rectangle WorkingArea, int Dpi);
internal sealed record ProductWindowPlacement(Rectangle Bounds, Size MinimumSize, double Scale);

/// <summary>Recompute from a preferred normal size, never from the last auto-scaled/clamped result.</summary>
internal sealed class ProductWindowLayout
{
    private SizeF _preferred;
    private PointF _center;
    internal bool Initialized { get; private set; }
    internal SizeF PreferredSize => _preferred;
    internal static double Scale(ProductDisplayMetrics display) => Math.Min(display.Dpi / 96d,
        Math.Min(display.Bounds.Width / 1920d, display.Bounds.Height / 1080d));
    internal void Capture(Rectangle normalBounds, ProductDisplayMetrics display, bool initial = false)
    {
        Validate(display);
        // WinForms initially scales the 96-DPI design size. Later explicit user resizes
        // use our viewport scale, so a game-resolution reduction is not saved as a new preference.
        var scale = initial ? display.Dpi / 96d : Scale(display);
        _preferred = new((float)(normalBounds.Width / scale), (float)(normalBounds.Height / scale));
        _center = new((float)((normalBounds.Left + normalBounds.Width / 2d - display.WorkingArea.Left) / display.WorkingArea.Width),
            (float)((normalBounds.Top + normalBounds.Height / 2d - display.WorkingArea.Top) / display.WorkingArea.Height));
        Initialized = true;
    }
    internal ProductWindowPlacement Calculate(ProductDisplayMetrics display)
    {
        Validate(display);
        if (!Initialized) throw new InvalidOperationException("La ventana requiere una referencia inicial.");
        var area = display.WorkingArea; var scale = Scale(display);
        static int Pixels(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 1, int.MaxValue);
        var minimum = new Size(Math.Min(area.Width, Pixels(1040 * scale)), Math.Min(area.Height, Pixels(660 * scale)));
        var size = new Size(Math.Clamp(Pixels(_preferred.Width * scale), minimum.Width, area.Width),
            Math.Clamp(Pixels(_preferred.Height * scale), minimum.Height, area.Height));
        var left = (int)Math.Clamp(Math.Round(area.Left + _center.X * area.Width - size.Width / 2d), area.Left, area.Right - size.Width);
        var top = (int)Math.Clamp(Math.Round(area.Top + _center.Y * area.Height - size.Height / 2d), area.Top, area.Bottom - size.Height);
        return new(new(left, top, size.Width, size.Height), minimum, scale);
    }
    private static void Validate(ProductDisplayMetrics display)
    {
        if (display.Dpi <= 0 || display.Bounds.Width <= 0 || display.Bounds.Height <= 0 ||
            display.WorkingArea.Width <= 0 || display.WorkingArea.Height <= 0)
            throw new ArgumentException("Geometría de pantalla no válida.");
    }
}
