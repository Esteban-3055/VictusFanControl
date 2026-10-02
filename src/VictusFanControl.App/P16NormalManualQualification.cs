using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>
/// P16 qualification-only bridge for proving the ordinary user-facing Manual
/// path without introducing a P16-specific application startup/test mode.
///
/// P16B attempt 3 consumed the fresh one-shot authorization and failed closed.
/// The PowerShell 5.1 app-audit correction and bounded HP 8C40 setpoint-pair
/// coherence filter are now staged software-only behind this still-false gate.
/// Do not reauthorize until the correction passes same-head CI, independent
/// review and a separate formal closure. Permanent user Manual and Automatic
/// remain false.
/// </summary>
internal static class Hp8C40P16NormalManualQualificationGate
{
    public static readonly bool PhysicalExecutionAuthorized = false;

    public const string RequiredToken = "8C40-P16-NORMAL-MANUAL-30-40-30";
    public const int InitialLevel = 30;
    public const int ChangedLevel = 40;
    public const int ReturnLevel = 30;

    public const int InitialOwnedGeneration = 3;
    public const int ChangedOwnedGeneration = 5;
    public const int ReturnOwnedGeneration = 7;

    public static bool PermanentUserPromotionStillClosed() =>
        !Hp8C40PostM9UserControlGate.ManualExecutionAuthorized &&
        !Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized;
}
