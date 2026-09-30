using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Intel;

internal static class IntelRaplStabilityProbe
{
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgPowerLimit = 0x610;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        int durationSeconds,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (durationSeconds is < 10 or > 600)
        {
            output.WriteLine("RESULT: FAIL_CLOSED");
            output.WriteLine("Reason: duration must be between 10 and 600 seconds.");
            return 2;
        }

        output.WriteLine("VictusFanControl - Intel RAPL P0.5 READ-ONLY stability observer");
        output.WriteLine("No MSR write path is used.");
        output.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var targetReason))
        {
            output.WriteLine("RESULT: FAIL_CLOSED_TARGET_MISMATCH");
            output.WriteLine($"Reason: {targetReason}");
            return 3;
        }

        var modulePath = Path.Combine(modulesDirectory, "IntelMSR.bin");

        try
        {
            using var intel = new IntelMsrReader(modulePath);
            var cpuLoad = new WindowsCpuLoadReader();

            var unitsRaw = intel.ReadMsr(MsrRaplPowerUnit);
            var units = IntelRaplCodec.DecodeUnits(unitsRaw);

            var baselineRaw = intel.ReadMsr(MsrPkgPowerLimit);
            var baseline = IntelRaplCodec.DecodePackagePowerLimit(
                baselineRaw,
                units);

            output.WriteLine($"Target        : {Hp8C40TargetProfile.Instance.Id}");
            output.WriteLine($"Duration      : {durationSeconds} s");
            output.WriteLine($"Baseline 0x610: 0x{baselineRaw:X16}");
            output.WriteLine($"Baseline PL1 : {baseline.Pl1.PowerWatts:0.###} W");
            output.WriteLine($"Baseline PL2 : {baseline.Pl2.PowerWatts:0.###} W");
            output.WriteLine($"Baseline lock: {(baseline.Locked ? 1 : 0)}");
            output.WriteLine();
            output.WriteLine("sec,raw610,pl1W,pl2W,lock,cpuPowerW,cpuTempC,cpuLoadPct");

            // Prime differential counters.
            _ = intel.ReadPackagePowerW();
            _ = cpuLoad.ReadTotalLoadPercent();
            await Task.Delay(1000, cancellationToken);

            var distinct = new HashSet<ulong>();
            var changedSamples = 0;
            var lockedSamples = 0;
            var maximumPower = 0.0;
            var maximumTemperature = 0.0;
            var maximumLoad = 0.0;

            for (var second = 1; second <= durationSeconds; second++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var raw = intel.ReadMsr(MsrPkgPowerLimit);
                var decoded = IntelRaplCodec.DecodePackagePowerLimit(
                    raw,
                    units);

                var power = intel.ReadPackagePowerW();
                var temperature = intel.ReadPackageTemperatureC();
                var load = cpuLoad.ReadTotalLoadPercent();

                distinct.Add(raw);
                if (raw != baselineRaw)
                {
                    changedSamples++;
                }

                if (decoded.Locked)
                {
                    lockedSamples++;
                }

                if (power.HasValue)
                {
                    maximumPower = Math.Max(maximumPower, power.Value);
                }

                if (temperature.HasValue)
                {
                    maximumTemperature = Math.Max(
                        maximumTemperature,
                        temperature.Value);
                }

                if (load.HasValue)
                {
                    maximumLoad = Math.Max(maximumLoad, load.Value);
                }

                output.WriteLine(
                    $"{second}," +
                    $"0x{raw:X16}," +
                    $"{decoded.Pl1.PowerWatts:0.###}," +
                    $"{decoded.Pl2.PowerWatts:0.###}," +
                    $"{(decoded.Locked ? 1 : 0)}," +
                    $"{Format(power)}," +
                    $"{Format(temperature)}," +
                    $"{Format(load)}");

                if (second < durationSeconds)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }

            output.WriteLine();
            output.WriteLine($"Distinct 0x610 values : {distinct.Count}");
            output.WriteLine($"Changed samples       : {changedSamples}/{durationSeconds}");
            output.WriteLine($"Locked samples        : {lockedSamples}/{durationSeconds}");
            output.WriteLine($"Max CPU package power : {maximumPower:0.###} W");
            output.WriteLine($"Max CPU package temp  : {maximumTemperature:0.###} C");
            output.WriteLine($"Max CPU load          : {maximumLoad:0.###} %");

            if (lockedSamples > 0)
            {
                output.WriteLine("RESULT: READ_ONLY_PASS__LOCK_APPEARED");
                return 0;
            }

            if (changedSamples > 0)
            {
                output.WriteLine("RESULT: READ_ONLY_PASS__DYNAMIC_LIMITS_OBSERVED");
                return 0;
            }

            output.WriteLine("RESULT: READ_ONLY_PASS__LIMITS_STABLE");
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            output.WriteLine();
            output.WriteLine("RESULT: FAIL_CLOSED");
            output.WriteLine($"Reason: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    private static string Format(double? value) =>
        value.HasValue ? value.Value.ToString("0.###") : "";
}
