using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

public sealed record Hp88F8EcControlState(
    byte CpuSetpoint,
    byte GpuSetpoint,
    byte CpuRate,
    byte GpuRate,
    byte Countdown,
    ushort CpuRpm,
    ushort GpuRpm)
{
    public override string ToString() =>
        $"setpoint CPU={CpuSetpoint} GPU={GpuSetpoint} | " +
        $"rate CPU={CpuRate}% GPU={GpuRate}% | " +
        $"countdown={Countdown} | RPM CPU={CpuRpm} GPU={GpuRpm}";
}

/// <summary>
/// On-demand, read-only diagnostic for the known HP 88F8 EC locations.
/// The ACPI read protocol writes only the READ command and register address,
/// never an EC register value.
/// </summary>
public sealed class Hp88F8EcControlStateProbe
{
    private readonly string _modulePath;

    public Hp88F8EcControlStateProbe(string modulesDirectory)
    {
        _modulePath = Path.Combine(modulesDirectory, "LpcACPIEC.bin");
    }

    public Hp88F8EcControlState Read()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!string.Equals(
                hardware.BoardProduct,
                "88F8",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"88F8 EC-state probe refused on board '{hardware.BoardProduct}'.");
        }

        using var ec = new AcpiEcReader(_modulePath);
        var state = ec.ReadHp88F8ControlState();

        return new Hp88F8EcControlState(
            state.CpuSetpoint,
            state.GpuSetpoint,
            state.CpuRate,
            state.GpuRate,
            state.Countdown,
            state.CpuRpm,
            state.GpuRpm);
    }
}
