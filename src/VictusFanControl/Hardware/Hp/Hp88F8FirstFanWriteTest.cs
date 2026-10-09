using System.Diagnostics;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Deliberately narrow first-write validation for the known HP 88F8.
/// It is not a general fan controller: level and duration are fixed.
/// Firmware authority is restored in a finally block after ANY attempted write.
/// </summary>
public static class Hp88F8FirstFanWriteTest
{
    public const byte TestLevel = 30;
    public const int TestDurationSeconds = 15;

    private const int AcknowledgementDeadlineSeconds = 8;
    private const int RequiredConsecutiveRpmSamples = 2;
    private const double MinimumAcknowledgedRpm = 2500;
    private const double MaximumAcknowledgedRpm = 4000;
    private const double MaximumSamplingGapSeconds = 3.0;
    private const int OwnershipCheckIntervalSeconds = 3;

    // First hardware-write validation should be done under light load.
    private const double MaximumBaselineCpuTemperatureC = 80;
    private const double MaximumBaselineGpuTemperatureC = 75;
    private const double MaximumBaselineCpuPowerW = 50;
    private const double MaximumBaselineGpuPowerW = 70;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp88F8TargetProfile.Matches(hardware, out var identityReason))
        {
            Console.Error.WriteLine(
                $"First fan-write test refused: {identityReason}");
            return 20;
        }

        var conflictingProcess = FindKnownConflictingControllerProcess();
        if (conflictingProcess is not null)
        {
            Console.Error.WriteLine(
                $"First fan-write test refused while '{conflictingProcess}' is running. " +
                "Close OmenMon/VictusFanControl GUI first; OMEN Gaming Hub may remain open for undervolt.");
            return 31;
        }

        using var reader = new HardwareTelemetryReader(modulesDirectory);
        if (!reader.BackendsInitialized)
        {
            Console.Error.WriteLine("Telemetry backends are not fully initialized; fan-write test refused.");
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.Error.WriteLine(line);
            }

            return 21;
        }

        Console.WriteLine("Priming telemetry...");
        _ = reader.ReadSnapshot();
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var baseline = reader.ReadSnapshot();
        Console.WriteLine("Baseline:");
        ConsoleTelemetryPrinter.Print(baseline);

        var initialSafety = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            baseline,
            DateTimeOffset.UtcNow);

        if (!initialSafety.PreconditionsReady)
        {
            Console.Error.WriteLine("Safety preconditions are not ready; fan-write test refused.");
            foreach (var reason in initialSafety.Reasons)
            {
                Console.Error.WriteLine($"  - {reason}");
            }

            return 22;
        }

        if (baseline.CpuTemperatureC > MaximumBaselineCpuTemperatureC ||
            baseline.GpuTemperatureC > MaximumBaselineGpuTemperatureC ||
            baseline.CpuPackagePowerW > MaximumBaselineCpuPowerW ||
            baseline.GpuPowerW > MaximumBaselineGpuPowerW)
        {
            Console.Error.WriteLine(
                "First fan-write test requires a light-load baseline. " +
                $"Limits: CPU <= {MaximumBaselineCpuTemperatureC:0} C / {MaximumBaselineCpuPowerW:0} W, " +
                $"GPU <= {MaximumBaselineGpuTemperatureC:0} C / {MaximumBaselineGpuPowerW:0} W.");
            return 29;
        }

        Hp88F8EcControlState ecBefore;
        try
        {
            ecBefore = new Hp88F8EcControlStateProbe(modulesDirectory).Read();
            Console.WriteLine($"EC before : {ecBefore}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not capture EC state before test: {ex.Message}");
            return 23;
        }

        Hp88F8BiosFanControl bios;
        try
        {
            bios = new Hp88F8BiosFanControl();
            var biosBefore = bios.GetCurrentFanLevels();
            Console.WriteLine(
                $"BIOS current level before: CPU={biosBefore.CpuLevel} GPU={biosBefore.GpuLevel}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not initialize/read the HP BIOS fan interface before test: {ex.Message}");
            return 30;
        }

        var writeAttempted = false;
        var acknowledged = false;
        var consecutiveRpmSamples = 0;
        long? controlStarted = null;
        long? previousProgressTick = null;
        var nextOwnershipCheckSecond = OwnershipCheckIntervalSeconds;
        Exception? testFailure = null;
        Exception? restoreFailure = null;

        try
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Applying fixed HP BIOS fan level {TestLevel},{TestLevel} for at most {TestDurationSeconds} seconds...");

            // Set this BEFORE the WMI call. OmenMon documents that on some HP
            // models a fan-level setting may take effect even if the BIOS call
            // reports an error. Therefore every attempted write must be followed
            // by a firmware-restore attempt.
            writeAttempted = true;
            controlStarted = Stopwatch.GetTimestamp();
            previousProgressTick = controlStarted;
            bios.SetFanLevel(TestLevel, TestLevel);

            EnsureControlWindow(controlStarted.Value);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            EnsureNoSchedulingGap(ref previousProgressTick, "post-write verification");
            EnsureControlWindow(controlStarted.Value);

            var biosApplied = bios.GetCurrentFanLevels();
            Console.WriteLine(
                $"BIOS current level after write: CPU={biosApplied.CpuLevel} GPU={biosApplied.GpuLevel} " +
                "(informational; this is current speed level, not commanded target)");

            var ecApplied = new Hp88F8EcControlStateProbe(modulesDirectory).Read();
            Console.WriteLine($"EC applied: {ecApplied}");

            if (ecApplied.CpuSetpoint != TestLevel ||
                ecApplied.GpuSetpoint != TestLevel)
            {
                throw new InvalidOperationException(
                    $"EC fan-level acknowledgement mismatch: requested {TestLevel},{TestLevel}, " +
                    $"read {ecApplied.CpuSetpoint},{ecApplied.GpuSetpoint}.");
            }

            EnsureControlWindow(controlStarted.Value);

            for (var second = 1; second <= TestDurationSeconds; second++)
            {
                if (Stopwatch.GetElapsedTime(controlStarted.Value).TotalSeconds >= TestDurationSeconds)
                {
                    break;
                }

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                EnsureNoSchedulingGap(ref previousProgressTick, "fan-control sampling loop");

                var elapsed = Stopwatch.GetElapsedTime(controlStarted.Value);
                if (elapsed.TotalSeconds >= TestDurationSeconds)
                {
                    break;
                }

                var sample = reader.ReadSnapshot();
                ConsoleTelemetryPrinter.Print(sample);

                var safety = SafetyGate.Evaluate(
                    hardware,
                    SystemState.Healthy,
                    sample,
                    DateTimeOffset.UtcNow);

                if (!safety.PreconditionsReady)
                {
                    throw new InvalidOperationException(
                        "Safety gate dropped during fan-write test: " +
                        string.Join(" | ", safety.Reasons));
                }

                if (sample.CpuTemperatureC > MaximumBaselineCpuTemperatureC ||
                    sample.GpuTemperatureC > MaximumBaselineGpuTemperatureC ||
                    sample.CpuPackagePowerW > MaximumBaselineCpuPowerW ||
                    sample.GpuPowerW > MaximumBaselineGpuPowerW)
                {
                    throw new InvalidOperationException(
                        "Light-load validation envelope was exceeded during the fan-write test.");
                }

                var rpmInWindow =
                    sample.CpuFanRpm >= MinimumAcknowledgedRpm &&
                    sample.CpuFanRpm <= MaximumAcknowledgedRpm &&
                    sample.GpuFanRpm >= MinimumAcknowledgedRpm &&
                    sample.GpuFanRpm <= MaximumAcknowledgedRpm;

                consecutiveRpmSamples = rpmInWindow
                    ? consecutiveRpmSamples + 1
                    : 0;

                if (consecutiveRpmSamples >= RequiredConsecutiveRpmSamples)
                {
                    acknowledged = true;
                }

                if (elapsed.TotalSeconds >= AcknowledgementDeadlineSeconds && !acknowledged)
                {
                    throw new InvalidOperationException(
                        $"Stable fan RPM acknowledgement not observed by {AcknowledgementDeadlineSeconds} s " +
                        $"(required {RequiredConsecutiveRpmSamples} consecutive samples with both fans " +
                        $"{MinimumAcknowledgedRpm:0}-{MaximumAcknowledgedRpm:0} RPM).");
                }

                if (elapsed.TotalSeconds >= nextOwnershipCheckSecond)
                {
                    var ownership = new Hp88F8EcControlStateProbe(modulesDirectory).Read();
                    Console.WriteLine(
                        $"EC ownership check: setpoint CPU={ownership.CpuSetpoint} GPU={ownership.GpuSetpoint} " +
                        $"| current-rate CPU={ownership.CpuRate}% GPU={ownership.GpuRate}%");

                    if (ownership.CpuSetpoint != TestLevel ||
                        ownership.GpuSetpoint != TestLevel)
                    {
                        throw new InvalidOperationException(
                            $"Fan-level ownership changed during the test: expected EC setpoint " +
                            $"{TestLevel},{TestLevel}, read {ownership.CpuSetpoint},{ownership.GpuSetpoint}.");
                    }

                    nextOwnershipCheckSecond += OwnershipCheckIntervalSeconds;
                    EnsureControlWindow(controlStarted.Value);
                }
            }
        }
        catch (Exception ex)
        {
            testFailure = ex;
        }
        finally
        {
            if (writeAttempted)
            {
                Console.WriteLine();
                Console.WriteLine("Releasing fixed fan level with FF,FF and restoring HP FanMode=LegacyDefault...");
                try
                {
                    bios.RestoreFirmwareAuto();
                    Console.WriteLine("HP BIOS reported successful fan-level release + LegacyDefault restore.");
                }
                catch (Exception ex)
                {
                    restoreFailure = ex;
                    Console.Error.WriteLine($"CRITICAL: firmware restore failed: {ex.Message}");
                }
            }
        }

        if (writeAttempted)
        {
            try
            {
                var ecAfter = await WaitForFanOverrideReleaseAsync(
                    modulesDirectory,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                var biosAfter = bios.GetCurrentFanLevels();
                Console.WriteLine(
                    $"BIOS current level after restore: CPU={biosAfter.CpuLevel} GPU={biosAfter.GpuLevel}");
                Console.WriteLine($"EC after  : {ecAfter}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not capture post-restore state: {ex.Message}");
            }
        }

        if (restoreFailure is not null)
        {
            return 25;
        }

        if (testFailure is OperationCanceledException)
        {
            Console.Error.WriteLine("Test cancelled; LegacyDefault restore was requested.");
            return 130;
        }

        if (testFailure is not null)
        {
            Console.Error.WriteLine($"Fan-write test failed safely: {testFailure.Message}");
            return 24;
        }

        if (!acknowledged)
        {
            Console.Error.WriteLine("Test completed without stable RPM acknowledgement.");
            return 26;
        }

        Console.WriteLine();
        Console.WriteLine("First fan-write test completed and HP firmware authority was restored.");
        return 0;
    }

    private static void EnsureControlWindow(long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed.TotalSeconds > TestDurationSeconds)
        {
            throw new TimeoutException(
                $"Custom fan-control window exceeded {TestDurationSeconds} seconds.");
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
        foreach (var processName in new[] { "OmenMon", "OmenMon-Reborn", "VictusFanControl.App" })
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
