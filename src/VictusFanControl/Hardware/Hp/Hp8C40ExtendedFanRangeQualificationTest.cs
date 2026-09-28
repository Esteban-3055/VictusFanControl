using System.Diagnostics;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Guarded physical characterization of equal HP 8C40 fan levels 10..50.
///
/// Execution intentionally starts from the known-good level 30, walks downward
/// one level at a time, then walks upward from 31. This is safer than starting
/// blindly at 10 because the low-range fan floor is not yet known.
///
/// Every attempted level is isolated by FF/FF + LegacyDefault restore.
/// The lower sweep stops at the first physical running-floor boundary instead
/// of continuing blindly toward smaller values. The upper sweep stops at the
/// first hard command/ack/feedback failure.
///
/// This test never changes the production range automatically.
/// </summary>
public static class Hp8C40ExtendedFanRangeQualificationTest
{
    private static readonly byte[] AnchorLevels = [30];
    private static readonly byte[] LowerLevels =
        Enumerable.Range(10, 20).Reverse().Select(value => (byte)value).ToArray();
    private static readonly byte[] UpperLevels =
        Enumerable.Range(31, 20).Select(value => (byte)value).ToArray();

    private const int SamplesPerLevel = 5;
    private static readonly TimeSpan SettleDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EcRetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SetpointAckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestoreAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumStepWindow = TimeSpan.FromSeconds(15);

    private const int EcEvidenceReadAttempts = 3;

    // Conservative characterization boundary. Falling below this while we own
    // a fixed low-level command stops the downward sweep immediately.
    private const double MinimumSafeCustomRpm = 750;

    private const double MaximumCpuTemperatureC = 80;
    private const double MaximumGpuTemperatureC = 75;
    private const double MaximumCpuPowerW = 50;
    private const double MaximumGpuPowerW = 70;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("HP 8C40 EXTENDED FAN-RANGE QUALIFICATION 10..50");
        Console.WriteLine("ACTIVE hardware characterization; automatic fan policy remains OFF.");
        Console.WriteLine();
        Console.WriteLine("Execution order:");
        Console.WriteLine("  anchor 30/30");
        Console.WriteLine("  lower sweep 29/29 -> 10/10");
        Console.WriteLine("  upper sweep 31/31 -> 50/50");
        Console.WriteLine();
        Console.WriteLine(
            $"Downward sweep stops if either fan falls below {MinimumSafeCustomRpm:0} RPM.");
        Console.WriteLine(
            "Every attempted level is followed by FF/FF + LegacyDefault restore and verification.");
        Console.WriteLine(
            "This characterization does NOT automatically expand the production 30..36 envelope.");
        Console.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var identityReason))
        {
            Console.Error.WriteLine($"Qualification refused: {identityReason}");
            return 110;
        }

        var conflict = FindKnownConflictingControllerProcess();
        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"Qualification refused while '{conflict}' is running. " +
                "Close OmenMon/OmenMon-Reborn/VictusFanControl GUI first.");
            return 111;
        }

        SystemSleepInhibitor sleepInhibitor;
        try
        {
            sleepInhibitor = SystemSleepInhibitor.Acquire(
                "HP 8C40 extended fan-range qualification 10..50");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Qualification refused: Windows sleep inhibition could not be acquired: {ex.Message}");
            return 117;
        }

        using (sleepInhibitor)
        {
            Console.WriteLine(
                "Automatic idle sleep/Modern Standby inhibition: ACTIVE for this test.");
            Console.WriteLine(
                "Explicit lid/user/critical power transitions are still not blocked.");
            Console.WriteLine();

        using var reader = new HardwareTelemetryReader(modulesDirectory);
        if (!reader.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "Qualification refused: telemetry backends are not initialized.");
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.Error.WriteLine(line);
            }

            return 112;
        }

        var control = new Hp8C40ExtendedFanLevelQualificationControl();
        var probe = new Hp8C40EcControlStateProbe(modulesDirectory);

        Console.WriteLine("Priming differential telemetry counters...");
        _ = reader.ReadSnapshot();
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var baseline = reader.ReadSnapshot();
        Console.WriteLine("Firmware baseline:");
        ConsoleTelemetryPrinter.Print(baseline);
        try
        {
            EnsureSafeTelemetry(hardware, baseline);
        }
        catch (PowerTransitionDetectedException ex)
        {
            Console.Error.WriteLine(
                $"Qualification aborted before any fan write: {ex.Message}");
            return 118;
        }

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
                $"Qualification refused: EC baseline unavailable: {ex.Message}");
            return 113;
        }

        Console.WriteLine($"EC baseline: {FormatControlEvidence(baselineEc)}");
        EnsureFirmwareReleasedAndGuardsSane(baselineEc);

        var results = new List<LevelResult>();
        byte? lowerBoundaryLevel = null;
        string? lowerBoundaryReason = null;
        byte? upperFailureLevel = null;
        string? upperFailureReason = null;

        // Known-good anchor first. If this fails, there is no basis for a broad
        // characterization run.
        foreach (var level in AnchorLevels)
        {
            var outcome = await QualifyLevelAsync(
                hardware,
                reader,
                control,
                probe,
                level,
                allowRunningFloorStop: false,
                cancellationToken).ConfigureAwait(false);

            if (outcome.RestoreFailed)
            {
                return 114;
            }

            if (outcome.PowerTransitionDetected)
            {
                Console.Error.WriteLine(
                    $"POWER TRANSITION DETECTED at {level}/{level}: {outcome.Message}");
                Console.Error.WriteLine(
                    "Extended sweep aborted after firmware-restore attempt; no further levels will be written.");
                return 118;
            }

            if (!outcome.Passed)
            {
                Console.Error.WriteLine(
                    $"Anchor {level}/{level} failed; extended sweep aborted: {outcome.Message}");
                return 115;
            }

            results.Add(outcome.Result!);
        }

        Console.WriteLine();
        Console.WriteLine("=== LOWER SWEEP: 29 -> 10 ===");

        foreach (var level in LowerLevels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await QualifyLevelAsync(
                hardware,
                reader,
                control,
                probe,
                level,
                allowRunningFloorStop: true,
                cancellationToken).ConfigureAwait(false);

            if (outcome.RestoreFailed)
            {
                return 114;
            }

            if (outcome.PowerTransitionDetected)
            {
                Console.Error.WriteLine(
                    $"POWER TRANSITION DETECTED at {level}/{level}: {outcome.Message}");
                Console.Error.WriteLine(
                    "Extended sweep aborted after firmware-restore attempt; no upper sweep will be started.");
                return 118;
            }

            if (outcome.RunningFloorReached)
            {
                lowerBoundaryLevel = level;
                lowerBoundaryReason = outcome.Message;
                Console.WriteLine();
                Console.WriteLine(
                    $"LOW-RANGE STOP at {level}/{level}: {outcome.Message}");
                Console.WriteLine(
                    "No smaller level will be written in this run.");
                break;
            }

            if (!outcome.Passed)
            {
                lowerBoundaryLevel = level;
                lowerBoundaryReason = outcome.Message;
                Console.WriteLine();
                Console.WriteLine(
                    $"LOW-RANGE STOP at {level}/{level}: {outcome.Message}");
                Console.WriteLine(
                    "No smaller level will be written in this run.");
                break;
            }

            results.Add(outcome.Result!);
        }

        Console.WriteLine();
        Console.WriteLine("=== UPPER SWEEP: 31 -> 50 ===");

        foreach (var level in UpperLevels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await QualifyLevelAsync(
                hardware,
                reader,
                control,
                probe,
                level,
                allowRunningFloorStop: false,
                cancellationToken).ConfigureAwait(false);

            if (outcome.RestoreFailed)
            {
                return 114;
            }

            if (outcome.PowerTransitionDetected)
            {
                Console.Error.WriteLine(
                    $"POWER TRANSITION DETECTED at {level}/{level}: {outcome.Message}");
                Console.Error.WriteLine(
                    "Extended sweep aborted after firmware-restore attempt; no larger level will be written.");
                return 118;
            }

            if (!outcome.Passed)
            {
                upperFailureLevel = level;
                upperFailureReason = outcome.Message;
                Console.WriteLine();
                Console.WriteLine(
                    $"UPPER-RANGE STOP at {level}/{level}: {outcome.Message}");
                Console.WriteLine(
                    "No larger level will be written in this run.");
                break;
            }

            results.Add(outcome.Result!);
        }

        PrintSummary(results, lowerBoundaryLevel, lowerBoundaryReason,
            upperFailureLevel, upperFailureReason);

        var verifiedLevels = results.Select(result => result.Level).ToHashSet();
        var allLevelsVerified =
            Enumerable.Range(
                    Hp8C40ExtendedFanLevelQualificationControl.MinimumQualificationLevel,
                    Hp8C40ExtendedFanLevelQualificationControl.MaximumQualificationLevel -
                    Hp8C40ExtendedFanLevelQualificationControl.MinimumQualificationLevel + 1)
                .All(level => verifiedLevels.Contains((byte)level));

        if (!allLevelsVerified)
        {
            Console.WriteLine();
            Console.WriteLine(
                "RESULT: PARTIAL (safe characterization boundary reached before every " +
                "level 10..50 could be verified).");
            Console.WriteLine(
                "No unverified level beyond the detected boundary was written.");
            return 116;
        }

        Console.WriteLine();
        Console.WriteLine(
            "RESULT: PASS (every equal level 10..50 was accepted, EC-acknowledged, " +
            "physically sampled and individually restored to firmware authority).");
        Console.WriteLine(
            "NOTE: PASS characterizes the range; it does not automatically promote " +
            "10..50 into the production backend.");

        return 0;
        }
    }

    private static async Task<LevelOutcome> QualifyLevelAsync(
        HardwareIdentity hardware,
        HardwareTelemetryReader reader,
        Hp8C40ExtendedFanLevelQualificationControl control,
        Hp8C40EcControlStateProbe probe,
        byte level,
        bool allowRunningFloorStop,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Qualification level {level}/{level} ===");

        Exception? stepFailure = null;
        Exception? restoreFailure = null;
        LevelResult? result = null;
        var runningFloorReached = false;
        var writeAttempted = false;
        var stepStarted = Stopwatch.GetTimestamp();

        try
        {
            var pre = reader.ReadSnapshot();
            EnsureSafeTelemetry(hardware, pre);

            var preEc = await ReadControlEvidenceWithRetryAsync(
                probe,
                $"level {level} pre-write",
                cancellationToken).ConfigureAwait(false);
            EnsureFirmwareReleasedAndGuardsSane(preEc);

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

            var settleStarted = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(settleStarted) < SettleDuration)
            {
                EnsureStepWindow(stepStarted);
                await Task.Delay(
                    SampleInterval,
                    cancellationToken).ConfigureAwait(false);

                var settleSample = reader.ReadSnapshot();
                EnsureSafeTelemetry(hardware, settleSample);

                var ec = await ReadControlEvidenceWithRetryAsync(
                    probe,
                    $"level {level} settling",
                    cancellationToken).ConfigureAwait(false);
                EnsureActiveOwnershipAndGuards(ec, level);

                if (allowRunningFloorStop &&
                    Stopwatch.GetElapsedTime(settleStarted) >= TimeSpan.FromSeconds(2) &&
                    (settleSample.CpuFanRpm < MinimumSafeCustomRpm ||
                     settleSample.GpuFanRpm < MinimumSafeCustomRpm))
                {
                    runningFloorReached = true;
                    throw new RunningFloorException(
                        $"fan feedback crossed the conservative running floor: " +
                        $"CPU={settleSample.CpuFanRpm:0} RPM " +
                        $"GPU={settleSample.GpuFanRpm:0} RPM");
                }
            }

            var cpuSamples = new List<double>(SamplesPerLevel);
            var gpuSamples = new List<double>(SamplesPerLevel);

            for (var index = 1; index <= SamplesPerLevel; index++)
            {
                EnsureStepWindow(stepStarted);

                var sample = reader.ReadSnapshot();
                EnsureSafeTelemetry(hardware, sample);

                var ec = await ReadControlEvidenceWithRetryAsync(
                    probe,
                    $"level {level} sample {index}",
                    cancellationToken).ConfigureAwait(false);
                EnsureActiveOwnershipAndGuards(ec, level);

                if (sample.CpuFanRpm < MinimumSafeCustomRpm ||
                    sample.GpuFanRpm < MinimumSafeCustomRpm)
                {
                    if (allowRunningFloorStop)
                    {
                        runningFloorReached = true;
                        throw new RunningFloorException(
                            $"steady fan feedback crossed the conservative running floor: " +
                            $"CPU={sample.CpuFanRpm:0} RPM GPU={sample.GpuFanRpm:0} RPM");
                    }

                    throw new InvalidOperationException(
                        $"unexpected low fan feedback: CPU={sample.CpuFanRpm:0} RPM " +
                        $"GPU={sample.GpuFanRpm:0} RPM");
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

            result = new LevelResult(
                level,
                Median(cpuSamples),
                Median(gpuSamples),
                cpuSamples.Min(),
                cpuSamples.Max(),
                gpuSamples.Min(),
                gpuSamples.Max());
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
            return new LevelOutcome(
                Passed: false,
                RunningFloorReached: false,
                RestoreFailed: true,
                PowerTransitionDetected: false,
                Message: restoreFailure.Message,
                Result: null);
        }

        if (stepFailure is OperationCanceledException)
        {
            throw stepFailure;
        }

        if (stepFailure is PowerTransitionDetectedException)
        {
            return new LevelOutcome(
                Passed: false,
                RunningFloorReached: false,
                RestoreFailed: false,
                PowerTransitionDetected: true,
                Message: stepFailure.Message,
                Result: null);
        }

        if (stepFailure is RunningFloorException)
        {
            return new LevelOutcome(
                Passed: false,
                RunningFloorReached: true,
                RestoreFailed: false,
                PowerTransitionDetected: false,
                Message: stepFailure.Message,
                Result: null);
        }

        if (stepFailure is not null)
        {
            return new LevelOutcome(
                Passed: false,
                RunningFloorReached: runningFloorReached,
                RestoreFailed: false,
                PowerTransitionDetected: false,
                Message: stepFailure.Message,
                Result: null);
        }

        Console.WriteLine(
            $"Level {level}: CPU median={result!.CpuMedianRpm:0} RPM " +
            $"({result.CpuMinimumRpm:0}-{result.CpuMaximumRpm:0}), " +
            $"GPU median={result.GpuMedianRpm:0} RPM " +
            $"({result.GpuMinimumRpm:0}-{result.GpuMaximumRpm:0}).");

        return new LevelOutcome(
            Passed: true,
            RunningFloorReached: false,
            RestoreFailed: false,
            PowerTransitionDetected: false,
            Message: "passed",
            Result: result);
    }

    private static void PrintSummary(
        IReadOnlyList<LevelResult> results,
        byte? lowerBoundaryLevel,
        string? lowerBoundaryReason,
        byte? upperFailureLevel,
        string? upperFailureReason)
    {
        Console.WriteLine();
        Console.WriteLine("Observed extended-range summary:");

        var ordered = results.OrderBy(result => result.Level).ToArray();
        foreach (var result in ordered)
        {
            var productionTag =
                result.Level >= Hp8C40TargetProfile.MinimumValidatedFanLevel &&
                result.Level <= Hp8C40TargetProfile.MaximumValidatedFanLevel
                    ? "production-regression"
                    : "qualification-only";

            Console.WriteLine(
                $"  {result.Level,2}/{result.Level,2}: " +
                $"CPU {result.CpuMedianRpm,5:0} RPM " +
                $"[{result.CpuMinimumRpm:0},{result.CpuMaximumRpm:0}] | " +
                $"GPU {result.GpuMedianRpm,5:0} RPM " +
                $"[{result.GpuMinimumRpm:0},{result.GpuMaximumRpm:0}] | " +
                productionTag);
        }

        Console.WriteLine();
        Console.WriteLine("Adjacent median deltas:");
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Level != ordered[i - 1].Level + 1)
            {
                continue;
            }

            Console.WriteLine(
                $"  {ordered[i - 1].Level}->{ordered[i].Level}: " +
                $"CPU {ordered[i].CpuMedianRpm - ordered[i - 1].CpuMedianRpm:+0;-0;0} RPM | " +
                $"GPU {ordered[i].GpuMedianRpm - ordered[i - 1].GpuMedianRpm:+0;-0;0} RPM");
        }

        if (lowerBoundaryLevel.HasValue)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Lower characterization boundary: {lowerBoundaryLevel}/{lowerBoundaryLevel}");
            Console.WriteLine($"  {lowerBoundaryReason}");
        }

        if (upperFailureLevel.HasValue)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Upper characterization boundary: {upperFailureLevel}/{upperFailureLevel}");
            Console.WriteLine($"  {upperFailureReason}");
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
                $"telemetry age jumped to {telemetryAge.TotalSeconds:0.0} s " +
                $"(maximum expected {MaximumTelemetryAge.TotalSeconds:0.0} s). " +
                "This is consistent with a sleep/hibernate/power-transition gap.");
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
                "SafetyGate refused qualification telemetry: " +
                string.Join(" | ", safety.Reasons));
        }

        if (snapshot.CpuControlTemperatureC > MaximumCpuTemperatureC ||
            snapshot.GpuTemperatureC > MaximumGpuTemperatureC ||
            snapshot.CpuPackagePowerW > MaximumCpuPowerW ||
            snapshot.GpuPowerW > MaximumGpuPowerW)
        {
            throw new InvalidOperationException(
                "Light-load qualification envelope exceeded: " +
                $"effective CPU={snapshot.CpuControlTemperatureC:0.0} C " +
                $"(limit {MaximumCpuTemperatureC:0} C), " +
                $"CPU package={snapshot.CpuPackagePowerW:0.0} W " +
                $"(limit {MaximumCpuPowerW:0} W), " +
                $"GPU={snapshot.GpuTemperatureC:0.0} C " +
                $"(limit {MaximumGpuTemperatureC:0} C), " +
                $"GPU power={snapshot.GpuPowerW:0.0} W " +
                $"(limit {MaximumGpuPowerW:0} W), " +
                $"CPU load={snapshot.CpuLoadPercent:0.0}%, " +
                $"GPU load={snapshot.GpuLoadPercent:0.0}%.");
        }
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
                var state = probe.ReadControlEvidence();

                // This harness writes only equal CPU/GPU setpoints and accepts
                // firmware release only as FF/FF. A mixed or asymmetric pair
                // is therefore either an external ownership conflict or a
                // transient/torn EC observation. Re-read it before deciding.
                if (state.CpuSetpoint != state.GpuSetpoint)
                {
                    throw new InvalidDataException(
                        $"unexpected asymmetric setpoint sample " +
                        $"CPU={state.CpuSetpoint} GPU={state.GpuSetpoint}");
                }

                if (state.MaxFan != 0x00 || state.FanSwitch != 0x00)
                {
                    throw new InvalidDataException(
                        $"unexpected control-guard sample " +
                        $"MaxFan=0x{state.MaxFan:X2} FanSwitch=0x{state.FanSwitch:X2}");
                }

                return state;
            }
            catch (Exception ex)
                when (ex is IOException or TimeoutException or InvalidDataException)
            {
                lastError = ex;
                Console.WriteLine(
                    $"EC transient during {context}: retry " +
                    $"{attempt}/{EcEvidenceReadAttempts}: {ex.Message}");

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
                $"Qualification ownership changed unexpectedly at " +
                $"{expectedLevel}/{expectedLevel}: " +
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
                await Task.Delay(
                    100,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (last.CpuSetpoint == expectedLevel &&
                last.GpuSetpoint == expectedLevel)
            {
                EnsureGuardsSane(last);
                return last;
            }

            await Task.Delay(
                100,
                cancellationToken).ConfigureAwait(false);
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
                await Task.Delay(
                    250,
                    CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (last.CpuSetpoint == byte.MaxValue &&
                last.GpuSetpoint == byte.MaxValue)
            {
                EnsureGuardsSane(last);
                return last;
            }

            await Task.Delay(
                250,
                CancellationToken.None).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Firmware restore did not reach FF/FF within " +
            $"{timeout.TotalSeconds:0.0} s.");
    }

    private static void EnsureStepWindow(long started)
    {
        if (Stopwatch.GetElapsedTime(started) > MaximumStepWindow)
        {
            throw new TimeoutException(
                $"Qualification step exceeded {MaximumStepWindow.TotalSeconds:0} seconds.");
        }
    }

    private static string FormatControlEvidence(Hp8C40EcControlState state) =>
        $"level CPU={state.CpuSetpoint} GPU={state.GpuSetpoint} | " +
        $"max=0x{state.MaxFan:X2} switch=0x{state.FanSwitch:X2} | " +
        $"RPM CPU={state.CpuRpm} GPU={state.GpuRpm}";

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

    private sealed record LevelOutcome(
        bool Passed,
        bool RunningFloorReached,
        bool RestoreFailed,
        bool PowerTransitionDetected,
        string Message,
        LevelResult? Result);

    private sealed class RunningFloorException : Exception
    {
        public RunningFloorException(string message)
            : base(message)
        {
        }
    }

    private sealed class PowerTransitionDetectedException : Exception
    {
        public PowerTransitionDetectedException(string message)
            : base(message)
        {
        }
    }
}
