using System.Diagnostics;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Bounded real-hardware validation of the production control path:
/// SafetyGate -> FanControlCoordinator -> Hp88F8FanControlBackend.
/// Automatic fan policy remains disabled.
/// </summary>
public static class Hp88F8IntegratedCoordinatorTest
{
    public const int TestLevel = 30;

    private const int PostAcknowledgementSamples = 6;
    private const double MaximumCustomWindowSeconds = 20.0;
    private const double MaximumSamplingGapSeconds = 3.0;

    private const double MaximumBaselineCpuTemperatureC = 80;
    private const double MaximumBaselineGpuTemperatureC = 75;
    private const double MaximumBaselineCpuPowerW = 50;
    private const double MaximumBaselineGpuPowerW = 70;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("HP 88F8 integrated coordinator hardware gate");
        Console.WriteLine("Route: SafetyGate -> FanControlCoordinator -> Hp88F8FanControlBackend");
        Console.WriteLine("Automatic policy remains OFF.");
        Console.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp88F8TargetProfile.Matches(hardware, out var identityReason))
        {
            Console.Error.WriteLine($"Integrated test refused: {identityReason}");
            return 41;
        }

        var conflictingProcess = FindKnownConflictingControllerProcess();
        if (conflictingProcess is not null)
        {
            Console.Error.WriteLine(
                $"Integrated test refused while '{conflictingProcess}' is running. " +
                "Close OmenMon/VictusFanControl GUI first; OMEN Gaming Hub may remain open.");
            return 42;
        }

        using var reader = new HardwareTelemetryReader(modulesDirectory);
        if (!reader.BackendsInitialized)
        {
            Console.Error.WriteLine("Telemetry backends are not fully initialized; integrated test refused.");
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.Error.WriteLine(line);
            }

            return 43;
        }

        await using var coordinator =
            new FanControlCoordinator(new Hp88F8FanControlBackend(modulesDirectory));

        if (!coordinator.BackendCanWrite)
        {
            Console.Error.WriteLine(
                $"Integrated test refused because backend '{coordinator.BackendName}' is not write-capable.");
            return 44;
        }

        Console.WriteLine("Priming differential telemetry counters...");
        _ = reader.ReadSnapshot();
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var baseline = reader.ReadSnapshot();
        Console.WriteLine("Baseline:");
        ConsoleTelemetryPrinter.Print(baseline);

        var baselineSafety = BuildSafety(hardware, baseline, coordinator.BackendCanWrite);
        if (!baselineSafety.CustomControlPermitted)
        {
            Console.Error.WriteLine("SafetyGate refused integrated custom authority:");
            foreach (var reason in baselineSafety.Reasons)
            {
                Console.Error.WriteLine($"  - {reason}");
            }

            return 45;
        }

        EnsureLightLoadEnvelope(baseline);

        var ecBefore = new Hp88F8EcControlStateProbe(modulesDirectory).Read();
        Console.WriteLine($"EC before : {ecBefore}");

        Exception? testFailure = null;
        Exception? restoreFailure = null;
        var fanWriteMayHaveOccurred = false;
        long? customStarted = null;
        long? previousProgressTick = null;

        try
        {
            var entered = await coordinator.TryEnterCustomAsync(
                baselineSafety,
                cancellationToken).ConfigureAwait(false);

            if (!entered || coordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "FanControlCoordinator did not grant Custom authority.");
            }

            customStarted = Stopwatch.GetTimestamp();
            previousProgressTick = customStarted;
            Console.WriteLine($"Coordinator authority: {coordinator.Authority}");

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            EnsureNoSchedulingGap(ref previousProgressTick, "pre-command telemetry");

            var commandSnapshot = reader.ReadSnapshot();
            ConsoleTelemetryPrinter.Print(commandSnapshot);
            EnsureLightLoadEnvelope(commandSnapshot);

            var commandSafety = BuildSafety(
                hardware,
                commandSnapshot,
                coordinator.BackendCanWrite);

            if (!commandSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped before the integrated fan command: " +
                    string.Join(" | ", commandSafety.Reasons));
            }

            Console.WriteLine();
            Console.WriteLine(
                $"Applying production-path fan command {TestLevel}/{TestLevel}...");

            // The backend's no-write admission exception is the only case that
            // proves SetFanLevel was not attempted after this point.
            fanWriteMayHaveOccurred = true;

            try
            {
                await coordinator.ApplyAsync(
                    new FanCommand(
                        TestLevel,
                        TestLevel,
                        "bounded integrated coordinator hardware validation"),
                    commandSafety,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FanControlAdmissionException)
            {
                fanWriteMayHaveOccurred = false;
                throw;
            }

            // ApplyAsync intentionally blocks while the production backend waits
            // for EC + dual-tach acknowledgement. That elapsed time is not a
            // scheduler gap, so start the post-ACK gap detector from here.
            previousProgressTick = Stopwatch.GetTimestamp();
            EnsureCustomWindow(customStarted.Value);

            Console.WriteLine(
                $"Command acknowledged through production backend. Authority={coordinator.Authority}");
            Console.WriteLine(
                $"EC after ACK: {new Hp88F8EcControlStateProbe(modulesDirectory).Read()}");

            for (var sampleIndex = 1;
                 sampleIndex <= PostAcknowledgementSamples;
                 sampleIndex++)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                EnsureNoSchedulingGap(ref previousProgressTick, "post-ACK supervision");
                EnsureCustomWindow(customStarted.Value);

                var sample = reader.ReadSnapshot();
                ConsoleTelemetryPrinter.Print(sample);
                EnsureLightLoadEnvelope(sample);

                var safety = BuildSafety(
                    hardware,
                    sample,
                    coordinator.BackendCanWrite);

                if (!safety.CustomControlPermitted)
                {
                    throw new InvalidOperationException(
                        "SafetyGate dropped during integrated supervision: " +
                        string.Join(" | ", safety.Reasons));
                }

                var stillSafe = await coordinator.EnforceSafetyAsync(
                    safety,
                    $"integrated hardware supervision sample {sampleIndex}",
                    cancellationToken).ConfigureAwait(false);

                if (!stillSafe || coordinator.Authority != FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        $"Coordinator did not retain validated Custom authority at sample {sampleIndex}. " +
                        $"Authority={coordinator.Authority}.");
                }
            }

            Console.WriteLine(
                $"{PostAcknowledgementSamples} post-ACK samples passed continuous safety/ownership supervision.");
        }
        catch (Exception ex)
        {
            testFailure = ex;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Requesting coordinator handoff to HP firmware...");
            try
            {
                await coordinator.RestoreFirmwareAsync(
                    "Integrated hardware validation completed/aborted.",
                    CancellationToken.None).ConfigureAwait(false);

                Console.WriteLine(
                    $"Coordinator authority after restore: {coordinator.Authority}");
            }
            catch (Exception ex)
            {
                restoreFailure = ex;
                Console.Error.WriteLine(
                    $"CRITICAL: coordinator firmware restore failed: {ex.Message}");
            }
        }

        if (fanWriteMayHaveOccurred)
        {
            try
            {
                var ecAfter = await WaitForFanOverrideReleaseAsync(
                    modulesDirectory,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Console.WriteLine($"EC after restore: {ecAfter}");
            }
            catch (Exception ex)
            {
                restoreFailure ??= ex;
                Console.Error.WriteLine(
                    $"CRITICAL: post-restore EC verification failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine(
                "No fan write was attempted; FF/FF verification intentionally skipped to preserve any external owner.");
        }

        if (restoreFailure is not null)
        {
            return 47;
        }

        if (testFailure is OperationCanceledException)
        {
            Console.Error.WriteLine(
                "Integrated test cancelled; coordinator handoff to HP firmware was requested.");
            return 130;
        }

        if (testFailure is not null)
        {
            Console.Error.WriteLine(
                $"Integrated coordinator test failed safely: {testFailure.Message}");
            return 46;
        }

        if (coordinator.Authority != FanAuthority.Firmware)
        {
            Console.Error.WriteLine(
                $"Integrated test ended with unexpected authority {coordinator.Authority}.");
            return 48;
        }

        Console.WriteLine();
        Console.WriteLine(
            "PASS: SafetyGate -> FanControlCoordinator -> real HP backend -> EC/tach ACK -> HP restore.");
        return 0;
    }

    private static SafetyGateResult BuildSafety(
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot,
        bool fanWritePathPresent) =>
        SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            DateTimeOffset.UtcNow,
            fanWritePathPresent);

    private static void EnsureLightLoadEnvelope(TelemetrySnapshot snapshot)
    {
        if (snapshot.CpuTemperatureC > MaximumBaselineCpuTemperatureC ||
            snapshot.GpuTemperatureC > MaximumBaselineGpuTemperatureC ||
            snapshot.CpuPackagePowerW > MaximumBaselineCpuPowerW ||
            snapshot.GpuPowerW > MaximumBaselineGpuPowerW)
        {
            throw new InvalidOperationException(
                "Light-load validation envelope exceeded. " +
                $"Limits: CPU <= {MaximumBaselineCpuTemperatureC:0} C / " +
                $"{MaximumBaselineCpuPowerW:0} W, GPU <= " +
                $"{MaximumBaselineGpuTemperatureC:0} C / " +
                $"{MaximumBaselineGpuPowerW:0} W.");
        }
    }

    private static void EnsureCustomWindow(long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed.TotalSeconds > MaximumCustomWindowSeconds)
        {
            throw new TimeoutException(
                $"Integrated custom-authority window exceeded {MaximumCustomWindowSeconds:0} seconds.");
        }
    }

    private static void EnsureNoSchedulingGap(
        ref long? previousTick,
        string phase)
    {
        var now = Stopwatch.GetTimestamp();

        if (previousTick.HasValue)
        {
            var gap = Stopwatch.GetElapsedTime(previousTick.Value, now);
            if (gap.TotalSeconds > MaximumSamplingGapSeconds)
            {
                previousTick = now;
                throw new TimeoutException(
                    $"Unexpected {gap.TotalSeconds:0.0} s scheduling gap during {phase}; " +
                    "possible suspend/resume or blocked execution.");
            }
        }

        previousTick = now;
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

    private static async Task<Hp88F8EcControlState> WaitForFanOverrideReleaseAsync(
        string modulesDirectory,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        Hp88F8EcControlState? last = null;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            last = new Hp88F8EcControlStateProbe(modulesDirectory).Read();

            if (last.CpuSetpoint == byte.MaxValue &&
                last.GpuSetpoint == byte.MaxValue)
            {
                return last;
            }

            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Fan-level release was not acknowledged by EC within {timeout.TotalSeconds:0} s. " +
            $"Last setpoints: CPU={last?.CpuSetpoint.ToString() ?? "n/a"} " +
            $"GPU={last?.GpuSetpoint.ToString() ?? "n/a"}.");
    }
}
