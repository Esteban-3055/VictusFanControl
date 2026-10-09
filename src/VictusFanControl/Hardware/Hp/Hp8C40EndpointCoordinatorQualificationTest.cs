using System.Diagnostics;
using VictusFanControl.Control;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Qualification-only endpoint integration gate.
///
/// This uses the real FanControlCoordinator and Hp8C40FanControlBackend control
/// logic, but injects the already-physically-characterized 10..50 command
/// envelope and the qualification-only WMI writer. Production defaults remain
/// 30..36 until a later promotion commit.
///
/// Endpoints are exercised independently:
/// firmware -> 10 -> firmware
/// firmware -> 50 -> firmware
///
/// Each endpoint requires SafetyGate admission, backend EC/tach acknowledgement,
/// terminal RPM convergence, continuous coordinator supervision and verified
/// FF/FF restore.
/// </summary>
public static class Hp8C40EndpointCoordinatorQualificationTest
{
    private static readonly byte[] EndpointLevels =
    [
        (byte)Hp8C40TargetProfile.MinimumPhysicallyQualifiedFanLevel,
        (byte)Hp8C40TargetProfile.MaximumPhysicallyQualifiedFanLevel
    ];

    private const int RequiredTerminalSamples = 2;
    private const int MinimumSafeCustomRpm = 750;
    private const byte MinimumBatteryPercentForQualification = 20;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TerminalConvergenceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BaselineReadyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BaselineRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RestoreVerifyTimeout = TimeSpan.FromSeconds(5);

    private const double MaximumCpuTemperatureC = 80;
    private const double MaximumGpuTemperatureC = 75;
    private const double MaximumCpuPowerW = 50;
    private const double MaximumGpuPowerW = 70;

    public static async Task<int> RunAsync(
        string modulesDirectory,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("HP 8C40 ENDPOINT COORDINATOR QUALIFICATION");
        Console.WriteLine(
            "Route: SafetyGate -> FanControlCoordinator -> Hp8C40FanControlBackend logic -> " +
            "qualification WMI writer -> EC/tach ACK -> restore.");
        Console.WriteLine(
            "Endpoints: firmware -> 10 -> firmware, then firmware -> 50 -> firmware.");
        Console.WriteLine(
            "Production defaults remain 30..36; this gate does not promote them.");
        Console.WriteLine();

        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var identityReason))
        {
            Console.Error.WriteLine($"Endpoint qualification refused: {identityReason}");
            return 131;
        }

        var conflict = FindKnownConflictingControllerProcess();
        if (conflict is not null)
        {
            Console.Error.WriteLine(
                $"Endpoint qualification refused while '{conflict}' is running. " +
                "Close OmenMon/OmenMon-Reborn/VictusFanControl GUI first.");
            return 132;
        }

        SystemSleepInhibitor sleepInhibitor;
        try
        {
            sleepInhibitor = SystemSleepInhibitor.Acquire(
                "HP 8C40 endpoint coordinator qualification");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Endpoint qualification refused: Windows sleep inhibition could not be acquired: {ex.Message}");
            return 133;
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
                    "Endpoint qualification refused: telemetry backends are not initialized.");
                foreach (var line in reader.GetBackendDiagnostics())
                {
                    Console.Error.WriteLine(line);
                }

                return 134;
            }

            try
            {
                EnsureQualificationPowerStatus(SystemPowerStatusReader.Read());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Endpoint qualification refused by AC/battery sanity gate: {ex.Message}");
                return 135;
            }

            Console.WriteLine(
                $"AC/battery sanity baseline: {SystemPowerStatusReader.Read()}");
            Console.WriteLine();

            Console.WriteLine("Priming differential telemetry counters...");
            _ = reader.ReadSnapshot();
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            var results = new List<EndpointResult>(EndpointLevels.Length);

            foreach (var level in EndpointLevels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await QualifyEndpointAsync(
                    hardware,
                    reader,
                    modulesDirectory,
                    level,
                    cancellationToken).ConfigureAwait(false);

                if (!result.Passed)
                {
                    Console.Error.WriteLine(
                        $"Endpoint {level}/{level} qualification failed safely: {result.Message}");
                    return result.RestoreFailed ? 137 : 136;
                }

                results.Add(result);

                // Let firmware settle before the next independent admission.
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }

            Console.WriteLine();
            Console.WriteLine("Endpoint coordinator summary:");
            foreach (var result in results)
            {
                Console.WriteLine(
                    $"  {result.Level,2}/{result.Level,2}: " +
                    $"converged in {result.ConvergenceSeconds,4:0.0} s | " +
                    $"CPU {result.TerminalCpuRpm,4:0} RPM | " +
                    $"GPU {result.TerminalGpuRpm,4:0} RPM | restore=FF/FF");
            }

            Console.WriteLine();
            Console.WriteLine(
                "RESULT: PASS (both physically characterized endpoints passed " +
                "SafetyGate -> Coordinator -> backend logic -> EC/tach ACK -> terminal convergence -> restore).");
            Console.WriteLine(
                "NOTE: production remains 30..36 until an explicit promotion commit and " +
                "post-promotion production-path regression are completed.");

            return 0;
        }
    }

    private static async Task<EndpointResult> QualifyEndpointAsync(
        HardwareIdentity hardware,
        HardwareTelemetryReader reader,
        string modulesDirectory,
        byte level,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Endpoint {level}/{level} ===");

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
            return EndpointResult.Fail(level, $"baseline refused: {ex.Message}");
        }

        Console.WriteLine("Baseline:");
        ConsoleTelemetryPrinter.Print(baseline);

        var probe = new Hp8C40EcControlStateProbe(modulesDirectory);
        Hp8C40EcControlState before;
        try
        {
            before = probe.ReadControlEvidence();
            EnsureFirmwareReleasedAndGuardsSane(before);
        }
        catch (Exception ex)
        {
            return EndpointResult.Fail(
                level,
                $"firmware baseline EC evidence invalid: {ex.Message}");
        }

        Console.WriteLine($"EC before: {FormatControlEvidence(before)}");

        await using var coordinator =
            new FanControlCoordinator(
                new Hp8C40FanControlBackend(
                    new QualificationHardware(modulesDirectory),
                    targetSupported: true,
                    supportDetail:
                        "Exact HP 8C40 qualification target with physically characterized endpoint envelope.",
                    minimumCommandLevel:
                        Hp8C40TargetProfile.MinimumPhysicallyQualifiedFanLevel,
                    maximumCommandLevel:
                        Hp8C40TargetProfile.MaximumPhysicallyQualifiedFanLevel));

        if (!coordinator.BackendCanWrite)
        {
            return EndpointResult.Fail(level, "qualification backend is not write-capable.");
        }

        Exception? endpointFailure = null;
        Exception? restoreFailure = null;
        var customMayHaveBeenEntered = false;
        var writeMayHaveOccurred = false;
        double terminalCpu = double.NaN;
        double terminalGpu = double.NaN;
        double convergenceSeconds = double.NaN;

        try
        {
            var admissionSafety = BuildSafety(
                hardware,
                baseline,
                coordinator.BackendCanWrite);

            if (!admissionSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate refused endpoint admission: " +
                    string.Join(" | ", admissionSafety.Reasons));
            }

            var entered = await coordinator.TryEnterCustomAsync(
                admissionSafety,
                cancellationToken).ConfigureAwait(false);

            if (!entered || coordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "FanControlCoordinator did not grant Custom authority.");
            }

            customMayHaveBeenEntered = true;
            Console.WriteLine($"Coordinator authority: {coordinator.Authority}");

            EnsureQualificationPowerStatus(SystemPowerStatusReader.Read());

            var commandSnapshot = reader.ReadSnapshot();
            EnsureSafeTelemetry(hardware, commandSnapshot);

            var commandSafety = BuildSafety(
                hardware,
                commandSnapshot,
                coordinator.BackendCanWrite);

            if (!commandSafety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped before endpoint command: " +
                    string.Join(" | ", commandSafety.Reasons));
            }

            Console.WriteLine($"Applying endpoint command {level}/{level}...");

            writeMayHaveOccurred = true;
            await coordinator.ApplyAsync(
                new FanCommand(
                    level,
                    level,
                    "bounded HP 8C40 endpoint coordinator qualification"),
                commandSafety,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine(
                $"Backend command acknowledged. Authority={coordinator.Authority}");

            var convergence = await WaitForTerminalConvergenceAsync(
                hardware,
                reader,
                probe,
                coordinator,
                level,
                cancellationToken).ConfigureAwait(false);

            terminalCpu = convergence.CpuRpm;
            terminalGpu = convergence.GpuRpm;
            convergenceSeconds = convergence.Seconds;

            Console.WriteLine(
                $"Endpoint {level}/{level} terminal convergence: " +
                $"{convergenceSeconds:0.0} s | CPU {terminalCpu:0} RPM | GPU {terminalGpu:0} RPM");
        }
        catch (Exception ex)
        {
            endpointFailure = ex;
        }
        finally
        {
            if (customMayHaveBeenEntered || writeMayHaveOccurred)
            {
                Console.WriteLine("Requesting coordinator handoff to HP firmware...");
                try
                {
                    await coordinator.RestoreFirmwareAsync(
                        "Endpoint coordinator qualification completed/aborted.",
                        CancellationToken.None).ConfigureAwait(false);

                    Console.WriteLine(
                        $"Coordinator authority after restore: {coordinator.Authority}");

                    var restored = await WaitForFirmwareReleaseAsync(
                        probe,
                        RestoreVerifyTimeout).ConfigureAwait(false);

                    Console.WriteLine(
                        $"EC after restore: {FormatControlEvidence(restored)}");
                }
                catch (Exception ex)
                {
                    restoreFailure = ex;
                    Console.Error.WriteLine(
                        $"CRITICAL: endpoint restore failed: {ex.Message}");
                }
            }
        }

        if (restoreFailure is not null)
        {
            return new EndpointResult(
                level,
                Passed: false,
                RestoreFailed: true,
                Message: restoreFailure.Message,
                TerminalCpuRpm: terminalCpu,
                TerminalGpuRpm: terminalGpu,
                ConvergenceSeconds: convergenceSeconds);
        }

        if (endpointFailure is OperationCanceledException)
        {
            throw endpointFailure;
        }

        if (endpointFailure is not null)
        {
            return EndpointResult.Fail(level, endpointFailure.Message);
        }

        if (coordinator.Authority != FanAuthority.Firmware)
        {
            return EndpointResult.Fail(
                level,
                $"coordinator ended with unexpected authority {coordinator.Authority}.");
        }

        Console.WriteLine($"Endpoint {level}/{level}: PASS");

        return new EndpointResult(
            level,
            Passed: true,
            RestoreFailed: false,
            Message: "passed",
            TerminalCpuRpm: terminalCpu,
            TerminalGpuRpm: terminalGpu,
            ConvergenceSeconds: convergenceSeconds);
    }

    private static async Task<(double CpuRpm, double GpuRpm, double Seconds)>
        WaitForTerminalConvergenceAsync(
            HardwareIdentity hardware,
            HardwareTelemetryReader reader,
            Hp8C40EcControlStateProbe probe,
            FanControlCoordinator coordinator,
            byte level,
            CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var expectedRpm = level * 100.0;
        var toleranceRpm = Math.Max(150.0, expectedRpm * 0.05);
        var consecutive = 0;
        double lastCpu = double.NaN;
        double lastGpu = double.NaN;

        while (Stopwatch.GetElapsedTime(started) < TerminalConvergenceTimeout)
        {
            await Task.Delay(
                SampleInterval,
                cancellationToken).ConfigureAwait(false);

            EnsureQualificationPowerStatus(SystemPowerStatusReader.Read());

            var sample = reader.ReadSnapshot();
            EnsureSafeTelemetry(hardware, sample);

            lastCpu = sample.CpuFanRpm!.Value;
            lastGpu = sample.GpuFanRpm!.Value;

            if (lastCpu < MinimumSafeCustomRpm ||
                lastGpu < MinimumSafeCustomRpm)
            {
                throw new InvalidOperationException(
                    $"endpoint {level}/{level} fan feedback crossed the conservative floor: " +
                    $"CPU={lastCpu:0} GPU={lastGpu:0} RPM.");
            }

            var safety = BuildSafety(
                hardware,
                sample,
                coordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted)
            {
                throw new InvalidOperationException(
                    "SafetyGate dropped during endpoint supervision: " +
                    string.Join(" | ", safety.Reasons));
            }

            var retained = await coordinator.EnforceSafetyAsync(
                safety,
                $"endpoint {level}/{level} terminal convergence",
                cancellationToken).ConfigureAwait(false);

            if (!retained || coordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    $"coordinator did not retain Custom authority during endpoint {level}/{level}.");
            }

            var ec = probe.ReadControlEvidence();
            EnsureActiveOwnershipAndGuards(ec, level);

            var elapsed = Stopwatch.GetElapsedTime(started);
            var cpuInBand = Math.Abs(lastCpu - expectedRpm) <= toleranceRpm;
            var gpuInBand = Math.Abs(lastGpu - expectedRpm) <= toleranceRpm;

            Console.WriteLine(
                $"  t={elapsed.TotalSeconds,4:0.0}s | " +
                $"CPU {lastCpu,4:0} RPM | GPU {lastGpu,4:0} RPM | " +
                $"target≈{expectedRpm:0}±{toleranceRpm:0} | " +
                $"CPU {sample.CpuControlTemperatureC:0.0} C | " +
                $"GPU {sample.GpuTemperatureC:0.0} C");

            if (cpuInBand && gpuInBand)
            {
                consecutive++;
                if (consecutive >= RequiredTerminalSamples)
                {
                    return (lastCpu, lastGpu, elapsed.TotalSeconds);
                }
            }
            else
            {
                consecutive = 0;
            }
        }

        throw new TimeoutException(
            $"endpoint {level}/{level} did not reach two consecutive terminal-band " +
            $"samples within {TerminalConvergenceTimeout.TotalSeconds:0} s. " +
            $"Last RPM={lastCpu:0}/{lastGpu:0}.");
    }

    private static async Task<TelemetrySnapshot> WaitForSafeBaselineAsync(
        HardwareIdentity hardware,
        HardwareTelemetryReader reader,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        string lastReason = "no sample";

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
            catch (InvalidOperationException ex)
            {
                lastReason = ex.Message;
                Console.WriteLine($"Telemetry baseline not ready yet: {lastReason}");
            }

            await Task.Delay(
                BaselineRetryInterval,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"telemetry did not become SafetyGate-ready within " +
            $"{BaselineReadyTimeout.TotalSeconds:0} s. Last reason: {lastReason}");
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

    private static void EnsureSafeTelemetry(
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot)
    {
        var age = DateTimeOffset.UtcNow - snapshot.Timestamp;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age > MaximumTelemetryAge)
        {
            throw new InvalidOperationException(
                $"telemetry age jumped to {age.TotalSeconds:0.0} s.");
        }

        var safety = BuildSafety(hardware, snapshot, fanWritePathPresent: true);
        if (!safety.CustomControlPermitted)
        {
            throw new InvalidOperationException(
                "SafetyGate refused endpoint telemetry: " +
                string.Join(" | ", safety.Reasons));
        }

        if (snapshot.CpuControlTemperatureC > MaximumCpuTemperatureC ||
            snapshot.GpuTemperatureC > MaximumGpuTemperatureC ||
            snapshot.CpuPackagePowerW > MaximumCpuPowerW ||
            snapshot.GpuPowerW > MaximumGpuPowerW)
        {
            throw new InvalidOperationException(
                "Light-load endpoint envelope exceeded: " +
                $"effective CPU={snapshot.CpuControlTemperatureC:0.0} C, " +
                $"CPU package={snapshot.CpuPackagePowerW:0.0} W, " +
                $"GPU={snapshot.GpuTemperatureC:0.0} C, " +
                $"GPU power={snapshot.GpuPowerW:0.0} W.");
        }
    }

    private static void EnsureQualificationPowerStatus(
        SystemPowerStatusSample status)
    {
        if (!status.AcOnline)
        {
            throw new InvalidOperationException(
                $"AC power unexpectedly went offline ({status}).");
        }

        if (!status.BatteryPresent)
        {
            throw new InvalidOperationException(
                $"Windows reported no usable battery ({status}).");
        }

        if (status.BatteryPercent > 100)
        {
            throw new InvalidOperationException(
                $"Windows reported an unknown battery percentage ({status}).");
        }

        if (status.BatteryPercent < MinimumBatteryPercentForQualification)
        {
            throw new InvalidOperationException(
                $"Windows reported battery={status.BatteryPercent}% while AC is online; " +
                $"qualification requires at least {MinimumBatteryPercentForQualification}%.");
        }
    }

    private static void EnsureFirmwareReleasedAndGuardsSane(
        Hp8C40EcControlState state)
    {
        if (state.CpuSetpoint != byte.MaxValue ||
            state.GpuSetpoint != byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"expected firmware-released FF/FF, observed " +
                $"{state.CpuSetpoint}/{state.GpuSetpoint}.");
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
                $"endpoint ownership changed unexpectedly: expected " +
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
                $"unexpected HP guard state MaxFan=0x{state.MaxFan:X2} " +
                $"FanSwitch=0x{state.FanSwitch:X2}.");
        }
    }

    private static async Task<Hp8C40EcControlState> WaitForFirmwareReleaseAsync(
        Hp8C40EcControlStateProbe probe,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            try
            {
                var state = probe.ReadControlEvidence();
                if (state.CpuSetpoint == byte.MaxValue &&
                    state.GpuSetpoint == byte.MaxValue &&
                    state.MaxFan == 0x00 &&
                    state.FanSwitch == 0x00)
                {
                    return state;
                }
            }
            catch (Exception ex)
                when (ex is IOException or TimeoutException or InvalidDataException)
            {
                // Bounded retry below.
            }

            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"firmware restore did not reach sane FF/FF state within " +
            $"{timeout.TotalSeconds:0.0} s.");
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

    private sealed class QualificationHardware : IHp8C40FanHardware
    {
        private readonly Hp8C40ExtendedFanLevelQualificationControl _writer = new();
        private readonly Hp8C40BiosFanControl _bios = new();
        private readonly AcpiEcReader _ec;

        public QualificationHardware(string modulesDirectory)
        {
            _ec = new AcpiEcReader(
                Path.Combine(modulesDirectory, "LpcACPIEC.bin"));
        }

        public Hp8C40EcControlState ReadEcState()
        {
            var layout = Hp8C40TargetProfile.Instance.FanEcLayout;
            var setpoint = _ec.ReadFanSetpoint(layout);
            var guard = _ec.ReadFanControlGuard(layout);
            var tachs = _ec.ReadFanTachometers(layout);

            return new Hp8C40EcControlState(
                CpuRateTarget: byte.MaxValue,
                GpuRateTarget: byte.MaxValue,
                CpuRate: byte.MaxValue,
                GpuRate: byte.MaxValue,
                CpuSetpoint: setpoint.CpuSetpoint,
                GpuSetpoint: setpoint.GpuSetpoint,
                Diagnostic62: byte.MaxValue,
                Diagnostic63: byte.MaxValue,
                Mode: byte.MaxValue,
                MaxFan: guard.MaxFan,
                FanSwitch: guard.FanSwitch,
                CpuRpm: tachs.CpuRpm,
                GpuRpm: tachs.GpuRpm);
        }

        public (byte CpuSetpoint, byte GpuSetpoint) ReadSetpoint()
        {
            var state = _ec.ReadFanSetpoint(
                Hp8C40TargetProfile.Instance.FanEcLayout);
            return (state.CpuSetpoint, state.GpuSetpoint);
        }

        public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
            _bios.GetCurrentFanLevels();

        public void SetFanLevel(byte cpuLevel, byte gpuLevel)
        {
            if (cpuLevel != gpuLevel)
            {
                throw new ArgumentException(
                    "HP 8C40 endpoint qualification permits equal levels only.");
            }

            _writer.SetEqualLevel(cpuLevel);
        }

        public void RestoreFirmwareAuto() =>
            _writer.RestoreFirmwareAuto();

        public void Dispose() =>
            _ec.Dispose();
    }

    private sealed record EndpointResult(
        byte Level,
        bool Passed,
        bool RestoreFailed,
        string Message,
        double TerminalCpuRpm,
        double TerminalGpuRpm,
        double ConvergenceSeconds)
    {
        public static EndpointResult Fail(byte level, string message) =>
            new(
                level,
                Passed: false,
                RestoreFailed: false,
                Message: message,
                TerminalCpuRpm: double.NaN,
                TerminalGpuRpm: double.NaN,
                ConvergenceSeconds: double.NaN);
    }
}
