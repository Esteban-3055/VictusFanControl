namespace VictusFanControl.Control.Adaptive;

/// <summary>
/// Compile-time post-M9 user-control authorization boundary.
///
/// P13 may build the user-facing control surface and connect software-only
/// state while these values remain false. Opening either value is a later,
/// separately reviewed target-side authorization and must never be bundled
/// with ordinary GUI work.
/// </summary>
public static class Hp8C40PostM9UserControlGate
{
    public const bool ManualExecutionAuthorized = false;
    public const bool AutomaticExecutionAuthorized = false;

    public const string Status =
        "P13_STEP1_PRESENTATION_ONLY_MANUAL_AUTOMATIC_GATES_CLOSED";
}
