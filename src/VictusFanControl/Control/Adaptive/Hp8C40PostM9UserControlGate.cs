using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Control.Adaptive;

/// <summary>
/// P16C ordinary Manual promotion after audited attempt 7 and closure CI #1218.
/// Automatic remains closed. Startup and persisted preferences cannot acquire authority.
/// </summary>
public static class Hp8C40PostM9UserControlGate
{
    public const bool ManualExecutionAuthorized = true;
    public const bool AutomaticExecutionAuthorized = false;

    public const string Status = "P16C_MANUAL_PROMOTED_AUTOMATIC_CLOSED";

    public static bool IsManualAuthorizedForTarget(string? targetProfileId) =>
        ManualExecutionAuthorized &&
        string.Equals(targetProfileId, Hp8C40TargetProfile.Instance.Id, StringComparison.Ordinal);
}
