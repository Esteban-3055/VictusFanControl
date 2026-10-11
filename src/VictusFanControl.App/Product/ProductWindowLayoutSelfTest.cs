using System.Drawing;

namespace VictusFanControl.App;

internal static class ProductWindowLayoutSelfTest
{
    internal static void Run(Action<bool, string> require)
    {
        var fourK = new ProductDisplayMetrics("fixture", new(0, 0, 3840, 2160), new(0, 0, 3840, 2080), 192);
        var gaming = fourK with { Bounds = new(0, 0, 2560, 1440), WorkingArea = new(0, 0, 2560, 1392) };
        var fullHd = fourK with { Bounds = new(0, 0, 1920, 1080), WorkingArea = new(0, 0, 1920, 1040), Dpi = 96 };
        var layout = new ProductWindowLayout();
        layout.Capture(new(576, 284, 2688, 1512), fourK, initial: true);
        var initial = layout.Calculate(fourK);
        require(initial.Bounds.Size == new Size(2688, 1512), "4K at 200% lost its initial DPI size.");
        require(layout.Calculate(gaming).Bounds.Size == new Size(1792, 1008), "1440p at unchanged 200% retained the oversized 4K window.");
        require(layout.Calculate(fullHd).Bounds.Size == new Size(1344, 756), "1080p at 100% was scaled twice.");
        require(layout.Calculate(gaming with { Dpi = 96 }).Bounds.Size == new Size(1344, 756), "Resolution fitting ignored a lower Windows DPI.");
        for (var i = 0; i < 100; i++)
        {
            layout.Calculate(gaming); layout.Calculate(fullHd);
            require(layout.Calculate(fourK) == initial, "Resolution/DPI round trips accumulated shrinkage or lost the preferred size.");
        }
        var dpiOnly = fourK with { Dpi = 96 };
        layout.Capture(new(1248, 662, 1344, 756), dpiOnly, initial: true);
        require(layout.Calculate(fourK).Bounds.Size == new Size(2688, 1512), "DPI-only change did not scale exactly once.");
        layout.Capture(new(700, 350, 2200, 1400), fourK);
        require(layout.Calculate(fourK).Bounds.Size == new Size(2200, 1400), "Explicit user resize was replaced by the default size.");
        layout.Calculate(gaming);
        require(layout.Calculate(fourK).Bounds.Size == new Size(2200, 1400), "Game mode overwrote the custom normal size.");
        var small = fourK with { Bounds = new(0, 0, 800, 600), WorkingArea = new(0, 0, 800, 560) };
        var fitted = layout.Calculate(small);
        require(small.WorkingArea.Contains(fitted.Bounds) && fitted.MinimumSize.Width <= small.WorkingArea.Width &&
            fitted.MinimumSize.Height <= small.WorkingArea.Height, "Tiny desktop fitting escaped the available area.");
        require(layout.Calculate(fourK).MinimumSize == initial.MinimumSize, "The reduced minimum size survived return to 4K.");
        var negative = gaming with { Bounds = new(-2560, 0, 2560, 1440), WorkingArea = new(-2560, 0, 2560, 1392) };
        require(negative.WorkingArea.Contains(layout.Calculate(negative).Bounds), "Negative monitor coordinates escaped clamping.");
        var taskbar = fourK with { WorkingArea = new(0, 120, 3840, 1960) };
        require(taskbar.WorkingArea.Contains(layout.Calculate(taskbar).Bounds), "Taskbar position was ignored.");
        layout.Capture(new(0, 0, 5000, 4000), fourK);
        require(fourK.WorkingArea.Contains(layout.Calculate(fourK).Bounds), "Oversized custom window was not bounded.");
        var rejected = false; try { layout.Calculate(fourK with { WorkingArea = Rectangle.Empty }); } catch (ArgumentException) { rejected = true; }
        require(rejected, "Invalid display geometry entered window placement.");
        Console.WriteLine("PASS display geometry: 4K/200% -> 1440p/200% -> 1080p/100%, DPI-only, 100 round trips, custom resize, taskbar and negative coordinates.");
    }
}
