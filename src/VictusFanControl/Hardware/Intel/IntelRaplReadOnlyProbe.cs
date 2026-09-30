using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Intel;

internal static class IntelRaplReadOnlyProbe
{
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgPowerLimit = 0x610;
    private const uint MsrPkgPowerInfo = 0x614;

    private const int StabilitySamples = 6;
    private static readonly TimeSpan StabilityInterval =
        TimeSpan.FromSeconds(1);

    public static int Run(
        string modulesDirectory,
        TextWriter output)
    {
        output.WriteLine("VictusFanControl - Intel RAPL P0 READ-ONLY feasibility probe");
        output.WriteLine("This mode performs MSR reads only. It never calls ioctl_write_msr.");
        output.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var targetReason))
        {
            output.WriteLine($"RESULT: FAIL_CLOSED_TARGET_MISMATCH");
            output.WriteLine($"Reason: {targetReason}");
            return 2;
        }

        var modulePath = Path.Combine(
            modulesDirectory,
            "IntelMSR.bin");

        try
        {
            using var session = new PawnIoModuleSession(modulePath);

            var unitsRaw = ReadMsr(session, MsrRaplPowerUnit);
            var units = IntelRaplCodec.DecodeUnits(unitsRaw);

            var powerLimitSamples = new ulong[StabilitySamples];
            powerLimitSamples[0] = ReadMsr(session, MsrPkgPowerLimit);

            var powerInfoRaw = ReadMsr(session, MsrPkgPowerInfo);

            for (var i = 1; i < StabilitySamples; i++)
            {
                Thread.Sleep(StabilityInterval);
                powerLimitSamples[i] = ReadMsr(
                    session,
                    MsrPkgPowerLimit);
            }

            var limits = IntelRaplCodec.DecodePackagePowerLimit(
                powerLimitSamples[0],
                units);

            var info = IntelRaplCodec.DecodePackagePowerInfo(
                powerInfoRaw,
                units);

            var distinctLimits = powerLimitSamples.Distinct().ToArray();

            output.WriteLine($"Target profile : {Hp8C40TargetProfile.Instance.Id}");
            output.WriteLine($"Board          : {hardware.BoardManufacturer} {hardware.BoardProduct} rev. {hardware.BoardVersion}");
            output.WriteLine($"System         : {hardware.SystemProductName}");
            output.WriteLine($"BIOS           : {hardware.BiosVersion}");
            output.WriteLine($"PawnIO         : {session.DriverVersion}");
            output.WriteLine();

            output.WriteLine("MSR_RAPL_POWER_UNIT (0x606)");
            output.WriteLine($"  Raw          : 0x{unitsRaw:X16}");
            output.WriteLine($"  Power unit   : {units.PowerWatts:0.########} W");
            output.WriteLine($"  Energy unit  : {units.EnergyJoules:0.##########} J");
            output.WriteLine($"  Time unit    : {units.TimeSeconds:0.##########} s");
            output.WriteLine();

            output.WriteLine("MSR_PKG_POWER_LIMIT (0x610)");
            output.WriteLine($"  Raw initial  : 0x{limits.Raw:X16}");
            PrintLimit(output, "PL1", limits.Pl1);
            PrintLimit(output, "PL2", limits.Pl2);
            output.WriteLine($"  LOCK bit     : {(limits.Locked ? 1 : 0)}");
            output.WriteLine();

            output.WriteLine("MSR_PKG_POWER_INFO (0x614)");
            output.WriteLine($"  Raw          : 0x{info.Raw:X16}");
            output.WriteLine($"  Thermal spec : {info.ThermalSpecWatts:0.###} W");
            output.WriteLine($"  Minimum      : {info.MinimumWatts:0.###} W");
            output.WriteLine($"  Maximum      : {info.MaximumWatts:0.###} W");
            output.WriteLine($"  Max time win : {info.MaximumTimeWindowSeconds:0.######} s");
            output.WriteLine();

            output.WriteLine($"0x610 stability observation ({StabilitySamples} samples / ~5 s)");
            for (var i = 0; i < powerLimitSamples.Length; i++)
            {
                output.WriteLine(
                    $"  [{i + 1}] 0x{powerLimitSamples[i]:X16}");
            }

            output.WriteLine($"  Distinct raw values: {distinctLimits.Length}");
            output.WriteLine();

            if (limits.Locked)
            {
                output.WriteLine("RESULT: READ_ONLY_PASS__WRITE_LOCKED");
                output.WriteLine(
                    "P1 write qualification is NOT authorized: PKG_PWR_LIM_LOCK is set.");
                return 0;
            }

            if (distinctLimits.Length != 1)
            {
                output.WriteLine("RESULT: READ_ONLY_PASS__DYNAMIC_LIMITS_OBSERVED");
                output.WriteLine(
                    "P1 write qualification is deferred: 0x610 changed during the short observation.");
                return 0;
            }

            output.WriteLine("RESULT: READ_ONLY_PASS__WRITE_TEST_CANDIDATE");
            output.WriteLine(
                "0x610 is readable, unlocked and stable during this short observation.");
            output.WriteLine(
                "This does NOT prove that HP/Intel DTT will preserve a future write under load.");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine("RESULT: FAIL_CLOSED");
            output.WriteLine($"Reason: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static ulong ReadMsr(
        PawnIoModuleSession session,
        uint msr)
    {
        var values = session.Execute(
            "ioctl_read_msr",
            new ulong[] { msr },
            1);

        if (values.Length != 1)
        {
            throw new InvalidDataException(
                $"IntelMSR returned {values.Length} cells for MSR 0x{msr:X}.");
        }

        return values[0];
    }

    private static void PrintLimit(
        TextWriter output,
        string name,
        IntelRaplPowerLimit limit)
    {
        output.WriteLine($"  {name} power    : {limit.PowerWatts:0.###} W (raw {limit.RawPower})");
        output.WriteLine($"  {name} enabled  : {limit.Enabled}");
        output.WriteLine($"  {name} clamp    : {limit.Clamp}");
        output.WriteLine($"  {name} time     : {limit.TimeWindowSeconds:0.######} s (raw 0x{limit.RawTimeWindow:X2})");
    }
}
