using System.Diagnostics;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Active bounded qualification of the first unvalidated 8C40 fan levels.
///
/// This deliberately bypasses the production backend's 30-only envelope, but
/// only through Hp8C40FanLevelQualificationControl, which is hard-limited to
/// equal 30..32. Firmware authority is restored and FF/FF verified after every
/// individual level.
/// </summary>
public static class Hp8C40FanLevelQualificationTest
{
    private static readonly byte[] Levels = [30, 31, 32];

    private const int SamplesPerLevel = 6;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SetpointAckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestoreAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumStepWindow = TimeSpan.FromSeconds(12);

    private const double MaximumCpuTemperatureC = 80;
    private const double MaximumGpuTemperatureC = 75;
    private const double MaximumCpuPowerW = 50;
    private const double MaximumGpuPowerW = 70;
    private const double MinimumExpectedRunningRpm = 1000;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("HP 8C40 adjacent fan-level qualification");
        Console.WriteLine("ACTIVE hardware test: equal levels 30 -> 31 -> 32.");
        Console.WriteLine("Each level is followed by FF/FF + LegacyDefault restore and verification.");
        Console.WriteLine("Production backend remains 30-only until results are reviewed.");
        Console.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var identityReason))
        {
            Console.Error.WriteLine($"Qualification refused: {identityReason}");
            return 71;
        }

        var conflict = FindKnownConflictingControllerProcess();
        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"Qualification refused while '{conflict}' is running. " +
                "Close OmenMon/OmenMon-Reborn/VictusFanControl GUI first.");
            return 72;
        }

        using var reader = new HardwareTelemetryReader(modulesDirectory);
        if (!reader.BackendsInitialized)
        {
            Console.Error.WriteLine("Qualification refused: telemetry backends are not initialized.");
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.Error.WriteLine(line);
            }

            return 73;
        }

        var control = new Hp8C40FanLevelQualificationControl();
        var probe = new Hp8C40EcControlStateProbe(modulesDirectory);

        Console.WriteLine("Priming differential telemetry counters...");
        _ = reader.ReadSnapshot();
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var baseline = reader.ReadSnapshot();
        Console.WriteLine("Firmware baseline:");
        ConsoleTelemetryPrinter.Print(baseline);
        EnsureSafeTelemetry(hardware, baseline);

        var baselineEc = probe.Read();
        Console.WriteLine($"EC baseline: {baselineEc}");
        EnsureFirmwareReleasedAndGuardsSane(baselineEc);

        var results = new List<LevelResult>();

        foreach (var level in Levels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine($"=== Qualification level {level}/{level} ===");

            var pre = reader.ReadSnapshot();
            EnsureSafeTelemetry(hardware, pre);

            var preEc = probe.Read();
            EnsureFirmwareReleasedAndGuardsSane(preEc);

            Exception? stepFailure = null;
            Exception? restoreFailure = null;
            var writeAttempted = false;
            var stepStarted = Stopwatch.GetTimestamp();
            var cpuSamples = new List<double>(SamplesPerLevel);
            var gpuSamples = new List<double>(SamplesPerLevel);

            try
            {
                Console.WriteLine($"Sending qualification WMI SetFanLevel({level},{level})...");
                writeAttempted = true;
                control.SetEqualLevel(level);

                var acknowledged = await WaitForSetpointAsync(
                    probe,
                    level,
                    SetpointAckTimeout,
                    cancellationToken).ConfigureAwait(false);

                Console.WriteLine($"EC setpoint ACK: {acknowledged.CpuSetpoint}/{acknowledged.GpuSetpoint}");

                for (var index = 1; index <= SamplesPerLevel; index++)
                {
                    EnsureStepWindow(stepStarted);

                    await Task.Delay(
                        SampleInterval,
                        cancellationToken).ConfigureAwait(false);

                    var sample = reader.ReadSnapshot();
                    EnsureSafeTelemetry(hardware, sample);

                    var ec = probe.Read();
                    EnsureActiveOwnershipAndGuards(ec, level);

                    if (sample.CpuFanRpm < MinimumExpectedRunningRpm ||
                        sample.GpuFanRpm < MinimumExpectedRunningRpm)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected low fan feedback at {level}/{level}: " +
                            $"CPU={sample.CpuFanRpm:0} RPM GPU={sample.GpuFanRpm:0} RPM.");
                    }

                    cpuSamples.Add(sample.CpuFanRpm!.Value);
                    gpuSamples.Add(sample.GpuFanRpm!.Value);

                    Console.WriteLine(
                        $"sample {index}/{SamplesPerLevel}: " +
                        $"CPU {sample.CpuFanRpm:0} RPM | GPU {sample.GpuFanRpm:0} RPM | " +
                        $"effective CPU {sample.CpuControlTemperatureC:0.0} C | " +
                        $"GPU {sample.GpuTemperatureC:0.0} C");
                }

                var cpuMedian = Median(cpuSamples);
                var gpuMedian = Median(gpuSamples);

                results.Add(new LevelResult(
                    level,
                    cpuMedian,
                    gpuMedian,
                    cpuSamples.Min(),
                    cpuSamples.Max(),
                    gpuSamples.Min(),
                    gpuSamples.Max()));

                Console.WriteLine(
                    $"Level {level}: CPU median={cpuMedian:0} RPM " +
                    $"({cpuSamples.Min():0}-{cpuSamples.Max():0}), " +
                    $"GPU median={gpuMedian:0} RPM " +
                    $"({gpuSamples.Min():0}-{gpuSamples.Max():0}).");
            }
            catch (Exception ex)
            {
                stepFailure = ex;
            }
            finally
            {
                if (writeAttempted)
                {
                    Console.WriteLine("Restoring HP firmware authority...");
                    try
                    {
                        control.RestoreFirmwareAuto();

                        var restored = await WaitForRestoreAsync(
                            probe,
                            RestoreAckTimeout).ConfigureAwait(false);

                        Console.WriteLine($"Restore verified: {restored}");
                    }
                    catch (Exception ex)
                    {
                        restoreFailure = ex;
                        Console.Error.WriteLine(
                            $"CRITICAL: restore failed after level {level}: {ex.Message}");
                    }
                }
            }

            if (restoreFailure is not null)
            {
                return 75;
            }

            if (stepFailure is OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"Qualification cancelled at level {level}; firmware restore was attempted.");
                return 130;
            }

            if (stepFailure is not null)
            {
                Console.Error.WriteLine(
                    $"Qualification stopped safely at level {level}: {stepFailure.Message}");
                return 74;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("Observed qualification summary:");
        foreach (var result in results)
        {
            Console.WriteLine(
                $"  {result.Level}/{result.Level}: " +
                $"CPU median {result.CpuMedianRpm:0} RPM " +
                $"[{result.CpuMinimumRpm:0},{result.CpuMaximumRpm:0}] | " +
                $"GPU median {result.GpuMedianRpm:0} RPM " +
                $"[{result.GpuMinimumRpm:0},{result.GpuMaximumRpm:0}]");
        }

        if (results.Count == Levels.Length)
        {
            for (var i = 1; i < results.Count; i++)
            {
                var previous = results[i - 1];
                var current = results[i];

                Console.WriteLine(
                    $"  delta {previous.Level}->{current.Level}: " +
                    $"CPU {current.CpuMedianRpm - previous.CpuMedianRpm:+0;-0;0} RPM | " +
                    $"GPU {current.GpuMedianRpm - previous.GpuMedianRpm:+0;-0;0} RPM");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "RESULT: PASS (30/31/32 were accepted by HP WMI, acknowledged by EC, " +
            "returned sustained dual-fan feedback, and restored to FF/FF after every step).");
        Console.WriteLine(
            "NOTE: this result must be reviewed before the production 8C40 validated range is expanded.");

        return 0;
    }

    private static void EnsureSafeTelemetry(
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot)
    {
        var safety = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            DateTimeOffset.UtcNow,
            fanWritePathPresent: true);

        if (!safety.CustomControlPermitted)
        {
            throw new InvalidOperationException(
                "SafetyGate refused qualification telemetry: " +
                string.Join(" | ", safety.Reasons));
        }

        if (snapshot.CpuControlTemperatureC > MaximumCpuTemperatureC ||
            snapshot.GpuTemperatureC > MaximumGpuTemperatureC ||
            snapshot.CpuPackagePowerW > MaximumCpuPowerW ||
            snapshot.GpuPowerW > MaximumGpuPowerW)
        {
            throw new InvalidOperationException(
                "Light-load qualification envelope exceeded. " +
                $"Effective CPU <= {MaximumCpuTemperatureC:0} C / " +
                $"{MaximumCpuPowerW:0} W required; GPU <= " +
                $"{MaximumGpuTemperatureC:0} C / {MaximumGpuPowerW:0} W required.");
        }
    }

    private static void EnsureFirmwareReleasedAndGuardsSane(
        Hp8C40EcControlState state)
    {
        if (state.CpuSetpoint != byte.MaxValue ||
            state.GpuSetpoint != byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Qualification requires firmware-released FF/FF baseline; " +
                $"observed {state.CpuSetpoint}/{state.GpuSetpoint}.");
        }

        EnsureGuardsSane(state);
    }

    private static void EnsureActiveOwnershipAndGuards(
        Hp8C40EcControlState state,
        byte expectedLevel)
    {
        if (state.CpuSetpoint != expectedLevel ||
            state.GpuSetpoint != expectedLevel)
        {
            throw new InvalidOperationException(
                $"Qualification ownership changed unexpectedly at {expectedLevel}/{expectedLevel}: " +
                $"EC={state.CpuSetpoint}/{state.GpuSetpoint}.");
        }

        EnsureGuardsSane(state);
    }

    private static void EnsureGuardsSane(Hp8C40EcControlState state)
    {
        if (state.MaxFan != 0x00 || state.FanSwitch != 0x00)
        {
            throw new InvalidOperationException(
                $"Unexpected HP fan-control guard state: " +
                $"MaxFan=0x{state.MaxFan:X2} FanSwitch=0x{state.FanSwitch:X2}.");
        }
    }

    private static async Task<Hp8C40EcControlState> WaitForSetpointAsync(
        Hp8C40EcControlStateProbe probe,
        byte expectedLevel,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Hp8C40EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            last = probe.Read();
            if (last.CpuSetpoint == expectedLevel &&
                last.GpuSetpoint == expectedLevel)
            {
                EnsureGuardsSane(last);
                return last;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"EC did not acknowledge {expectedLevel}/{expectedLevel} within " +
            $"{timeout.TotalSeconds:0.0} s. Last={last?.CpuSetpoint.ToString() ?? "n/a"}/" +
            $"{last?.GpuSetpoint.ToString() ?? "n/a"}.");
    }

    private static async Task<Hp8C40EcControlState> WaitForRestoreAsync(
        Hp8C40EcControlStateProbe probe,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        Hp8C40EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            last = probe.Read();

            if (last.CpuSetpoint == byte.MaxValue &&
                last.GpuSetpoint == byte.MaxValue)
            {
                EnsureGuardsSane(last);
                return last;
            }

            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Firmware restore did not reach FF/FF within {timeout.TotalSeconds:0.0} s. " +
            $"Last={last?.CpuSetpoint.ToString() ?? "n/a"}/" +
            $"{last?.GpuSetpoint.ToString() ?? "n/a"}.");
    }

    private static void EnsureStepWindow(long started)
    {
        if (Stopwatch.GetElapsedTime(started) > MaximumStepWindow)
        {
            throw new TimeoutException(
                $"Qualification step exceeded {MaximumStepWindow.TotalSeconds:0} seconds.");
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            throw new InvalidOperationException("No fan samples were captured.");
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private static string? FindKnownConflictingControllerProcess()
    {
        foreach (var processName in new[]
                 {
                     "OmenMon",
                     "OmenMon-Reborn",
                     "VictusFanControl.App"
                 })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return processName;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }

    private sealed record LevelResult(
        byte Level,
        double CpuMedianRpm,
        double GpuMedianRpm,
        double CpuMinimumRpm,
        double CpuMaximumRpm,
        double GpuMinimumRpm,
        double GpuMaximumRpm);
}
