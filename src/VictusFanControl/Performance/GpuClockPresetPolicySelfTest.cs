namespace VictusFanControl.Performance;

internal static class GpuClockPresetPolicySelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            RequestedVictusValuesAreExact(output);
            AcAndBatteryResolveIndependently(output);
            UnknownSourceHasNoAuthority(output);
            DisabledGpuPresetHasNoRequest(output);
            InvalidGpuPresetIsRejected(output);
            CompositePerformanceConfigurationKeepsCpuSeparate(output);

            output.WriteLine(
                "GPU clock AC/battery preset policy self-test: PASS (210-1850 AC / 210-1200 Battery, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU clock AC/battery preset policy self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void RequestedVictusValuesAreExact(
        TextWriter output)
    {
        var presets =
            GpuClockPresetSet.UserRequestedVictus;

        Require(
            presets.Ac ==
                new GpuClockPreset(
                    true,
                    210,
                    1850),
            "AC GPU preset must be exactly 210..1850 MHz");

        Require(
            presets.Battery ==
                new GpuClockPreset(
                    true,
                    210,
                    1200),
            "Battery GPU preset must be exactly 210..1200 MHz");

        output.WriteLine(
            "PASS requested GPU caps are fixed in the preset model as AC 210..1850 and Battery 210..1200 MHz");
    }

    private static void AcAndBatteryResolveIndependently(
        TextWriter output)
    {
        var policy =
            new GpuClockPresetPolicy(
                GpuClockPresetSet.UserRequestedVictus);

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
                new GpuClockLimitRequest(
                    210,
                    1850),
            "AC GPU request");

        Require(
            battery.SourceKnown &&
            battery.Enabled &&
            battery.Slot ==
                PerformancePresetSlot.Battery &&
            battery.Request ==
                new GpuClockLimitRequest(
                    210,
                    1200),
            "Battery GPU request");

        output.WriteLine(
            "PASS AC/Battery source selection maps to independent GPU locked-clock ranges");
    }

    private static void UnknownSourceHasNoAuthority(
        TextWriter output)
    {
        var selection =
            new GpuClockPresetPolicy(
                GpuClockPresetSet.UserRequestedVictus)
            .Resolve(
                PerformancePowerSourceKind.Unknown);

        Require(
            !selection.SourceKnown &&
            !selection.Enabled &&
            selection.Request is null &&
            selection.Slot is null,
            "unknown source must have zero GPU preset authority");

        output.WriteLine(
            "PASS unknown source cannot authorize a GPU clock write");
    }

    private static void DisabledGpuPresetHasNoRequest(
        TextWriter output)
    {
        var policy =
            new GpuClockPresetPolicy(
                new GpuClockPresetSet(
                    Ac:
                        new GpuClockPreset(
                            true,
                            210,
                            1850),

                    Battery:
                        new GpuClockPreset(
                            false,
                            210,
                            1200)));

        var battery =
            policy.Resolve(
                PerformancePowerSourceKind.Battery);

        Require(
            battery.SourceKnown &&
            !battery.Enabled &&
            battery.Request is null,
            "disabled Battery GPU preset exposes no request");

        output.WriteLine(
            "PASS disabled GPU slot preserves configuration but produces release/no-control intent");
    }

    private static void InvalidGpuPresetIsRejected(
        TextWriter output)
    {
        RequireThrows<ArgumentOutOfRangeException>(
            () =>
                new GpuClockPresetPolicy(
                    new GpuClockPresetSet(
                        new GpuClockPreset(
                            true,
                            0,
                            1850),
                        GpuClockPreset.Disabled)),
            "zero minimum clock rejected");

        RequireThrows<ArgumentOutOfRangeException>(
            () =>
                new GpuClockPresetPolicy(
                    new GpuClockPresetSet(
                        GpuClockPreset.Disabled,
                        new GpuClockPreset(
                            true,
                            1500,
                            1200))),
            "max below min rejected");

        output.WriteLine(
            "PASS invalid enabled GPU clock presets are rejected before runtime selection");
    }

    private static void CompositePerformanceConfigurationKeepsCpuSeparate(
        TextWriter output)
    {
        var configuration =
            PerformancePresetConfiguration
                .UserRequestedGpuWithCpuDisabled;

        Require(
            configuration.Cpu ==
                CpuPowerPresetSet.Disabled,
            "GPU preset defaults do not invent CPU wattage");

        Require(
            configuration.Gpu ==
                GpuClockPresetSet.UserRequestedVictus,
            "composite configuration carries requested GPU ranges");

        output.WriteLine(
            "PASS shared AC/Battery performance configuration keeps CPU and GPU ownership independent");
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
