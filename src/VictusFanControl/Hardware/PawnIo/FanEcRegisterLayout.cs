namespace VictusFanControl.Hardware.PawnIo;

/// <summary>
/// Read-only EC register map used to observe HP dual-fan ownership, guards,
/// tachometers and diagnostic state. VictusFanControl never writes these EC
/// registers; ordinary fan commands continue to use the validated HP BIOS/WMI
/// interface.
/// </summary>
public readonly record struct FanEcRegisterLayout(
    byte CpuRateTarget,
    byte GpuRateTarget,
    byte CpuRate,
    byte GpuRate,
    byte CpuSetpoint,
    byte GpuSetpoint,
    byte CpuTemperature,
    byte Manual,
    byte Countdown,
    byte Mode,
    byte CpuTachLow,
    byte GpuTachLow,
    byte GpuTemperature,
    byte MaxFan,
    byte FanSwitch)
{
    /// <summary>
    /// Layout physically observed on both the validated 88F8 platform and the
    /// bounded 8C40 qualification target. Sharing the numeric layout does not
    /// make it universal: each hardware profile must opt into it explicitly.
    /// </summary>
    public static FanEcRegisterLayout HpLegacyDualFan { get; } = new(
        CpuRateTarget: 0x2C,
        GpuRateTarget: 0x2D,
        CpuRate: 0x2E,
        GpuRate: 0x2F,
        CpuSetpoint: 0x34,
        GpuSetpoint: 0x35,
        CpuTemperature: 0x57,
        Manual: 0x62,
        Countdown: 0x63,
        Mode: 0x95,
        CpuTachLow: 0xB0,
        GpuTachLow: 0xB2,
        GpuTemperature: 0xB7,
        MaxFan: 0xEC,
        FanSwitch: 0xF4);
}
