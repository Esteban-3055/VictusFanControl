namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Compile-time M9D full-GUI production-path lifecycle qualification gate.
///
/// This class contains no hardware operations. The App checks this barrier
/// before module discovery, MainForm construction or any IPC/hardware object
/// can be created. M9D may be opened only by a later explicit commit after
/// M9B and M9C physical evidence are formally closed.
/// </summary>
public static class Hp8C40M9DProductionLifecycleQualification
{
    public const string RequiredToken =
        Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationToken;

    public const int QualificationLevel = 30;

    public static readonly bool PhysicalExecutionAuthorized = false;
}
