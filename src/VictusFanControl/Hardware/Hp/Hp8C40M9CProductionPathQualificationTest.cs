using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

public static class Hp8C40M9CProductionPathQualificationTest
{
    public static readonly bool PhysicalExecutionAuthorized = false;

    public const string RequiredToken =
        Hp8C40ProductionWatchdogGate.M9CPhysicalQualificationToken;

    public const int QualificationLevel = 30;
    public const int RequiredConsecutivePreWriteSamples = 3;
    public const int MaximumPreWriteSamples = 10;
    public const int SupervisionSamples = 5;
    public const int SampleIntervalMilliseconds = 1000;
    public const double CpuPhysicalAbortC = 90.0;
    public const double GpuPhysicalAbortC = 82.0;
    public const double MaximumCpuPackagePowerW = 60.0;
    public const double MaximumGpuPowerW = 75.0;

    private const byte MinimumBatteryPercent = 20;
    private static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumInterSampleGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ParentContinueTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string readyPath,
        string continuePath,
        string resultPath,
        CancellationToken cancellationToken)
    {
        // HARD BARRIER: this must precede Administrator, SMBIOS, PawnIO, pipe,
        // WMI or EC construction.
        if (!PhysicalExecutionAuthorized ||
            !Hp8C40ProductionWatchdogGate.M9CPhysicalQualificationConstructionAuthorized)
        {
            Console.Error.WriteLine(
                "M9C PHYSICAL REFUSED: controller/factory construction gates are closed.");
            return 230;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var result = "FAIL_CLOSED";
        string? failureReason = null;
        var exitCode = 231;
        var applyCalls = 0;
        var readyPublished = false;
        var continueObserved = false;
        var constructionScopeClosedBeforeCustom = false;
        var normalRestoreCompleted = false;
        var finalFirmwareOwned = false;
        var events = new List<EventEvidence>();
        var preWriteSamples = new List<SampleEvidence>();
        var supervisionSamples = new List<SampleEvidence>();
        var controllerIdentity = GetCurrentProcessIdentity();

        FanControlCoordinator? coordinator = null;
        Hp8C40EcControlStateProbe? ecProbe = null;

        try
        {
            if (!IsAdministrator())
            {
                throw new InvalidOperationException("M9C requires an elevated Administrator process.");
            }

            var hardware = HardwareIdentityReader.ReadCurrent();

            if (!Hp8C40TargetProfile.Matches(hardware, out var targetReason))
            {
                throw new InvalidOperationException($"M9C exact-target refusal: {targetReason}");
            }

            var conflict = FindKnownConflictingControllerProcess();
            if (conflict is not null)
            {
                throw new InvalidOperationException($"M9C refused while '{conflict}' is running.");
            }

            EnsurePowerStatus(SystemPowerStatusReader.Read());

            ecProbe = new Hp8C40EcControlStateProbe(modulesDirectory);
            var initial = await ReadExpectedEcConfirmedAsync(
                    ecProbe, byte.MaxValue, byte.MaxValue,
                    "initial firmware-owned baseline", cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(initial, byte.MaxValue, byte.MaxValue, "initial firmware-owned baseline");

            using var telemetry = new HardwareTelemetryReader(modulesDirectory);
            if (!telemetry.BackendsInitialized ||
                !string.Equals(
                    telemetry.TargetProfile?.Id,
                    Hp8C40TargetProfile.Instance.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "M9C exact-target telemetry backends are not fully initialized.");
            }

            var thermalConfirmation = new Hp8C40ThermalEmergencyConfirmation();

            _ = telemetry.ReadSnapshot();
            telemetry.ResetHealthWindow();
            await Task.Delay(
                    TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);

            SafetyGateResult? admittedSafety = null;
            DateTimeOffset? previousTimestamp = null;
            var healthyStreak = 0;

            for (var index = 1; index <= MaximumPreWriteSamples; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePowerStatus(SystemPowerStatusReader.Read());

                var snapshot = telemetry.ReadSnapshot();
                EnsureImmediatePhysicalLimits(snapshot, $"pre-write sample {index}");

                SafetyGateResult safety;
                try
                {
                    safety = EvaluateEffectiveSafety(
                        hardware,
                        snapshot,
                        previousTimestamp,
                        thermalConfirmation,
                        fanWritePathPresent: true);
                }
                catch (InvalidOperationException ex)
                {
                    healthyStreak = 0;
                    Console.WriteLine($"M9C_PREWRITE {index}/{MaximumPreWriteSamples} waiting: {ex.Message}");
                    previousTimestamp = snapshot.Timestamp;

                    if (index < MaximumPreWriteSamples)
                    {
                        await Task.Delay(
                                TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw;
                }

                previousTimestamp = snapshot.Timestamp;
                var lowLoad = IsBoundedSmokeLoad(snapshot);
                var accepted =
                    safety.PreconditionsReady &&
                    safety.CustomControlPermitted &&
                    !safety.ThermalEmergency &&
                    lowLoad;

                healthyStreak = accepted ? healthyStreak + 1 : 0;
                preWriteSamples.Add(ToEvidence(index, snapshot, safety, lowLoad, healthyStreak));

                Console.WriteLine(
                    $"M9C_PREWRITE {index}/{MaximumPreWriteSamples} accepted={accepted} " +
                    $"streak={healthyStreak}/{RequiredConsecutivePreWriteSamples} " +
                    $"CPU={snapshot.CpuControlTemperatureC:0.0}C {snapshot.CpuPackagePowerW:0.0}W " +
                    $"GPU={snapshot.GpuTemperatureC:0.0}C {snapshot.GpuPowerW:0.0}W");

                if (healthyStreak >= RequiredConsecutivePreWriteSamples)
                {
                    admittedSafety = safety;
                    break;
                }

                if (index < MaximumPreWriteSamples)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (admittedSafety is null)
            {
                throw new InvalidOperationException(
                    "M9C could not establish three consecutive fresh, complete, SafetyGate-permitted bounded-load snapshots.");
            }

            var preConstruction = await ReadExpectedEcConfirmedAsync(
                    ecProbe, byte.MaxValue, byte.MaxValue,
                    "before production construction", cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(preConstruction, byte.MaxValue, byte.MaxValue, "before production construction");

            var lease = new NamedPipeFanControlWatchdogLeaseClient(
                Hp8C40TargetProfile.Instance.Id,
                FanControlWatchdogLeaseContract.Hp8C40M4PipeName);

            HpFanBackendSelection selection;
            using (Hp8C40ProductionWatchdogGate.EnterM9CPhysicalQualificationConstructionScope(
                       hardware,
                       RequiredToken))
            {
                events.Add(new EventEvidence(
                    DateTimeOffset.UtcNow,
                    "M9C_CONSTRUCTION_SCOPE_ENTER",
                    "Temporary construction-only scope entered; no Custom authority or fan write yet."));

                selection = HpFanControlBackendFactory.Create(
                    modulesDirectory,
                    hardware,
                    lease);
            }

            constructionScopeClosedBeforeCustom =
                !Hp8C40ProductionWatchdogGate.IsM9CPhysicalQualificationScopeActive;

            if (!constructionScopeClosedBeforeCustom)
            {
                throw new InvalidOperationException(
                    "M9C temporary construction scope remained active after backend construction.");
            }

            if (!string.Equals(
                    selection.TargetProfile?.Id,
                    Hp8C40TargetProfile.Instance.Id,
                    StringComparison.Ordinal) ||
                selection.Backend is not Hp8C40FanControlBackend)
            {
                await selection.Backend.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    "M9C factory did not select the exact HP 8C40 production backend.");
            }

            coordinator = new FanControlCoordinator(selection.Backend);
            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "PRODUCTION_PATH_CONSTRUCTED",
                selection.Detail));

            var admitted = await coordinator.TryEnterCustomAsync(
                    admittedSafety,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!admitted || coordinator.Authority != FanAuthority.Custom)
            {
                throw new InvalidOperationException(
                    "M9C coordinator did not acquire watchdog PREPARED Custom authority.");
            }

            var afterPrepare = await ReadExpectedEcConfirmedAsync(
                    ecProbe, byte.MaxValue, byte.MaxValue,
                    "after PREPARE / before WRITE_INTENT", cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(afterPrepare, byte.MaxValue, byte.MaxValue, "after PREPARE / before WRITE_INTENT");

            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "APPLY_30_BEGIN",
                "Exactly one production factory/backend ApplyAsync(30/30) begins."));

            applyCalls++;
            await coordinator.ApplyAsync(
                    new FanCommand(
                        QualificationLevel,
                        QualificationLevel,
                        "HP 8C40 M9C production-path watchdog smoke"),
                    admittedSafety,
                    cancellationToken)
                .ConfigureAwait(false);

            if (applyCalls != 1)
            {
                throw new InvalidOperationException("M9C must issue exactly one ApplyAsync call.");
            }

            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "APPLY_30_COMPLETE",
                "WRITE_INTENT -> one real 30/30 -> EC+dual-tach ACK -> COMMIT completed."));

            var owned = await ReadExpectedEcConfirmedAsync(
                    ecProbe, QualificationLevel, QualificationLevel,
                    "post-Commit ownership", cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedEc(owned, QualificationLevel, QualificationLevel, "post-Commit ownership");

            if (owned.CpuRpm == 0 || owned.GpuRpm == 0)
            {
                throw new InvalidOperationException("M9C post-Commit dual-tach evidence is zero.");
            }

            await WriteJsonAsync(
                    readyPath,
                    new
                    {
                        schemaVersion = 1,
                        gate = "M9C",
                        targetProfileId = Hp8C40TargetProfile.Instance.Id,
                        processId = controllerIdentity.ProcessId,
                        processStartUtcTicks = controllerIdentity.ProcessStartUtcTicks,
                        timestampUtc = DateTimeOffset.UtcNow,
                        authority = coordinator.Authority.ToString(),
                        cpuSetpoint = owned.CpuSetpoint,
                        gpuSetpoint = owned.GpuSetpoint,
                        cpuRpm = owned.CpuRpm,
                        gpuRpm = owned.GpuRpm,
                        applyCalls,
                        constructionRoute =
                            "HpFanControlBackendFactory.Create -> public Hp8C40FanControlBackend",
                        constructionScopeClosedBeforeCustom
                    })
                .ConfigureAwait(false);

            readyPublished = true;

            await WaitForParentContinueAsync(
                    continuePath,
                    cancellationToken)
                .ConfigureAwait(false);
            continueObserved = true;
            previousTimestamp = null;

            for (var index = 1; index <= SupervisionSamples; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePowerStatus(SystemPowerStatusReader.Read());

                var snapshot = telemetry.ReadSnapshot();
                EnsureImmediatePhysicalLimits(snapshot, $"supervision sample {index}");

                var safety = EvaluateEffectiveSafety(
                    hardware,
                    snapshot,
                    previousTimestamp,
                    thermalConfirmation,
                    fanWritePathPresent: true);
                previousTimestamp = snapshot.Timestamp;

                if (!IsBoundedSmokeLoad(snapshot))
                {
                    throw new InvalidOperationException(
                        "M9C supervision left the bounded smoke-load envelope; refusing to hold 30/30.");
                }

                var retained = await coordinator.EnforceSafetyAsync(
                        safety,
                        $"M9C production-path supervision {index}/{SupervisionSamples}",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!retained || coordinator.Authority != FanAuthority.Custom)
                {
                    throw new InvalidOperationException(
                        "M9C supervision returned authority to firmware before the planned normal handoff.");
                }

                var ec = await ReadExpectedEcConfirmedAsync(
                        ecProbe, QualificationLevel, QualificationLevel,
                        $"supervision sample {index}", cancellationToken)
                    .ConfigureAwait(false);
                EnsureExpectedEc(ec, QualificationLevel, QualificationLevel, $"supervision sample {index}");

                supervisionSamples.Add(ToEvidence(index, snapshot, safety, true, index));

                if (index < SupervisionSamples)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            events.Add(new EventEvidence(
                DateTimeOffset.UtcNow,
                "NORMAL_RESTORE_BEGIN",
                "M9C supervision complete; returning authority to firmware."));

            await coordinator.RestoreFirmwareAsync(
                    "M9C normal production-path release",
                    CancellationToken.None)
                .ConfigureAwait(false);

            var final = await ReadExpectedEcConfirmedAsync(
                    ecProbe, byte.MaxValue, byte.MaxValue,
                    "post-restore firmware ownership", CancellationToken.None)
                .ConfigureAwait(false);
            EnsureExpectedEc(final, byte.MaxValue, byte.MaxValue, "post-restore firmware ownership");

            var restoreEvidence = coordinator.LastRestoreEvidence ??
                throw new InvalidOperationException(
                    "M9C backend did not expose restore evidence.");

            if (coordinator.Authority != FanAuthority.Firmware ||
                !restoreEvidence.LocalFirmwareAckVerified ||
                !restoreEvidence.WatchdogLeaseRequired ||
                !restoreEvidence.WatchdogReleaseVerified)
            {
                throw new InvalidOperationException(
                    "M9C normal restore did not prove local FF/FF plus watchdog Release.");
            }

            normalRestoreCompleted = true;
            finalFirmwareOwned = true;
            result = "PASS_CONTROLLER_LOCAL_CLOSURE";
            exitCode = 0;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            failureReason = "OperationCanceledException: operator/lifecycle cancellation";
            exitCode = 130;
            Console.Error.WriteLine($"M9C FAIL_CLOSED: {failureReason}");
        }
        catch (Exception ex)
        {
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"M9C FAIL_CLOSED: {failureReason}");
        }
        finally
        {
            if (coordinator is not null)
            {
                if (coordinator.Authority == FanAuthority.Custom)
                {
                    try
                    {
                        await coordinator.RestoreFirmwareAsync(
                                "M9C finally fallback",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception restoreFailure)
                    {
                        Console.Error.WriteLine(
                            "M9C finally restore failed: " +
                            $"{restoreFailure.GetType().Name}: {restoreFailure.Message}");
                    }
                }

                await coordinator.DisposeAsync().ConfigureAwait(false);
            }

            if (ecProbe is not null)
            {
                try
                {
                    var final = await ReadExpectedEcConfirmedAsync(
                            ecProbe, byte.MaxValue, byte.MaxValue,
                            "final controller-side firmware proof", CancellationToken.None)
                        .ConfigureAwait(false);

                    finalFirmwareOwned =
                        final.CpuSetpoint == byte.MaxValue &&
                        final.GpuSetpoint == byte.MaxValue;
                }
                catch
                {
                    // Parent harness will own the independent final proof.
                }
            }

            try
            {
                await WriteJsonAsync(
                        resultPath,
                        new
                        {
                            schemaVersion = 1,
                            gate = "M9C",
                            result,
                            failureReason,
                            startedUtc,
                            endedUtc = DateTimeOffset.UtcNow,
                            targetProfileId = Hp8C40TargetProfile.Instance.Id,
                            controller = controllerIdentity,
                            physicalExecutionAuthorized = PhysicalExecutionAuthorized,
                            qualificationConstructionAuthorized =
                                Hp8C40ProductionWatchdogGate.M9CPhysicalQualificationConstructionAuthorized,
                            productionConstructionAuthorized =
                                Hp8C40ProductionWatchdogGate.ProductionConstructionAuthorized,
                            watchdogRecoveryValidated =
                                Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated,
                            qualificationLevel = $"{QualificationLevel}/{QualificationLevel}",
                            applyCalls,
                            readyPublished,
                            continueObserved,
                            constructionScopeClosedBeforeCustom,
                            normalRestoreCompleted,
                            finalFirmwareOwned,
                            preWriteSamples,
                            supervisionSamples,
                            events
                        })
                    .ConfigureAwait(false);
            }
            catch (Exception evidenceFailure)
            {
                Console.Error.WriteLine(
                    "M9C evidence write failed: " +
                    $"{evidenceFailure.GetType().Name}: {evidenceFailure.Message}");

                if (exitCode == 0)
                {
                    exitCode = 232;
                }
            }
        }

        return exitCode;
    }

    private static bool IsBoundedSmokeLoad(TelemetrySnapshot snapshot) =>
        snapshot.CpuPackagePowerW.HasValue &&
        snapshot.GpuPowerW.HasValue &&
        snapshot.CpuPackagePowerW.Value <= MaximumCpuPackagePowerW &&
        snapshot.GpuPowerW.Value <= MaximumGpuPowerW;

    private static SafetyGateResult EvaluateEffectiveSafety(
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot,
        DateTimeOffset? previousTimestamp,
        Hp8C40ThermalEmergencyConfirmation confirmation,
        bool fanWritePathPresent)
    {
        var now = DateTimeOffset.UtcNow;
        var age = now - snapshot.Timestamp;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age > MaximumTelemetryAge)
        {
            throw new InvalidOperationException(
                $"M9C stale telemetry: {age.TotalSeconds:0.000}s.");
        }

        if (previousTimestamp.HasValue)
        {
            var gap = snapshot.Timestamp - previousTimestamp.Value;
            if (gap < TimeSpan.Zero || gap > MaximumInterSampleGap)
            {
                throw new InvalidOperationException(
                    $"M9C invalid inter-sample gap: {gap.TotalSeconds:0.000}s.");
            }
        }

        if (!snapshot.IsComplete ||
            !snapshot.CpuCoreTelemetryComplete ||
            !snapshot.GpuTemperatureC.HasValue ||
            snapshot.GpuTemperatureC.Value <= 0)
        {
            throw new InvalidOperationException(
                "M9C requires complete package/core/power/load/GPU/tach telemetry.");
        }

        var raw = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            now,
            fanWritePathPresent);

        return confirmation.Apply(hardware, snapshot, raw);
    }

    private static void EnsureImmediatePhysicalLimits(
        TelemetrySnapshot snapshot,
        string context)
    {
        if (snapshot.CpuControlTemperatureC.HasValue &&
            snapshot.CpuControlTemperatureC.Value >= CpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"M9C {context} CPU physical abort: " +
                $"{snapshot.CpuControlTemperatureC.Value:0.0} C >= {CpuPhysicalAbortC:0} C.");
        }

        if (snapshot.GpuTemperatureC.HasValue &&
            snapshot.GpuTemperatureC.Value >= GpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"M9C {context} GPU physical abort: " +
                $"{snapshot.GpuTemperatureC.Value:0.0} C >= {GpuPhysicalAbortC:0} C.");
        }
    }

    private static void EnsurePowerStatus(SystemPowerStatusSample status)
    {
        if (!status.AcOnline ||
            !status.BatteryPresent ||
            status.BatteryPercent > 100 ||
            status.BatteryPercent < MinimumBatteryPercent)
        {
            throw new InvalidOperationException(
                $"M9C AC/battery sanity gate refused: {status}");
        }
    }

    private static async Task<Hp8C40EcControlState> ReadExpectedEcConfirmedAsync(
        Hp8C40EcControlStateProbe probe,
        int expectedCpu,
        int expectedGpu,
        string context,
        CancellationToken cancellationToken)
    {
        const int maxReads = 3;
        const int requiredConsecutiveUnexpected = 2;

        var previous = probe.ReadControlEvidence();
        if (MatchesExpected(previous, expectedCpu, expectedGpu))
        {
            return previous;
        }

        var consecutiveUnexpected = 1;

        for (var read = 2; read <= maxReads; read++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);

            var next = probe.ReadControlEvidence();
            if (MatchesExpected(next, expectedCpu, expectedGpu))
            {
                return next;
            }

            if (SameUnexpected(previous, next))
            {
                consecutiveUnexpected++;
                if (consecutiveUnexpected >= requiredConsecutiveUnexpected)
                {
                    return next;
                }
            }
            else
            {
                consecutiveUnexpected = 1;
            }

            previous = next;
        }

        return previous;
    }

    private static bool MatchesExpected(
        Hp8C40EcControlState state,
        int expectedCpu,
        int expectedGpu) =>
        state.CpuSetpoint == expectedCpu &&
        state.GpuSetpoint == expectedGpu &&
        state.MaxFan == 0 &&
        state.FanSwitch == 0 &&
        state.CpuRpm <= 10_000 &&
        state.GpuRpm <= 10_000;

    private static bool SameUnexpected(
        Hp8C40EcControlState left,
        Hp8C40EcControlState right) =>
        left.CpuSetpoint == right.CpuSetpoint &&
        left.GpuSetpoint == right.GpuSetpoint &&
        left.MaxFan == right.MaxFan &&
        left.FanSwitch == right.FanSwitch;

    private static void EnsureExpectedEc(
        Hp8C40EcControlState state,
        int expectedCpu,
        int expectedGpu,
        string context)
    {
        if (!MatchesExpected(state, expectedCpu, expectedGpu))
        {
            throw new InvalidOperationException(
                $"M9C EC evidence refused during {context}: " +
                $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}; " +
                $"guards=0x{state.MaxFan:X2}/0x{state.FanSwitch:X2}; " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}; expected={expectedCpu}/{expectedGpu} guards=00/00.");
        }
    }

    private static async Task WaitForParentContinueAsync(
        string continuePath,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < ParentContinueTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(continuePath))
            {
                var value = (await File.ReadAllTextAsync(continuePath, cancellationToken)
                        .ConfigureAwait(false))
                    .Trim();

                if (!string.Equals(value, "M9C-CONTINUE", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("M9C parent continue marker is invalid.");
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "M9C timed out waiting for parent journal/failsafe verification.");
    }

    private static SampleEvidence ToEvidence(
        int index,
        TelemetrySnapshot snapshot,
        SafetyGateResult safety,
        bool lowLoad,
        int consecutiveAccepted) =>
        new(
            index,
            snapshot.Timestamp,
            lowLoad,
            consecutiveAccepted,
            snapshot.CpuControlTemperatureC,
            snapshot.CpuPackagePowerW,
            snapshot.CpuLoadPercent,
            snapshot.GpuTemperatureC,
            snapshot.GpuPowerW,
            snapshot.GpuLoadPercent,
            snapshot.CpuFanRpm,
            snapshot.GpuFanRpm,
            safety.PreconditionsReady,
            safety.CustomControlPermitted,
            safety.ThermalEmergency,
            safety.Reasons.Count == 0 ? "none" : string.Join(" | ", safety.Reasons));

    private static async Task WriteJsonAsync(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("M9C evidence path has no parent directory."));

        var json = JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

        await File.WriteAllTextAsync(fullPath, json, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static (int ProcessId, long ProcessStartUtcTicks) GetCurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return (process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? FindKnownConflictingControllerProcess()
    {
        foreach (var name in new[] { "OmenMon", "OmenMon-Reborn", "VictusFanControl.App" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return name;
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

    private sealed record SampleEvidence(
        int Index,
        DateTimeOffset TimestampUtc,
        bool LowLoad,
        int ConsecutiveAccepted,
        double? CpuEffectiveC,
        double? CpuPackagePowerW,
        double? CpuLoadPercent,
        double? GpuTemperatureC,
        double? GpuPowerW,
        double? GpuLoadPercent,
        double? CpuFanRpm,
        double? GpuFanRpm,
        bool PreconditionsReady,
        bool CustomControlPermitted,
        bool ThermalEmergency,
        string SafetyReasons);

    private sealed record EventEvidence(
        DateTimeOffset TimestampUtc,
        string Event,
        string Detail);
}
