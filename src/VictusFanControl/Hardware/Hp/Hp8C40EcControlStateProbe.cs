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
    byte Diagnostic62,
    byte Diagnostic63,
    byte Mode,
    byte MaxFan,
    byte FanSwitch,
    ushort CpuRpm,
    ushort GpuRpm)
{
    // Diagnostic EC records retain exact 1-RPM resolution and sequence zero.
    // Production control attaches fresh WMI query identity and 100-RPM bins.
    public int TachometerResolutionRpm { get; init; } = 1;
    public long FanQuerySequence { get; init; }
    public long FanQueryStartedAtMilliseconds { get; init; }

    public override string ToString() =>
        $"level CPU={CpuSetpoint} GPU={GpuSetpoint} | " +
        $"rate-target CPU={CpuRateTarget}% GPU={GpuRateTarget}% | " +
        $"rate-read CPU={CpuRate}% GPU={GpuRate}% | " +
        $"reg62=0x{Diagnostic62:X2} reg63=0x{Diagnostic63:X2} mode=0x{Mode:X2} " +
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
        var state = ec.ReadStableFanSetpoint(Hp8C40TargetProfile.Instance.FanEcLayout);

        return (
            state.CpuSetpoint,
            state.GpuSetpoint);
    }

    /// <summary>
    /// Reads only the EC evidence used by production ownership/safety:
    /// setpoints, MaxFan/FanSwitch and both physical tachometers.
    /// This deliberately avoids the broader diagnostic registers because
    /// qualification/control must not depend on 0x2C-0x2F/0x62/0x63/0x95.
    /// </summary>
    public Hp8C40EcControlState ReadControlEvidence()
    {
        EnsureTargetBoard();

        using var ec = new AcpiEcReader(_modulePath);
        var layout = Hp8C40TargetProfile.Instance.FanEcLayout;
        var setpoint = ec.ReadStableFanSetpoint(layout);
        var guard = ec.ReadFanControlGuard(layout);
        var tachometers = ec.ReadFanTachometers(layout);

        return new Hp8C40EcControlState(
            CpuRateTarget: byte.MaxValue,
            GpuRateTarget: byte.MaxValue,
            CpuRate: byte.MaxValue,
            GpuRate: byte.MaxValue,
            CpuSetpoint: setpoint.CpuSetpoint,
            GpuSetpoint: setpoint.GpuSetpoint,
            Diagnostic62: byte.MaxValue,
            Diagnostic63: byte.MaxValue,
            Mode: byte.MaxValue,
            MaxFan: guard.MaxFan,
            FanSwitch: guard.FanSwitch,
            CpuRpm: tachometers.CpuRpm,
            GpuRpm: tachometers.GpuRpm);
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
            state.Diagnostic62,
            state.Diagnostic63,
            state.Mode,
            state.MaxFan,
            state.FanSwitch,
            state.CpuRpm,
            state.GpuRpm);
    }

    private static void EnsureTargetBoard()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var reason))
        {
            throw new InvalidOperationException(
                $"8C40 EC-state probe refused: {reason}");
        }
    }
}
