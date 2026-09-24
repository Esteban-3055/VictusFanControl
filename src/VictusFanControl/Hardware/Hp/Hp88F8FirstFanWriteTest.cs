using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Deliberately narrow first-write validation for the known HP 88F8.
/// It is not a general fan controller: level and duration are fixed.
/// Firmware authority is restored in a finally block.
/// </summary>
public static class Hp88F8FirstFanWriteTest
{
    public const byte TestLevel = 30;
    public const int TestDurationSeconds = 15;
    private const int AcknowledgementDeadlineSeconds = 8;
    private const double MinimumAcknowledgedRpm = 2500;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!string.Equals(
                hardware.BoardProduct,
                SafetyGate.InitialValidatedBoardProduct,
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"First fan-write test refused on board '{hardware.BoardProduct}'.");
            return 20;
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

        Hp88F8EcControlState? ecBefore = null;
        try
        {
            ecBefore = new Hp88F8EcControlStateProbe(modulesDirectory).Read();
            Console.WriteLine($"EC before: {ecBefore}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not capture EC state before test: {ex.Message}");
            return 23;
        }

        var bios = new Hp88F8BiosFanControl();
        var commandSent = false;
        var acknowledged = false;
        Exception? testFailure = null;
        Exception? restoreFailure = null;

        try
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Applying fixed HP BIOS fan level {TestLevel},{TestLevel} for at most {TestDurationSeconds} seconds...");
            bios.SetFanLevel(TestLevel, TestLevel);
            commandSent = true;

            for (var second = 1; second <= TestDurationSeconds; second++)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

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

                if (sample.CpuFanRpm >= MinimumAcknowledgedRpm &&
                    sample.GpuFanRpm >= MinimumAcknowledgedRpm)
                {
                    acknowledged = true;
                }

                if (second >= AcknowledgementDeadlineSeconds && !acknowledged)
                {
                    throw new InvalidOperationException(
                        $"Fan RPM acknowledgement not observed by {AcknowledgementDeadlineSeconds} s " +
                        $"(required both >= {MinimumAcknowledgedRpm:0} RPM).");
                }
            }
        }
        catch (Exception ex)
        {
            testFailure = ex;
        }
        finally
        {
            if (commandSent)
            {
                Console.WriteLine();
                Console.WriteLine("Restoring HP FanMode=LegacyDefault...");
                try
                {
                    bios.RestoreLegacyDefault();
                    Console.WriteLine("HP BIOS reported successful LegacyDefault restore.");
                }
                catch (Exception ex)
                {
                    restoreFailure = ex;
                    Console.Error.WriteLine($"CRITICAL: firmware restore failed: {ex.Message}");
                }
            }
        }

        if (commandSent)
        {
            try
            {
                await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
                var ecAfter = new Hp88F8EcControlStateProbe(modulesDirectory).Read();
                Console.WriteLine($"EC after : {ecAfter}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not capture EC state after restore: {ex.Message}");
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
            Console.Error.WriteLine("Test completed without RPM acknowledgement.");
            return 26;
        }

        Console.WriteLine();
        Console.WriteLine("First fan-write test completed and HP firmware authority was restored.");
        return 0;
    }
}
