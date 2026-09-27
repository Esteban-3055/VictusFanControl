using VictusFanControl.Hardware.PawnIo;
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

    public static HardwareTargetProfile Instance { get; } = new(
        Id: "HP-88F8-62C37LA-F32",
        DisplayName: "HP Victus 16-d0515la / HP 88F8",
        BoardManufacturer: BoardManufacturer,
        BoardProduct: BoardProduct,
        BoardVersion: BoardVersion,
        SystemManufacturer: SystemManufacturer,
        SystemProductName: SystemProductName,
        SystemSkuBase: SystemSkuPrefix,
        ValidatedBiosVersion: ValidatedBiosVersion,
        ExpectedGpuName: ExpectedGpuName,
        ExpectedPhysicalCoreCount: 8,
        MinimumValidatedFanLevel: MinimumValidatedFanLevel,
        MaximumValidatedFanLevel: MaximumValidatedFanLevel,
        SupportsIndependentFanLevels: true,
        FanEcLayout: FanEcRegisterLayout.HpLegacyDualFan,
        SleepModel: WindowsSleepModel.LegacyS3,
        WatchdogRecoveryValidated: true,
        CpuObservedMaximumRpm: CpuObservedMaximumRpm,
        GpuObservedMaximumRpm: GpuObservedMaximumRpm);

    public static bool Matches(HardwareIdentity hardware, out string reason) =>
        Instance.Matches(hardware, out reason);

    public static bool MatchesExpectedGpu(string? gpuName) =>
        Instance.MatchesExpectedGpu(gpuName);
}
