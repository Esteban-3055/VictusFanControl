namespace VictusFanControl.App;

internal enum ProductTrayState { Default, Automatic, Error, AutomaticMuted }

/// <summary>One owned set per window. Original artwork is embedded; no external icon files are required.</summary>
internal sealed class ProductIcons : IDisposable
{
    private bool _disposed;
    internal Icon Program { get; } = Load("program", 32);
    internal Icon Default { get; } = Load("tray-default", SystemInformation.SmallIconSize.Width);
    internal Icon Automatic { get; } = Load("tray-automatic", SystemInformation.SmallIconSize.Width);
    internal Icon AutomaticMuted { get; } = Load("tray-automatic-muted", SystemInformation.SmallIconSize.Width);
    internal Icon Error { get; } = Load("tray-error", SystemInformation.SmallIconSize.Width);
    internal Icon For(ProductTrayState state) => state switch { ProductTrayState.Automatic => Automatic, ProductTrayState.AutomaticMuted => AutomaticMuted, ProductTrayState.Error => Error, _ => Default };
    internal static ProductTrayState Select(ProductRuntimeState state, ProductTrayState previous, bool microphoneMuted = false)
    {
        if (state.LifecycleBlocked || state.Failure is not null || state.Runtime is "Failed" or "Faulted") return ProductTrayState.Error;
        if (state.FanMode == "Automatic" && state.FanAuthority == "Custom" && state.Runtime == "Healthy" && !state.AutomaticPreparing)
            return microphoneMuted ? ProductTrayState.AutomaticMuted : ProductTrayState.Automatic;
        // A cancelled Automatic remains visible until a successful explicit reactivation.
        return previous == ProductTrayState.Error ? ProductTrayState.Error : ProductTrayState.Default;
    }
    private static Icon Load(string name, int size)
    {
        using var stream = typeof(ProductIcons).Assembly.GetManifestResourceStream("VictusFanControl.Icons." + name + ".ico")
            ?? throw new InvalidDataException("Missing embedded product icon: " + name);
        using var icon = new Icon(stream, new Size(size, size));
        return (Icon)icon.Clone();
    }
    public void Dispose() { if(_disposed)return;_disposed=true;Program.Dispose(); Default.Dispose(); Automatic.Dispose(); AutomaticMuted.Dispose(); Error.Dispose(); }
}
