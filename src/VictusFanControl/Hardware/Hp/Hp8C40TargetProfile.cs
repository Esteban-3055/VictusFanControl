using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Exact qualification fingerprint for the HP Victus 15-fa1013la / 9D0R1LA
/// target. Equal fan levels 30/30 through 36/36 have been physically
/// qualified with EC acknowledgement, dual-tach feedback and verified
/// FF/FF -> LegacyDefault restore after each step.
/// </summary>
public static class Hp8C40TargetProfile
{
    public const string BoardManufacturer = "HP";
    public const string BoardProduct = "8C40";
    public const string BoardVersion = "63.43";
    public const string SystemManufacturer = "HP";
    public const string SystemProductName = "Victus by HP Gaming Laptop 15-fa1xxx";
    public const string SystemSkuPrefix = "9D0R1LA";
    public const string ValidatedBiosVersion = "F.18";
    public const string ExpectedGpuName = "NVIDIA GeForce RTX 4060 Laptop GPU";

    // Physical qualification has proven equal 30/30 through 36/36.
    // Independent CPU/GPU levels remain unqualified and are rejected.
    public const int MinimumValidatedFanLevel = 30;
    public const int MaximumValidatedFanLevel = 36;

    public static HardwareTargetProfile Instance { get; } = new(
        Id: "HP-8C40-9D0R1LA-F18",
        DisplayName: "HP Victus 15-fa1013la / HP 8C40",
        BoardManufacturer: BoardManufacturer,
        BoardProduct: BoardProduct,
        BoardVersion: BoardVersion,
        SystemManufacturer: SystemManufacturer,
        SystemProductName: SystemProductName,
        SystemSkuBase: SystemSkuPrefix,
        ValidatedBiosVersion: ValidatedBiosVersion,
        ExpectedGpuName: ExpectedGpuName,
        ExpectedPhysicalCoreCount: 14,
        MinimumValidatedFanLevel: MinimumValidatedFanLevel,
        MaximumValidatedFanLevel: MaximumValidatedFanLevel,
        SupportsIndependentFanLevels: false,
        FanEcLayout: FanEcRegisterLayout.HpLegacyDualFan,
        SleepModel: WindowsSleepModel.ModernStandbyS0LowPowerIdle,
        WatchdogRecoveryValidated: false);

    public static bool Matches(HardwareIdentity hardware, out string reason) =>
        Instance.Matches(hardware, out reason);

    public static bool MatchesExpectedGpu(string? gpuName) =>
        Instance.MatchesExpectedGpu(gpuName);
}
