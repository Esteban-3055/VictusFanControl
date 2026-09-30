namespace VictusFanControl.Hardware.Intel;

internal readonly record struct IntelRaplUnits(
    double PowerWatts,
    double EnergyJoules,
    double TimeSeconds);

internal readonly record struct IntelRaplPowerLimit(
    double PowerWatts,
    bool Enabled,
    bool Clamp,
    double TimeWindowSeconds,
    ushort RawPower,
    byte RawTimeWindow);

internal readonly record struct IntelRaplPackageLimit(
    IntelRaplPowerLimit Pl1,
    IntelRaplPowerLimit Pl2,
    bool Locked,
    ulong Raw);

internal readonly record struct IntelRaplPackagePowerInfo(
    double ThermalSpecWatts,
    double MinimumWatts,
    double MaximumWatts,
    double MaximumTimeWindowSeconds,
    ulong Raw);

internal static class IntelRaplCodec
{
    public static IntelRaplUnits DecodeUnits(ulong raw)
    {
        var powerExponent = (int)(raw & 0x0F);
        var energyExponent = (int)((raw >> 8) & 0x1F);
        var timeExponent = (int)((raw >> 16) & 0x0F);

        var power = Math.Pow(0.5, powerExponent);
        var energy = Math.Pow(0.5, energyExponent);
        var time = Math.Pow(0.5, timeExponent);

        if (!double.IsFinite(power) || power <= 0 ||
            !double.IsFinite(energy) || energy <= 0 ||
            !double.IsFinite(time) || time <= 0)
        {
            throw new InvalidDataException("Decoded Intel RAPL units are invalid.");
        }

        return new IntelRaplUnits(power, energy, time);
    }

    public static IntelRaplPackageLimit DecodePackagePowerLimit(
        ulong raw,
        IntelRaplUnits units)
    {
        var pl1RawPower = (ushort)(raw & 0x7FFF);
        var pl1Enabled = (raw & (1UL << 15)) != 0;
        var pl1Clamp = (raw & (1UL << 16)) != 0;
        var pl1RawTime = (byte)((raw >> 17) & 0x7F);

        var pl2RawPower = (ushort)((raw >> 32) & 0x7FFF);
        var pl2Enabled = (raw & (1UL << 47)) != 0;
        var pl2Clamp = (raw & (1UL << 48)) != 0;
        var pl2RawTime = (byte)((raw >> 49) & 0x7F);

        var pl1 = new IntelRaplPowerLimit(
            pl1RawPower * units.PowerWatts,
            pl1Enabled,
            pl1Clamp,
            DecodeTimeWindow(pl1RawTime, units.TimeSeconds),
            pl1RawPower,
            pl1RawTime);

        var pl2 = new IntelRaplPowerLimit(
            pl2RawPower * units.PowerWatts,
            pl2Enabled,
            pl2Clamp,
            DecodeTimeWindow(pl2RawTime, units.TimeSeconds),
            pl2RawPower,
            pl2RawTime);

        return new IntelRaplPackageLimit(
            pl1,
            pl2,
            Locked: (raw & (1UL << 63)) != 0,
            Raw: raw);
    }

    public static IntelRaplPackagePowerInfo DecodePackagePowerInfo(
        ulong raw,
        IntelRaplUnits units)
    {
        var thermalRaw = (ushort)(raw & 0x7FFF);
        var minimumRaw = (ushort)((raw >> 16) & 0x7FFF);
        var maximumRaw = (ushort)((raw >> 32) & 0x7FFF);
        var maximumTimeRaw = (byte)((raw >> 48) & 0x3F);

        return new IntelRaplPackagePowerInfo(
            ThermalSpecWatts: thermalRaw * units.PowerWatts,
            MinimumWatts: minimumRaw * units.PowerWatts,
            MaximumWatts: maximumRaw * units.PowerWatts,
            MaximumTimeWindowSeconds: maximumTimeRaw * units.TimeSeconds,
            Raw: raw);
    }

    private static double DecodeTimeWindow(
        byte encoded,
        double timeUnitSeconds)
    {
        var y = encoded & 0x1F;
        var z = (encoded >> 5) & 0x03;

        return Math.Pow(2.0, y) *
               (1.0 + (z / 4.0)) *
               timeUnitSeconds;
    }
}

internal static class IntelRaplCodecSelfTest
{
    public static int Run(TextWriter output)
    {
        try
        {
            const ulong unitsRaw =
                3UL |
                (14UL << 8) |
                (10UL << 16);

            var units = IntelRaplCodec.DecodeUnits(unitsRaw);

            RequireClose(units.PowerWatts, 0.125, "power unit");
            RequireClose(units.EnergyJoules, 1.0 / 16384.0, "energy unit");
            RequireClose(units.TimeSeconds, 1.0 / 1024.0, "time unit");

            const ushort pl1Raw = 360; // 45 W at 0.125 W/unit.
            const ushort pl2Raw = 480; // 60 W at 0.125 W/unit.
            const byte pl1Time = 0x6A;
            const byte pl2Time = 0x4A;

            var limitRaw =
                (ulong)pl1Raw |
                (1UL << 15) |
                ((ulong)pl1Time << 17) |
                ((ulong)pl2Raw << 32) |
                (1UL << 47) |
                (1UL << 48) |
                ((ulong)pl2Time << 49);

            var limit = IntelRaplCodec.DecodePackagePowerLimit(
                limitRaw,
                units);

            RequireClose(limit.Pl1.PowerWatts, 45.0, "PL1 power");
            RequireClose(limit.Pl2.PowerWatts, 60.0, "PL2 power");
            Require(limit.Pl1.Enabled, "PL1 enable");
            Require(!limit.Pl1.Clamp, "PL1 clamp");
            Require(limit.Pl2.Enabled, "PL2 enable");
            Require(limit.Pl2.Clamp, "PL2 clamp");
            Require(!limit.Locked, "lock state");

            var locked = IntelRaplCodec.DecodePackagePowerLimit(
                limitRaw | (1UL << 63),
                units);
            Require(locked.Locked, "lock bit");

            const ushort thermalRaw = 360;
            const ushort minimumRaw = 160;
            const ushort maximumRaw = 920;
            const byte maximumTimeRaw = 47;

            var infoRaw =
                (ulong)thermalRaw |
                ((ulong)minimumRaw << 16) |
                ((ulong)maximumRaw << 32) |
                ((ulong)maximumTimeRaw << 48);

            var info = IntelRaplCodec.DecodePackagePowerInfo(infoRaw, units);

            RequireClose(info.ThermalSpecWatts, 45.0, "thermal spec");
            RequireClose(info.MinimumWatts, 20.0, "minimum power");
            RequireClose(info.MaximumWatts, 115.0, "maximum power");
            RequireClose(
                info.MaximumTimeWindowSeconds,
                maximumTimeRaw * units.TimeSeconds,
                "maximum time window");

            output.WriteLine("Intel RAPL codec self-test: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Intel RAPL codec self-test: FAIL - {ex.Message}");
            return 1;
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {name}.");
        }
    }

    private static void RequireClose(
        double actual,
        double expected,
        string name)
    {
        if (Math.Abs(actual - expected) > 1e-9)
        {
            throw new InvalidOperationException(
                $"Assertion failed: {name}; expected {expected}, got {actual}.");
        }
    }
}
