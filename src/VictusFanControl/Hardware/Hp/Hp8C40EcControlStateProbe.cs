using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

public sealed record Hp8C40EcControlState(
    byte CpuRateTarget,
    byte GpuRateTarget,
    byte CpuRate,
    byte GpuRate,
    byte CpuSetpoint,
    byte GpuSetpoint,
    byte Manual,
    byte Countdown,
    byte Mode,
    byte MaxFan,
    byte FanSwitch,
    ushort CpuRpm,
    ushort GpuRpm)
{
    public override string ToString() =>
        $"level CPU={CpuSetpoint} GPU={GpuSetpoint} | " +
        $"rate-target CPU={CpuRateTarget}% GPU={GpuRateTarget}% | " +
        $"rate-read CPU={CpuRate}% GPU={GpuRate}% | " +
        $"manual=0x{Manual:X2} countdown={Countdown} mode=0x{Mode:X2} " +
        $"max=0x{MaxFan:X2} switch=0x{FanSwitch:X2} | " +
        $"RPM CPU={CpuRpm} GPU={GpuRpm}";
}

/// <summary>
/// On-demand, read-only diagnostic for the known HP 8C40 EC locations.
/// The ACPI read protocol writes only the READ command and register address,
/// never an EC register value.
/// </summary>
public sealed class Hp8C40EcControlStateProbe
{
    private readonly string _modulePath;

    public Hp8C40EcControlStateProbe(string modulesDirectory)
    {
        _modulePath = Path.Combine(modulesDirectory, "LpcACPIEC.bin");
    }

    public (byte CpuSetpoint, byte GpuSetpoint) ReadSetpoint()
    {
        EnsureTargetBoard();

        using var ec = new AcpiEcReader(_modulePath);
        var state = ec.ReadFanSetpoint(Hp8C40TargetProfile.Instance.FanEcLayout);

        return (
            state.CpuSetpoint,
            state.GpuSetpoint);
    }

    public Hp8C40EcControlState Read()
    {
        EnsureTargetBoard();

        using var ec = new AcpiEcReader(_modulePath);
        var state = ec.ReadFanControlState(Hp8C40TargetProfile.Instance.FanEcLayout);

        return new Hp8C40EcControlState(
            state.CpuRateTarget,
            state.GpuRateTarget,
            state.CpuRate,
            state.GpuRate,
            state.CpuSetpoint,
            state.GpuSetpoint,
            state.Manual,
            state.Countdown,
            state.Mode,
            state.MaxFan,
            state.FanSwitch,
            state.CpuRpm,
            state.GpuRpm);
    }

    private static void EnsureTargetBoard()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!string.Equals(
                hardware.BoardProduct,
                "8C40",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"8C40 EC-state probe refused on board '{hardware.BoardProduct}'.");
        }
    }
}
