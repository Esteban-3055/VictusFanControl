using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed record ProductInstallationPlan(ProductProfiles Profiles, bool SaveProfiles, bool RegisterStartup)
{
    internal static ProductInstallationPlan Create(ProductProfiles profiles, bool preserve, bool startupWasEnabled) =>
        preserve ? new(profiles, false, startupWasEnabled) : new(profiles with { ActivateAutomaticOnStart = true, StartMinimized = true }, true, true);
}
