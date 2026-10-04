namespace VictusFanControl.Performance;

internal static class CpuPowerPresetPolicySelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            BothPresetsAreIndependent(output);
            UnknownSourceHasNoAuthority(output);
            DisabledBatteryPresetSelectsReleaseIntentOnly(output);
            DisabledPresetsDoNotInventLimits(output);
            InvalidEnabledPresetsAreRejected(output);

            output.WriteLine(
                "CPU power AC/battery preset policy self-test: PASS (pure selection, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "CPU power AC/battery preset policy self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void BothPresetsAreIndependent(
        TextWriter output)
    {
        var policy =
            new CpuPowerPresetPolicy(
                new CpuPowerPresetSet(
                    Ac:
                        new CpuPowerPreset(
                            Enabled: true,
                            Pl1Watts: 30,
                            Pl2Watts: 60),

                    Battery:
                        new CpuPowerPreset(
                            Enabled: true,
                            Pl1Watts: 15,
                            Pl2Watts: 30)));

        var ac =
            policy.Resolve(
                PerformancePowerSourceKind.Ac);

        var battery =
            policy.Resolve(
                PerformancePowerSourceKind.Battery);

        Require(
            ac.SourceKnown &&
            ac.Enabled &&
            ac.Slot ==
                PerformancePresetSlot.Ac &&
            ac.Request ==
                new CpuPowerLimitRequest(30, 60),
            "AC preset resolves independently");

        Require(
            battery.SourceKnown &&
            battery.Enabled &&
            battery.Slot ==
                PerformancePresetSlot.Battery &&
            battery.Request ==
                new CpuPowerLimitRequest(15, 30),
            "Battery preset resolves independently");

        output.WriteLine(
            "PASS AC and Battery presets select independent PL1/PL2 requests");
    }

    private static void UnknownSourceHasNoAuthority(
        TextWriter output)
    {
        var policy =
            new CpuPowerPresetPolicy(
                new CpuPowerPresetSet(
                    new CpuPowerPreset(true, 30, 60),
                    new CpuPowerPreset(true, 15, 30)));

        var selection =
            policy.Resolve(
                PerformancePowerSourceKind.Unknown);

        Require(
            !selection.SourceKnown &&
            !selection.Enabled &&
            selection.Request is null &&
            selection.Slot is null,
            "unknown source selects no preset");

        output.WriteLine(
            "PASS unknown AC/battery source cannot authorize a preset write");
    }

    private static void DisabledBatteryPresetSelectsReleaseIntentOnly(
        TextWriter output)
    {
        var policy =
            new CpuPowerPresetPolicy(
                new CpuPowerPresetSet(
                    Ac:
                        new CpuPowerPreset(
                            true,
                            30,
                            60),

                    Battery:
                        new CpuPowerPreset(
                            false,
                            15,
                            30)));

        var battery =
            policy.Resolve(
                PerformancePowerSourceKind.Battery);

        Require(
            battery.SourceKnown &&
            battery.Slot ==
                PerformancePresetSlot.Battery &&
            !battery.Enabled &&
            battery.Request is null,
            "disabled battery preset exposes no request");

        output.WriteLine(
            "PASS disabled Battery preset can later map to release/OEM without losing configured watts");
    }

    private static void DisabledPresetsDoNotInventLimits(
        TextWriter output)
    {
        var policy =
            new CpuPowerPresetPolicy(
                CpuPowerPresetSet.Disabled);

        var ac =
            policy.Resolve(
                PerformancePowerSourceKind.Ac);

        var battery =
            policy.Resolve(
                PerformancePowerSourceKind.Battery);

        Require(
            !ac.Enabled &&
            ac.Request is null &&
            !battery.Enabled &&
            battery.Request is null,
            "disabled defaults have no power request");

        output.WriteLine(
            "PASS default preset set is Disabled/Disabled and invents no wattage");
    }

    private static void InvalidEnabledPresetsAreRejected(
        TextWriter output)
    {
        RequireThrows<ArgumentOutOfRangeException>(
            () =>
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        new CpuPowerPreset(
                            true,
                            9,
                            40),
                        CpuPowerPreset.Disabled)),
            "PL1 below minimum is rejected");

        RequireThrows<ArgumentOutOfRangeException>(
            () =>
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        CpuPowerPreset.Disabled,
                        new CpuPowerPreset(
                            true,
                            40,
                            30))),
            "PL2 below PL1 is rejected");

        RequireThrows<ArgumentOutOfRangeException>(
            () =>
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        new CpuPowerPreset(
                            true,
                            double.NaN,
                            40),
                        CpuPowerPreset.Disabled)),
            "non-finite enabled preset is rejected");

        output.WriteLine(
            "PASS invalid enabled AC/Battery presets are rejected before runtime selection");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private static void RequireThrows<T>(
        Action action,
        string label)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(label);
    }
}
