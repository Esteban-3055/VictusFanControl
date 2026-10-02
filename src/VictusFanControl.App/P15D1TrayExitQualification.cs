using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>
/// Dedicated software/physical barrier for P15D1, which will qualify the real
/// GUI Manual 30/30 path across window-close-to-tray and explicit tray Exit.
/// This gate is intentionally independent from P15C and never opens the normal
/// post-M9 user Manual/Automatic gates.
/// </summary>
internal static class Hp8C40P15D1TrayExitQualificationGate
{
    public static readonly bool PhysicalExecutionAuthorized = false;

    public const string RequiredToken = "8C40-P15D1-TRAYEXIT30";
    public const int QualificationLevel = 30;
    public const int RequiredHealthyPreWriteSamples = 3;
    public const double MaximumCpuPhysicalC = 90.0;
    public const double MaximumGpuPhysicalC = 82.0;
    public const double MaximumCpuPackagePowerW = 60.0;
    public const double MaximumGpuPowerW = 75.0;

    public static bool NormalUserExecutionGatesClosed() =>
        !Hp8C40PostM9UserControlGate.ManualExecutionAuthorized &&
        !Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized;
}
