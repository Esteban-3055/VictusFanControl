using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>
/// P16 qualification-only bridge for proving the ordinary user-facing Manual
/// path without introducing a P16-specific application startup/test mode.
///
/// Generation 4 was consumed by attempt 5: initial 30/30 WMI proof and ownership
/// succeeded, then 40/40 was rejected by the coordinator safety/lifecycle gate.
/// Re-block pending exact denial diagnosis and fresh one-shot authorization.
/// Permanent user Manual and Automatic remain false.
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
