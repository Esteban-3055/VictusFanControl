namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Compile-time authorization metadata for the M9D GUI production-path
/// Modern Standby lifecycle regression.
/// </summary>
public static class Hp8C40M9DProductionLifecycleQualificationTest
{
    public const string RequiredToken =
        Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationToken;

    public const int QualificationLevel = 30;

    public static readonly bool PhysicalExecutionAuthorized = false;
}
