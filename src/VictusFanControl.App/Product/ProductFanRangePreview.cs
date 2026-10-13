namespace VictusFanControl.App;

// Process-only trial switch. Never saved in profiles or enabled by Windows startup.
internal static class ProductFanRangePreview
{
    internal static bool Enabled { get; set; }
}
