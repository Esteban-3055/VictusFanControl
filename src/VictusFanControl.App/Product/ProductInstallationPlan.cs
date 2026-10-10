using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed record ProductInstallationPlan(ProductProfiles Profiles, bool SaveProfiles, bool RegisterStartup)
{
    internal static ProductInstallationPlan Create(ProductProfiles profiles, bool preserve, bool startupWasEnabled, bool? enableStartupAutomatic = null) =>
        preserve ? enableStartupAutomatic == true ? new(profiles with { ActivateAutomaticOnStart = true }, true, true) : new(profiles, false, startupWasEnabled)
            : new(profiles with { ActivateAutomaticOnStart = enableStartupAutomatic != false, StartMinimized = true }, true, enableStartupAutomatic != false);
}
