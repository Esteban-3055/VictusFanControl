using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Exact development-target fingerprint. Product ID alone is not sufficient:
/// HP can reuse a motherboard product ID across multiple system SKUs.
/// </summary>
public static class Hp88F8TargetProfile
{
    public const string BoardManufacturer = "HP";
    public const string BoardProduct = "88F8";
    public const string BoardVersion = "88.58";
    public const string SystemManufacturer = "HP";
    public const string SystemProductName = "Victus by HP Laptop 16-d0xxx";
    public const string SystemSkuPrefix = "62C37LA";
    public const string ValidatedBiosVersion = "F.32";
    public const string ExpectedGpuName = "NVIDIA GeForce RTX 3060 Laptop GPU";
    public const int MinimumValidatedFanLevel = 14;
    public const int MaximumValidatedFanLevel = 50;

    // Physical observations on the development target. These are not command
    // targets; they are used only to avoid demanding further RPM increase when
    // a fan is already at its measured physical ceiling.
    public const int CpuObservedMaximumRpm = 4330;
    public const int GpuObservedMaximumRpm = 4670;

    public static bool Matches(HardwareIdentity hardware, out string reason)
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

        if (!EqualsIgnoreCase(skuBase, SystemSkuPrefix))
        {
            reason = $"System SKU base '{skuBase}' != '{SystemSkuPrefix}'.";
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

        reason = "Exact HP 88F8 development target and validated BIOS matched.";
        return true;
    }

    public static bool MatchesExpectedGpu(string? gpuName) =>
        string.Equals(
            gpuName?.Trim(),
            ExpectedGpuName,
            StringComparison.OrdinalIgnoreCase);

    private static bool EqualsIgnoreCase(string left, string right) =>
        string.Equals(left?.Trim(), right, StringComparison.OrdinalIgnoreCase);
}
