using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

public enum WindowsSleepModel
{
    LegacyS3,
    ModernStandbyS0LowPowerIdle
}

/// <summary>
/// Immutable, exact-match hardware qualification profile. A shared HP command
/// or EC layout is never assumed merely because two systems are both Victus
/// models; every target opts into each capability explicitly.
/// </summary>
public sealed record HardwareTargetProfile(
    string Id,
    string DisplayName,
    string BoardManufacturer,
    string BoardProduct,
    string BoardVersion,
    string SystemManufacturer,
    string SystemProductName,
    string SystemSkuBase,
    string ValidatedBiosVersion,
    string ExpectedGpuName,
    int MinimumValidatedFanLevel,
    int MaximumValidatedFanLevel,
    bool SupportsIndependentFanLevels,
    FanEcRegisterLayout FanEcLayout,
    WindowsSleepModel SleepModel,
    bool WatchdogRecoveryValidated,
    int? CpuObservedMaximumRpm = null,
    int? GpuObservedMaximumRpm = null)
{
    public bool Matches(HardwareIdentity hardware, out string reason)
    {
        if (!EqualsIgnoreCase(hardware.BoardManufacturer, BoardManufacturer))
        {
            reason = $"Board manufacturer '{hardware.BoardManufacturer}' != '{BoardManufacturer}'.";
            return false;
        }

        if (!EqualsIgnoreCase(hardware.BoardProduct, BoardProduct))
        {
            reason = $"Board product '{hardware.BoardProduct}' != '{BoardProduct}'.";
            return false;
        }

        if (!EqualsIgnoreCase(hardware.BoardVersion, BoardVersion))
        {
            reason = $"Board version '{hardware.BoardVersion}' != '{BoardVersion}'.";
            return false;
        }

        if (!EqualsIgnoreCase(hardware.SystemManufacturer, SystemManufacturer))
        {
            reason = $"System manufacturer '{hardware.SystemManufacturer}' != '{SystemManufacturer}'.";
            return false;
        }

        if (!EqualsIgnoreCase(hardware.SystemProductName, SystemProductName))
        {
            reason = $"System product '{hardware.SystemProductName}' != '{SystemProductName}'.";
            return false;
        }

        var skuBase = hardware.SystemSku
            .Split('#', 2, StringSplitOptions.TrimEntries)[0];

        if (!EqualsIgnoreCase(skuBase, SystemSkuBase))
        {
            reason = $"System SKU base '{skuBase}' != '{SystemSkuBase}'.";
            return false;
        }

        var biosMatched = hardware.BiosVersion
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => EqualsIgnoreCase(value, ValidatedBiosVersion));

        if (!biosMatched)
        {
            reason = $"BIOS '{hardware.BiosVersion}' is not the validated '{ValidatedBiosVersion}'.";
            return false;
        }

        reason = $"Exact validated target '{Id}' matched.";
        return true;
    }

    public bool MatchesExpectedGpu(string? gpuName) =>
        string.Equals(
            gpuName?.Trim(),
            ExpectedGpuName,
            StringComparison.OrdinalIgnoreCase);

    private static bool EqualsIgnoreCase(string? left, string right) =>
        string.Equals(left?.Trim(), right, StringComparison.OrdinalIgnoreCase);
}
