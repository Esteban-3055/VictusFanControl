using System.Diagnostics;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Qualification-only transition gate for the physically characterized HP 8C40
/// equal-level range.
///
/// Sequence:
/// firmware -> 10 -> 30 -> 50 -> 30 -> 10 -> firmware.
///
/// Unlike the broad characterization sweep, the fixed override is intentionally
/// retained between transition steps so large up/down transitions can be
/// observed without an intervening firmware restore.
///
/// This test does not change the production backend envelope.
/// </summary>
public static class Hp8C40TransitionQualificationTest
{
    private static readonly TransitionStep[] Sequence =
    [
        new("firmware -> 10", 10),
        new("10 -> 30", 30),
        new("30 -> 50", 50),
        new("50 -> 30", 30),
        new("30 -> 10", 10)
    ];

    private const int RequiredConsecutiveInBandSamples = 2;
    private const int MinimumSafeCustomRpm = 750;
    private const byte MinimumBatteryPercentForQualification = 20;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SetpointAckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestoreAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BaselineReadyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BaselineRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EcRetryDelay = TimeSpan.FromMilliseconds(150);

    private const int EcReadAttempts = 3;

    private const double MaximumCpuTemperatureC = 80;
    private const double MaximumGpuTemperatureC = 75;
    private const double MaximumCpuPowerW = 50;
    private const double MaximumGpuPowerW = 70;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("HP 8C40 LARGE-TRANSITION QUALIFICATION");
        Console.WriteLine("ACTIVE qualification-only hardware gate; automatic policy remains OFF.");
        Console.WriteLine("Sequence: firmware -> 10 -> 30 -> 50 -> 30 -> 10 -> firmware.");
        Console.WriteLine("No firmware restore occurs between transition steps.");
        Console.WriteLine("A verified FF/FF + LegacyDefault restore is mandatory at the end/abort.");
        Console.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var identityReason))
        {
            Console.Error.WriteLine($"Transition qualification refused: {identityReason}");
            return 121;
        }

        var conflict = FindKnownConflictingControllerProcess();
        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"Transition qualification refused while '{conflict}' is running. " +
                "Close OmenMon/OmenMon-Reborn/VictusFanControl GUI first.");
            return 122;
        }

        SystemSleepInhibitor sleepInhibitor;
        try
        {
            sleepInhibitor = SystemSleepInhibitor.Acquire(
                "HP 8C40 large fan transition qualification");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Transition qualification refused: Windows sleep inhibition could not be acquired: {ex.Message}");
            return 123;
        }

        using (sleepInhibitor)
        using (var reader = new HardwareTelemetryReader(modulesDirectory))
        {
            Console.WriteLine("Automatic idle sleep/Modern Standby inhibition: ACTIVE.");
            Console.WriteLine("Explicit lid/user/critical power transitions are still not blocked.");
            Console.WriteLine();

            if (!reader.BackendsInitialized)
            {
                Console.Error.WriteLine(
                    "Transition qualification refused: telemetry backends are not initialized.");
                foreach (var line in reader.GetBackendDiagnostics())
                {
                    Console.Error.WriteLine(line);
                }

                return 124;
            }

            try
            {
                EnsureQualificationPowerStatus(SystemPowerStatusReader.Read());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Transition qualification refused by AC/battery sanity gate: {ex.Message}");
                return 125;
            }

            Console.WriteLine(
                $"AC/battery sanity baseline: {SystemPowerStatusReader.Read()}");
            Console.WriteLine();

            Console.WriteLine("Priming differential telemetry counters...");
            _ = reader.ReadSnapshot();
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            TelemetrySnapshot baseline;
            try
            {
                baseline = await WaitForSafeBaselineAsync(
                    hardware,
                    reader,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Transition qualification refused before any fan write: {ex.Message}");
                return 126;
            }

            Console.WriteLine("Firmware baseline:");
            ConsoleTelemetryPrinter.Print(baseline);

            var probe = new Hp8C40EcControlStateProbe(modulesDirectory);
            Hp8C40EcControlState baselineEc;
            try
            {
                baselineEc = await ReadControlEvidenceWithRetryAsync(
                    probe,
                    "firmware baseline",
                    cancellationToken).ConfigureAwait(false);
                EnsureFirmwareReleasedAndGuardsSane(baselineEc);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Transition qualification refused at EC baseline: {ex.Message}");
                return 127;
            }

            Console.WriteLine($"EC baseline: {FormatControlEvidence(baselineEc)}");
            Console.WriteLine();

            var control = new Hp8C40ExtendedFanLevelQualificationControl();
            var results = new List<TransitionResult>(Sequence.Length);
            var writeMayHaveOccurred = false;
            Exception? transitionFailure = null;
            Exception? restoreFailure = null;
            byte? ownedLevel = null;

            try
            {
                foreach (var step in Sequence)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureQualificationPowerStatus(SystemPowerStatusReader.Read());

                    var pre = reader.ReadSnapshot();
                    EnsureSafeTelemetry(hardware, pre);

                    var preEc = await ReadControlEvidenceWithRetryAsync(
                        probe,
                        $"{step.Name} pre-command",
                        cancellationToken).ConfigureAwait(false);

                    if (ownedLevel.HasValue)
                    {
                        EnsureActiveOwnershipAndGuards(preEc, ownedLevel.Value);
                    }
                    else
                    {
                        EnsureFirmwareReleasedAndGuardsSane(preEc);
                    }

                    var preCpuRpm = pre.CpuFanRpm!.Value;
                    var preGpuRpm = pre.GpuFanRpm!.Value;

                    Console.WriteLine($"=== Transition {step.Name} ===");
                    Console.WriteLine(
                        $"Pre-command RPM: CPU {preCpuRpm:0} | GPU {preGpuRpm:0}");
                    Console.WriteLine(
                        $"Sending qualification WMI SetFanLevel({step.TargetLevel},{step.TargetLevel})...");

                    writeMayHaveOccurred = true;
                    control.SetEqualLevel(step.TargetLevel);

                    var acknowledged = await WaitForSetpointAsync(
                        probe,
                        step.TargetLevel,
                        SetpointAckTimeout,
                        cancellationToken).ConfigureAwait(false);

                    Console.WriteLine(
                        $"EC setpoint ACK: {acknowledged.Cpu}/{acknowledged.Gpu}");

                    ownedLevel = step.TargetLevel;

                    var result = await ObserveTransitionAsync(
                        hardware,
                        reader,
                        probe,
                        step,
                        preCpuRpm,
                        preGpuRpm,
                        cancellationToken).ConfigureAwait(false);

                    results.Add(result);

                    Console.WriteLine(
                        $"Transition {step.Name}: PASS | " +
                        $"converged in {result.ConvergenceSeconds:0.0} s | " +
                        $"terminal CPU {result.TerminalCpuRpm:0} RPM | " +
                        $"GPU {result.TerminalGpuRpm:0} RPM");
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                transitionFailure = ex;
            }
            finally
            {
                if (writeMayHaveOccurred)
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
                            $"CRITICAL: transition-gate restore failed: {ex.Message}");
                    }
                }
            }

            if (restoreFailure is not null)
            {
                return 128;
            }

            if (transitionFailure is OperationCanceledException)
            {
                Console.Error.WriteLine(
                    "Transition qualification cancelled; firmware restore was requested.");
                return 130;
            }

            if (transitionFailure is not null)
            {
                Console.Error.WriteLine(
                    $"Transition qualification failed safely: {transitionFailure.Message}");
                return 129;
            }

            Console.WriteLine();
            Console.WriteLine("Transition summary:");
            foreach (var result in results)
            {
                Console.WriteLine(
                    $"  {result.Name,-16} " +
                    $"{result.ConvergenceSeconds,4:0.0} s | " +
                    $"CPU {result.StartCpuRpm,4:0}->{result.TerminalCpuRpm,4:0} RPM | " +
                    $"GPU {result.StartGpuRpm,4:0}->{result.TerminalGpuRpm,4:0} RPM");
            }

            Console.WriteLine();
            Console.WriteLine(
                "RESULT: PASS (firmware -> 10 -> 30 -> 50 -> 30 -> 10 -> firmware " +
                "completed with bounded telemetry, ownership, dual-fan convergence and verified restore).");
            Console.WriteLine(
                "NOTE: this qualifies transition behavior only; it does not expand the production backend.");

            return 0;
        }
    }

    private static async Task<TransitionResult> ObserveTransitionAsync(
        HardwareIdentity hardware,
        HardwareTelemetryReader reader,
        Hp8C40EcControlStateProbe probe,
        TransitionStep step,
        double startCpuRpm,
        double startGpuRpm,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var expectedRpm = step.TargetLevel * 100.0;
        var toleranceRpm = Math.Max(150.0, expectedRpm * 0.05);
        var consecutiveInBand = 0;
        double terminalCpuRpm = double.NaN;
        double terminalGpuRpm = double.NaN;

        while (Stopwatch.GetElapsedTime(started) < TransitionTimeout)
        {
            await Task.Delay(
                SampleInterval,
                cancellationToken).ConfigureAwait(false);

            EnsureQualificationPowerStatus(SystemPowerStatusReader.Read());

            var sample = reader.ReadSnapshot();
            EnsureSafeTelemetry(hardware, sample);

            var cpuRpm = sample.CpuFanRpm!.Value;
            var gpuRpm = sample.GpuFanRpm!.Value;
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (cpuRpm < MinimumSafeCustomRpm ||
                gpuRpm < MinimumSafeCustomRpm)
            {
                throw new InvalidOperationException(
                    $"fan feedback crossed the conservative running floor during {step.Name}: " +
                    $"CPU={cpuRpm:0} RPM GPU={gpuRpm:0} RPM.");
            }

            var cpuInBand = Math.Abs(cpuRpm - expectedRpm) <= toleranceRpm;
            var gpuInBand = Math.Abs(gpuRpm - expectedRpm) <= toleranceRpm;

            Console.WriteLine(
                $"  t={elapsed.TotalSeconds,4:0.0}s | " +
                $"CPU {cpuRpm,4:0} RPM | GPU {gpuRpm,4:0} RPM | " +
                $"target≈{expectedRpm:0}±{toleranceRpm:0} | " +
                $"CPU {sample.CpuControlTemperatureC:0.0} C | GPU {sample.GpuTemperatureC:0.0} C");

            if (cpuInBand && gpuInBand)
            {
                consecutiveInBand++;
                terminalCpuRpm = cpuRpm;
                terminalGpuRpm = gpuRpm;

                if (consecutiveInBand >= RequiredConsecutiveInBandSamples)
                {
                    var finalEc = await ReadControlEvidenceWithRetryAsync(
                        probe,
                        $"{step.Name} converged ownership",
                        cancellationToken).ConfigureAwait(false);
                    EnsureActiveOwnershipAndGuards(finalEc, step.TargetLevel);

                    return new TransitionResult(
                        step.Name,
                        step.TargetLevel,
                        startCpuRpm,
                        startGpuRpm,
                        terminalCpuRpm,
                        terminalGpuRpm,
                        elapsed.TotalSeconds);
                }
            }
            else
            {
                consecutiveInBand = 0;
            }
        }

        throw new TimeoutException(
            $"Both fans did not converge to the level-{step.TargetLevel} terminal band " +
            $"within {TransitionTimeout.TotalSeconds:0} s during {step.Name}.");
    }

    private static async Task<TelemetrySnapshot> WaitForSafeBaselineAsync(
        HardwareIdentity hardware,
        HardwareTelemetryReader reader,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        string lastReason = "no telemetry sample was evaluated";

        while (Stopwatch.GetElapsedTime(started) < BaselineReadyTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureQualificationPowerStatus(SystemPowerStatusReader.Read());

            var sample = reader.ReadSnapshot();
            try
            {
                EnsureSafeTelemetry(hardware, sample);
                return sample;
            }
            catch (PowerTransitionDetectedException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                lastReason = ex.Message;
                Console.WriteLine(
                    $"Telemetry baseline not ready yet: {lastReason}");
            }

            await Task.Delay(
                BaselineRetryInterval,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"telemetry did not become SafetyGate-ready within " +
            $"{BaselineReadyTimeout.TotalSeconds:0} s. Last reason: {lastReason}");
    }

    private static void EnsureQualificationPowerStatus(
        SystemPowerStatusSample status)
    {
        if (!status.AcOnline)
        {
            throw new PowerTransitionDetectedException(
                $"AC power unexpectedly went offline ({status}).");
        }

        if (!status.BatteryPresent)
        {
            throw new PowerTransitionDetectedException(
                $"Windows reported no usable battery ({status}).");
        }

        if (status.BatteryPercent > 100)
        {
            throw new PowerTransitionDetectedException(
                $"Windows reported an unknown battery percentage ({status}).");
        }

        if (status.BatteryPercent < MinimumBatteryPercentForQualification)
        {
            throw new PowerTransitionDetectedException(
                $"Windows reported battery={status.BatteryPercent}% while AC is online; " +
                $"qualification requires at least {MinimumBatteryPercentForQualification}%.");
        }
    }

    private static void EnsureSafeTelemetry(
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot)
    {
        var telemetryAge = DateTimeOffset.UtcNow - snapshot.Timestamp;
        if (telemetryAge < TimeSpan.Zero)
        {
            telemetryAge = TimeSpan.Zero;
        }

        if (telemetryAge > MaximumTelemetryAge)
        {
            throw new PowerTransitionDetectedException(
                $"telemetry age jumped to {telemetryAge.TotalSeconds:0.0} s.");
        }

        var safety = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            DateTimeOffset.UtcNow,
            fanWritePathPresent: true);

        if (!safety.CustomControlPermitted)
        {
            throw new InvalidOperationException(
                "SafetyGate refused transition telemetry: " +
                string.Join(" | ", safety.Reasons));
        }

        if (snapshot.CpuControlTemperatureC > MaximumCpuTemperatureC ||
            snapshot.GpuTemperatureC > MaximumGpuTemperatureC ||
            snapshot.CpuPackagePowerW > MaximumCpuPowerW ||
            snapshot.GpuPowerW > MaximumGpuPowerW)
        {
            throw new InvalidOperationException(
                "Light-load transition envelope exceeded: " +
                $"effective CPU={snapshot.CpuControlTemperatureC:0.0} C, " +
                $"CPU package={snapshot.CpuPackagePowerW:0.0} W, " +
                $"GPU={snapshot.GpuTemperatureC:0.0} C, " +
                $"GPU power={snapshot.GpuPowerW:0.0} W.");
        }
    }

    private static async Task<(byte Cpu, byte Gpu)> WaitForSetpointAsync(
        Hp8C40EcControlStateProbe probe,
        byte expectedLevel,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        (byte Cpu, byte Gpu)? last = null;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var sample = probe.ReadSetpoint();
                last = (sample.CpuSetpoint, sample.GpuSetpoint);

                if (last.Value.Cpu == expectedLevel &&
                    last.Value.Gpu == expectedLevel)
                {
                    return last.Value;
                }
            }
            catch (Exception ex)
                when (ex is IOException or TimeoutException or InvalidDataException)
            {
                // Bounded retry below.
            }

            await Task.Delay(
                100,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"EC did not acknowledge {expectedLevel}/{expectedLevel} within " +
            $"{timeout.TotalSeconds:0.0} s. Last setpoint=" +
            $"{last?.Cpu.ToString() ?? "n/a"}/{last?.Gpu.ToString() ?? "n/a"}.");
    }

    private static async Task<Hp8C40EcControlState> ReadControlEvidenceWithRetryAsync(
        Hp8C40EcControlStateProbe probe,
        string context,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= EcReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var state = probe.ReadControlEvidence();

                if (state.CpuSetpoint != state.GpuSetpoint)
                {
                    throw new InvalidDataException(
                        $"asymmetric setpoint sample CPU={state.CpuSetpoint} GPU={state.GpuSetpoint}");
                }

                if (state.MaxFan != 0x00 || state.FanSwitch != 0x00)
                {
                    throw new InvalidDataException(
                        $"guard sample MaxFan=0x{state.MaxFan:X2} FanSwitch=0x{state.FanSwitch:X2}");
                }

                return state;
            }
            catch (Exception ex)
                when (ex is IOException or TimeoutException or InvalidDataException)
            {
                lastError = ex;
                Console.WriteLine(
                    $"EC transient during {context}: retry {attempt}/{EcReadAttempts}: {ex.Message}");

                if (attempt < EcReadAttempts)
                {
                    await Task.Delay(
                        EcRetryDelay,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            $"EC control evidence remained unavailable during {context} after " +
            $"{EcReadAttempts} attempts.",
            lastError);
    }

    private static async Task<Hp8C40EcControlState> WaitForRestoreAsync(
        Hp8C40EcControlStateProbe probe,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            try
            {
                var state = await ReadControlEvidenceWithRetryAsync(
                    probe,
                    "transition restore verification",
                    CancellationToken.None).ConfigureAwait(false);

                if (state.CpuSetpoint == byte.MaxValue &&
                    state.GpuSetpoint == byte.MaxValue)
                {
                    EnsureGuardsSane(state);
                    return state;
                }
            }
            catch (IOException)
            {
                // Bounded retry below.
            }

            await Task.Delay(
                250,
                CancellationToken.None).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Firmware restore did not reach FF/FF within {timeout.TotalSeconds:0.0} s.");
    }

    private static void EnsureFirmwareReleasedAndGuardsSane(
        Hp8C40EcControlState state)
    {
        if (state.CpuSetpoint != byte.MaxValue ||
            state.GpuSetpoint != byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Transition qualification requires firmware-released FF/FF baseline; " +
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
                $"Transition ownership changed unexpectedly: expected " +
                $"{expectedLevel}/{expectedLevel}, observed " +
                $"{state.CpuSetpoint}/{state.GpuSetpoint}.");
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

    private static string FormatControlEvidence(Hp8C40EcControlState state) =>
        $"level CPU={state.CpuSetpoint} GPU={state.GpuSetpoint} | " +
        $"max=0x{state.MaxFan:X2} switch=0x{state.FanSwitch:X2} | " +
        $"RPM CPU={state.CpuRpm} GPU={state.GpuRpm}";

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

    private sealed record TransitionStep(
        string Name,
        byte TargetLevel);

    private sealed record TransitionResult(
        string Name,
        byte TargetLevel,
        double StartCpuRpm,
        double StartGpuRpm,
        double TerminalCpuRpm,
        double TerminalGpuRpm,
        double ConvergenceSeconds);

    private sealed class PowerTransitionDetectedException : Exception
    {
        public PowerTransitionDetectedException(string message)
            : base(message)
        {
        }
    }
}
