using VictusFanControl.Hardware.Intel;

namespace VictusFanControl.CpuProbe;

// Pure policy: the physical adapter can write ONLY 0x610. Enable, clamp,
// time windows, lock and reserved bits are never part of this test's ownership.
internal static class RaplWritePolicy
{
    internal const ulong Pl1Mask = 0x7FFFUL;
    internal const ulong Pl2Mask = 0x7FFFUL << 32;
    internal const ulong OwnedMask = Pl1Mask | Pl2Mask;

    internal static ulong BuildReducedLimit(ulong baseline, IntelRaplUnits units)
    {
        var limits = ValidateBaseline(baseline, units);

        // A downward-only 20% change, rounded DOWN to the register's power unit.
        var pl1 = (ulong)Math.Floor(limits.Pl1.RawPower * 0.8);
        var pl2 = (ulong)Math.Floor(limits.Pl2.RawPower * 0.8);
        return BuildFromRaw(baseline, limits, units, pl1, pl2, "WRITE_REFUSED_INVALID_REDUCTION");
    }

    internal static ulong BuildRequestedLimit(
        ulong baseline,
        IntelRaplUnits units,
        double pl1Watts,
        double pl2Watts)
    {
        var limits = ValidateBaseline(baseline, units);
        if (!double.IsFinite(pl1Watts) || !double.IsFinite(pl2Watts) ||
            pl1Watts < 10 || pl1Watts > 200 || pl2Watts < pl1Watts || pl2Watts > 250)
            throw new InvalidOperationException("WRITE_REFUSED_INVALID_EXPLICIT_LIMIT");

        // Encode downward to the hardware power unit: the physical value can
        // never exceed the operator request.
        var pl1 = (ulong)Math.Floor((pl1Watts / units.PowerWatts) + 1e-12);
        var pl2 = (ulong)Math.Floor((pl2Watts / units.PowerWatts) + 1e-12);
        return BuildFromRaw(baseline, limits, units, pl1, pl2, "WRITE_REFUSED_INVALID_EXPLICIT_LIMIT");
    }

    private static IntelRaplPackageLimit ValidateBaseline(ulong baseline, IntelRaplUnits units)
    {
        var limits = IntelRaplCodec.DecodePackagePowerLimit(baseline, units);
        if (limits.Locked || !limits.Pl1.Enabled || !limits.Pl2.Enabled)
            throw new InvalidOperationException("WRITE_REFUSED_LOCKED_OR_DISABLED_LIMIT");
        if (limits.Pl1.PowerWatts is < 12.5 or > 200 ||
            limits.Pl2.PowerWatts < limits.Pl1.PowerWatts || limits.Pl2.PowerWatts > 250)
            throw new InvalidOperationException("WRITE_REFUSED_IMPLAUSIBLE_BASELINE");
        return limits;
    }

    private static ulong BuildFromRaw(
        ulong baseline,
        IntelRaplPackageLimit limits,
        IntelRaplUnits units,
        ulong pl1,
        ulong pl2,
        string error)
    {
        if (pl1 == 0 || pl2 == 0 || pl1 > Pl1Mask || pl2 > 0x7FFFUL ||
            pl1 * units.PowerWatts < 10 || pl2 < pl1 ||
            pl1 >= limits.Pl1.RawPower || pl2 >= limits.Pl2.RawPower)
            throw new InvalidOperationException(error);
        return (baseline & ~OwnedMask) | pl1 | (pl2 << 32);
    }

    internal static (ulong Value, string Status) PlanRestore(
        ulong baseline, ulong applied, ulong current)
    {
        if (current == baseline) return (current, "ALREADY_BASELINE");
        if ((current & (1UL << 63)) != 0) return (current, "RESTORE_BLOCKED_LOCK");
        if ((current & ~OwnedMask) != (baseline & ~OwnedMask))
            return (current, "RESTORE_DEFERRED_NONPOWER_FIELDS_CHANGED");

        var restored = current;
        var conflict = false;
        foreach (var mask in new[] { Pl1Mask, Pl2Mask })
        {
            var field = current & mask;
            if (field == (applied & mask))
                restored = (restored & ~mask) | (baseline & mask);
            else if (field != (baseline & mask)) conflict = true;
        }
        return (restored, conflict ? "EXTERNAL_CHANGE_PRESERVED" : "RESTORE_PLANNED");
    }
}
