using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Control.Adaptive;

/// <summary>
/// Dedicated, explicit physical-qualification gate for the final HP 8C40
/// Automatic path. This does not promote normal user Automatic authority.
/// </summary>
public static class Hp8C40AutomaticFinalQualificationGate
{
    public const bool PhysicalExecutionAuthorized = true;
    public const string RequiredToken = "8C40-AUTOMATIC-FINAL";
    public const int RequiredHealthyPreWriteSamples = 3;
    public const double MaximumCpuPhysicalC = 90.0;
    public const double MaximumGpuPhysicalC = 82.0;
    public const double MaximumCpuPackagePowerW = 60.0;
    public const double MaximumGpuPowerW = 75.0;

    public const string ReadyFileName = "automatic-final.ready.json";
    public const string EventsFileName = "automatic-final.events.jsonl";
    public const string ResultFileName = "automatic-final.result.json";

    public static bool IsAuthorizedForTarget(string? targetProfileId) =>
        PhysicalExecutionAuthorized &&
        NormalUserAutomaticRemainsClosed() &&
        string.Equals(
            targetProfileId,
            Hp8C40TargetProfile.Instance.Id,
            StringComparison.Ordinal);

    public static bool NormalUserAutomaticRemainsClosed() =>
        !Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized;
}
