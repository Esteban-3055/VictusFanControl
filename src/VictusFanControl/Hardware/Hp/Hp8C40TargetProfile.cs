using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Exact qualification fingerprint for the HP Victus 15-fa1013la / 9D0R1LA
/// target. Only fan level 30/30 has been physically qualified so far; the
/// validated range intentionally remains 30..30 until further characterization.
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

    // The bounded hardware qualification has proven only the equal 30/30
    // transaction and its FF/FF -> LegacyDefault restore path.
    public const int MinimumValidatedFanLevel = 30;
    public const int MaximumValidatedFanLevel = 30;

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
