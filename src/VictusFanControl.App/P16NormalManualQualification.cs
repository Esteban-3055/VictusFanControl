using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>
/// P16 qualification-only bridge for proving the ordinary user-facing Manual
/// path without introducing a P16-specific application startup/test mode.
///
/// Fresh P16B one-shot target authorization after formal hardening closure.
/// This opens only the dedicated normal-Manual qualification bridge. Target
/// execution remains forbidden until this exact authorization HEAD passes full
/// same-head CI; the durable attempt fence then permits only one invocation.
/// Permanent user Manual and Automatic remain independently false.
/// </summary>
internal static class Hp8C40P16NormalManualQualificationGate
{
    public static readonly bool PhysicalExecutionAuthorized = true;

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
