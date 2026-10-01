namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Compile-time M9D physical authorization boundary. This type has no hardware
/// methods; the App checks it before MainForm/SMBIOS/PawnIO/backend creation.
/// </summary>
public static class Hp8C40M9DProductionLifecycleQualification
{
    public static readonly bool PhysicalExecutionAuthorized = false;

    public const string RequiredToken =
        Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationToken;

    public const int QualificationLevel = 30;
}
