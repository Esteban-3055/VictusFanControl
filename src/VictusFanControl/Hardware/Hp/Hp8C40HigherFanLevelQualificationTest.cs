using System.Diagnostics;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Bounded active qualification of equal HP 8C40 levels 37..40.
/// Firmware authority is restored and FF/FF verified after every individual
/// level. Production remains capped at 36 until these results are reviewed.
/// </summary>
public static class Hp8C40HigherFanLevelQualificationTest
{
    // Levels 30..36 are already physically qualified. Keep this harness
    // isolated to the next unvalidated equal-level block.
    private static readonly byte[] Levels = [37, 38, 39, 40];

    private const int SamplesPerLevel = 6;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EcRetryDelay = TimeSpan.FromMilliseconds(150);
    private const int EcEvidenceReadAttempts = 3;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SetpointAckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestoreAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumStepWindow = TimeSpan.FromSeconds(14);

    private const double MaximumCpuTemperatureC = 80;
    private const double MaximumGpuTemperatureC = 75;
    private const double MaximumCpuPowerW = 50;
    private const double MaximumGpuPowerW = 70;
    private const double MinimumExpectedRunningRpm = 1000;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("HP 8C40 higher fan-level qualification");
        Console.WriteLine("ACTIVE hardware test: equal levels 37 -> 38 -> 39 -> 40.");
        Console.WriteLine("Levels 30 through 36 are already physically qualified and production-integrated.");
        Console.WriteLine("Each remaining level is followed by FF/FF + LegacyDefault restore and verification.");
        Console.WriteLine("Production backend remains capped at 36 until results are reviewed.");
        Console.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var identityReason))
        {
            Console.Error.WriteLine($"Qualification refused: {identityReason}");
            return 81;
        }

        var conflict = FindKnownConflictingControllerProcess();
        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"Qualification refused while '{conflict}' is running. " +
                "Close OmenMon/OmenMon-Reborn/VictusFanControl GUI first.");
            return 82;
        }

        using var reader = new HardwareTelemetryReader(modulesDirectory);
        if (!reader.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "Qualification refused: telemetry backends are not initialized.");
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.Error.WriteLine(line);
            }

            return 83;
        }

        var control = new Hp8C40HigherFanLevelQualificationControl();
        var probe = new Hp8C40EcControlStateProbe(modulesDirectory);

        Console.WriteLine("Priming differential telemetry counters...");
        _ = reader.ReadSnapshot();
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var baseline = reader.ReadSnapshot();
        Console.WriteLine("Firmware baseline:");
        ConsoleTelemetryPrinter.Print(baseline);
        EnsureSafeTelemetry(hardware, baseline);

        Hp8C40EcControlState baselineEc;
        try
        {
            baselineEc = await ReadControlEvidenceWithRetryAsync(
                probe,
                "firmware baseline",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Qualification refused: EC baseline could not be read reliably: {ex.Message}");
            return 86;
        }

        Console.WriteLine($"EC baseline (narrow): {FormatControlEvidence(baselineEc)}");
        EnsureFirmwareReleasedAndGuardsSane(baselineEc);

        var results = new List<LevelResult>();

        foreach (var level in Levels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine($"=== Qualification level {level}/{level} ===");

            var pre = reader.ReadSnapshot();
            EnsureSafeTelemetry(hardware, pre);

            Hp8C40EcControlState preEc;
            try
            {
                preEc = await ReadControlEvidenceWithRetryAsync(
                    probe,
                    $"level {level} pre-write",
                    cancellationToken).ConfigureAwait(false);
                EnsureFirmwareReleasedAndGuardsSane(preEc);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Qualification stopped before any {level}/{level} write: " +
                    $"EC pre-write evidence unavailable: {ex.Message}");
                return 86;
            }

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

                Console.WriteLine(
                    $"EC setpoint ACK: {acknowledged.CpuSetpoint}/{acknowledged.GpuSetpoint}");

                // Prior qualification showed the GPU tach can lag the setpoint
                // acknowledgement for the first sample. Let the physical fans
                // settle before collecting the calibration window.
                await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);

                for (var index = 1; index <= SamplesPerLevel; index++)
                {
                    EnsureStepWindow(stepStarted);

                    var sample = reader.ReadSnapshot();
                    EnsureSafeTelemetry(hardware, sample);

                    var ec = await ReadControlEvidenceWithRetryAsync(
                        probe,
                        $"level {level} active sample {index}",
                        cancellationToken).ConfigureAwait(false);
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

                    if (index < SamplesPerLevel)
                    {
                        await Task.Delay(
                            SampleInterval,
                            cancellationToken).ConfigureAwait(false);
                    }
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

                        Console.WriteLine(
                            $"Restore verified: {FormatControlEvidence(restored)}");
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
                return 85;
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
                return 84;
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

        for (var i = 1; i < results.Count; i++)
        {
            var previous = results[i - 1];
            var current = results[i];

            Console.WriteLine(
                $"  delta {previous.Level}->{current.Level}: " +
                $"CPU {current.CpuMedianRpm - previous.CpuMedianRpm:+0;-0;0} RPM | " +
                $"GPU {current.GpuMedianRpm - previous.GpuMedianRpm:+0;-0;0} RPM");
        }

        Console.WriteLine();
        Console.WriteLine(
            "RESULT: PASS (37/38/39/40 were accepted by HP WMI, acknowledged by EC, " +
            "returned sustained dual-fan feedback, and restored to FF/FF after every step).");
        Console.WriteLine(
            "NOTE: production remains capped at 36 until this result is reviewed.");

        return 0;
    }

    private static async Task<Hp8C40EcControlState> ReadControlEvidenceWithRetryAsync(
        Hp8C40EcControlStateProbe probe,
        string context,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= EcEvidenceReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return probe.ReadControlEvidence();
            }
            catch (Exception ex)
                when (ex is IOException or TimeoutException or InvalidDataException)
            {
                lastError = ex;
                Console.WriteLine(
                    $"EC transient during {context}: retry {attempt}/{EcEvidenceReadAttempts}: " +
                    $"{ex.Message}");

                if (attempt < EcEvidenceReadAttempts)
                {
                    await Task.Delay(
                        EcRetryDelay,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            $"EC control evidence remained unavailable during {context} after " +
            $"{EcEvidenceReadAttempts} high-level attempts.",
            lastError);
    }

    private static string FormatControlEvidence(Hp8C40EcControlState state) =>
        $"level CPU={state.CpuSetpoint} GPU={state.GpuSetpoint} | " +
        $"max=0x{state.MaxFan:X2} switch=0x{state.FanSwitch:X2} | " +
        $"RPM CPU={state.CpuRpm} GPU={state.GpuRpm}";

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
                "Light-load qualification envelope exceeded.");
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

            try
            {
                last = await ReadControlEvidenceWithRetryAsync(
                    probe,
                    $"setpoint ACK {expectedLevel}/{expectedLevel}",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

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
            $"{timeout.TotalSeconds:0.0} s.");
    }

    private static async Task<Hp8C40EcControlState> WaitForRestoreAsync(
        Hp8C40EcControlStateProbe probe,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        Hp8C40EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            try
            {
                last = await ReadControlEvidenceWithRetryAsync(
                    probe,
                    "firmware restore verification",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (last.CpuSetpoint == byte.MaxValue &&
                last.GpuSetpoint == byte.MaxValue)
            {
                EnsureGuardsSane(last);
                return last;
            }

            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Firmware restore did not reach FF/FF within {timeout.TotalSeconds:0.0} s.");
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
