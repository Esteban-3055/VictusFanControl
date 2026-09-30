using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// M8A read-only representative-load qualification for the exact HP 8C40 target.
/// The operator supplies the real gaming/3D workload. This path never creates a
/// fan-control backend, coordinator or watchdog lease and never issues a fan write
/// or firmware restore.
/// </summary>
public static class Hp8C40M8RepresentativeLoadQualificationTest
{
    public const int QualificationSamples = 60;
    public const int SampleIntervalMilliseconds = 1000;
    public const int MinimumRepresentativeSamples = 45;
    public const int MinimumConsecutiveRepresentativeSamples = 10;
    public const double MinimumGpuLoadPercent = 35.0;
    public const double MinimumGpuPowerW = 20.0;
    public const double MinimumCpuLoadPercent = 5.0;
    public const double MinimumCpuPackagePowerW = 15.0;
    public const double CpuPhysicalAbortC = 90.0;
    public const double GpuPhysicalAbortC = 82.0;
    public const int EcEvidenceIntervalSamples = 5;
    public const int MaximumUnexpectedEcConfirmationReads = 3;
    public const int RequiredConsecutiveUnexpectedEcSamples = 2;
    public const int UnexpectedEcConfirmationDelayMilliseconds = 25;

    private const byte MinimumBatteryPercent = 20;
    private static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumInterSampleGap = TimeSpan.FromSeconds(3);

    public static int RunClassifierSelfTest(TextWriter output)
    {
        var failures = new List<string>();

        Check(IsRepresentativeLoadValues(35.0, 20.0, 5.0, 1.0),
            "threshold sample with CPU load path should qualify");
        Check(IsRepresentativeLoadValues(35.0, 20.0, 0.0, 15.0),
            "threshold sample with CPU power path should qualify");
        Check(!IsRepresentativeLoadValues(34.9, 20.0, 20.0, 30.0),
            "GPU load below threshold must fail");
        Check(!IsRepresentativeLoadValues(90.0, 19.9, 20.0, 30.0),
            "GPU power below threshold must fail");
        Check(!IsRepresentativeLoadValues(90.0, 60.0, 4.9, 14.9),
            "CPU activity below both thresholds must fail");

        var passWindow = Enumerable.Repeat(true, MinimumRepresentativeSamples)
            .Concat(Enumerable.Repeat(false, QualificationSamples - MinimumRepresentativeSamples))
            .ToArray();
        Check(EvaluateRepresentativeWindow(passWindow).Passed,
            "45/60 with a sustained streak must pass");

        var countFailWindow = Enumerable.Repeat(true, MinimumRepresentativeSamples - 1)
            .Concat(Enumerable.Repeat(false, QualificationSamples - MinimumRepresentativeSamples + 1))
            .ToArray();
        Check(!EvaluateRepresentativeWindow(countFailWindow).Passed,
            "44/60 must fail");

        var streakFailWindow = new List<bool>(QualificationSamples);
        for (var block = 0; block < 5; block++)
        {
            streakFailWindow.AddRange(
                Enumerable.Repeat(true, MinimumConsecutiveRepresentativeSamples - 1));
            streakFailWindow.Add(false);
        }

        while (streakFailWindow.Count < QualificationSamples)
        {
            streakFailWindow.Add(false);
        }

        var streakEvaluation = EvaluateRepresentativeWindow(streakFailWindow);
        Check(
            streakEvaluation.RepresentativeSamples == MinimumRepresentativeSamples &&
            streakEvaluation.MaximumConsecutiveRepresentative ==
                MinimumConsecutiveRepresentativeSamples - 1 &&
            !streakEvaluation.Passed,
            "45/60 fragmented into <=9-sample streaks must fail");

        if (failures.Count == 0)
        {
            output.WriteLine(
                "HP 8C40 M8A representative-load classifier self-test: PASS");
            return 0;
        }

        foreach (var failure in failures)
        {
            output.WriteLine($"FAIL: {failure}");
        }

        return 158;

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }
    }

    public static async Task<int> RunAsync(
        string modulesDirectory,
        string resultPath,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var result = "FAIL_CLOSED";
        string? failureReason = null;
        var exitCode = 159;

        var samples = new List<SampleEvidence>(QualificationSamples);
        var ecChecks = new List<EcEvidence>();
        var representativeFlags = new List<bool>(QualificationSamples);

        try
        {
            if (!IsAdministrator())
            {
                throw new InvalidOperationException(
                    "M8A requires an elevated Administrator process.");
            }

            var hardware = HardwareIdentityReader.ReadCurrent();

            if (!Hp8C40TargetProfile.Matches(hardware, out var targetReason))
            {
                throw new InvalidOperationException(
                    $"M8A exact-target refusal: {targetReason}");
            }

            var conflict = FindKnownConflictingControllerProcess();
            if (conflict is not null)
            {
                throw new InvalidOperationException(
                    $"M8A refused while '{conflict}' is running.");
            }

            EnsurePowerStatus(SystemPowerStatusReader.Read());

            var ecProbe = new Hp8C40EcControlStateProbe(modulesDirectory);
            var initialEc = await ReadFirmwareEvidenceConfirmedAsync(
                    ecProbe,
                    "initial",
                    cancellationToken)
                .ConfigureAwait(false);

            ecChecks.Add(ToEcEvidence("initial", 0, initialEc));
            EnsureFirmwareEvidenceHealthy(initialEc.State, "initial");

            using var telemetry = new HardwareTelemetryReader(modulesDirectory);

            if (!telemetry.BackendsInitialized ||
                !string.Equals(
                    telemetry.TargetProfile?.Id,
                    Hp8C40TargetProfile.Instance.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "M8A exact-target telemetry backends are not fully initialized.");
            }

            var warmup = telemetry.ReadSnapshot();
            _ = EvaluateAndValidateSnapshot(
                hardware,
                warmup,
                previousTimestamp: null);

            telemetry.ResetHealthWindow();

            await Task.Delay(
                    TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset? previousTimestamp = null;

            for (var sampleIndex = 1;
                 sampleIndex <= QualificationSamples;
                 sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var power = SystemPowerStatusReader.Read();
                EnsurePowerStatus(power);

                var snapshot = telemetry.ReadSnapshot();
                var safety = EvaluateAndValidateSnapshot(
                    hardware,
                    snapshot,
                    previousTimestamp);

                previousTimestamp = snapshot.Timestamp;

                var representative = IsRepresentativeLoad(snapshot);
                representativeFlags.Add(representative);

                var evidence = new SampleEvidence(
                    Index: sampleIndex,
                    TimestampUtc: snapshot.Timestamp,
                    Representative: representative,
                    CpuEffectiveC: snapshot.CpuControlTemperatureC!.Value,
                    CpuPackageC: snapshot.CpuTemperatureC!.Value,
                    CpuCoreMaxC: snapshot.CpuCoreMaxTemperatureC!.Value,
                    CpuPowerW: snapshot.CpuPackagePowerW!.Value,
                    CpuLoadPercent: snapshot.CpuLoadPercent!.Value,
                    GpuTemperatureC: snapshot.GpuTemperatureC!.Value,
                    GpuPowerW: snapshot.GpuPowerW!.Value,
                    GpuLoadPercent: snapshot.GpuLoadPercent!.Value,
                    CpuFanRpm: snapshot.CpuFanRpm!.Value,
                    GpuFanRpm: snapshot.GpuFanRpm!.Value,
                    SafetyPreconditionsReady: safety.PreconditionsReady,
                    GpuIdentityValid: safety.TelemetryDeviceIdentityValid,
                    ThermalEmergency: safety.ThermalEmergency,
                    AcOnline: power.AcOnline,
                    BatteryPercent: power.BatteryPercent);

                samples.Add(evidence);

                Console.WriteLine(
                    $"M8A_SAMPLE {sampleIndex}/{QualificationSamples} " +
                    $"representative={representative} " +
                    $"CPU={evidence.CpuEffectiveC:0.0}C " +
                    $"{evidence.CpuPowerW:0.0}W " +
                    $"{evidence.CpuLoadPercent:0.0}% " +
                    $"GPU={evidence.GpuTemperatureC:0.0}C " +
                    $"{evidence.GpuPowerW:0.0}W " +
                    $"{evidence.GpuLoadPercent:0.0}% " +
                    $"FAN={evidence.CpuFanRpm:0}/{evidence.GpuFanRpm:0}rpm");

                if (sampleIndex % EcEvidenceIntervalSamples == 0)
                {
                    var ec = await ReadFirmwareEvidenceConfirmedAsync(
                            ecProbe,
                            $"sample-{sampleIndex}",
                            cancellationToken)
                        .ConfigureAwait(false);

                    ecChecks.Add(ToEcEvidence($"sample-{sampleIndex}", sampleIndex, ec));
                    EnsureFirmwareEvidenceHealthy(ec.State, $"sample {sampleIndex}");
                }

                if (sampleIndex < QualificationSamples)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(SampleIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var finalEc = await ReadFirmwareEvidenceConfirmedAsync(
                    ecProbe,
                    "final",
                    cancellationToken)
                .ConfigureAwait(false);

            ecChecks.Add(ToEcEvidence("final", QualificationSamples, finalEc));
            EnsureFirmwareEvidenceHealthy(finalEc.State, "final");

            var window = EvaluateRepresentativeWindow(representativeFlags);

            Console.WriteLine();
            Console.WriteLine(
                $"M8A_WINDOW representative={window.RepresentativeSamples}/{QualificationSamples} " +
                $"maxConsecutive={window.MaximumConsecutiveRepresentative} " +
                $"required={MinimumRepresentativeSamples}/{QualificationSamples} and >= " +
                $"{MinimumConsecutiveRepresentativeSamples} consecutive.");

            if (!window.Passed)
            {
                throw new InvalidOperationException(
                    "M8A representative-load window was not established: " +
                    $"{window.RepresentativeSamples}/{QualificationSamples} qualifying samples, " +
                    $"maximum consecutive streak {window.MaximumConsecutiveRepresentative}.");
            }

            result = "PASS";
            exitCode = 0;

            Console.WriteLine();
            Console.WriteLine(
                "PASS: HP 8C40 M8A representative-load admission completed NO-WRITE.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            failureReason = "M8A cancelled by operator.";
            exitCode = 130;
            Console.Error.WriteLine(failureReason);
        }
        catch (Exception ex)
        {
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
            exitCode = 159;
            Console.Error.WriteLine(
                $"M8A FAIL_CLOSED / NO-WRITE: {failureReason}");
        }

        var endedUtc = DateTimeOffset.UtcNow;
        var windowSummary = EvaluateRepresentativeWindow(representativeFlags);

        var evidenceObject = new
        {
            schemaVersion = 1,
            gate = "M8A",
            result,
            failureReason,
            startedUtc,
            endedUtc,
            targetProfileId = Hp8C40TargetProfile.Instance.Id,
            writeCapable = false,
            fanWriteAttempted = false,
            firmwareRestoreAttempted = false,
            watchdogLeaseAttempted = false,
            criteria = new
            {
                qualificationSamples = QualificationSamples,
                sampleIntervalMilliseconds = SampleIntervalMilliseconds,
                minimumRepresentativeSamples = MinimumRepresentativeSamples,
                minimumConsecutiveRepresentativeSamples =
                    MinimumConsecutiveRepresentativeSamples,
                minimumGpuLoadPercent = MinimumGpuLoadPercent,
                minimumGpuPowerW = MinimumGpuPowerW,
                minimumCpuLoadPercent = MinimumCpuLoadPercent,
                minimumCpuPackagePowerW = MinimumCpuPackagePowerW,
                cpuActivityRule =
                    "cpuLoad >= minimumCpuLoadPercent OR cpuPackagePower >= minimumCpuPackagePowerW",
                gpuActivityRule =
                    "gpuLoad >= minimumGpuLoadPercent AND gpuPower >= minimumGpuPowerW",
                cpuPhysicalAbortC = CpuPhysicalAbortC,
                gpuPhysicalAbortC = GpuPhysicalAbortC,
                maximumTelemetryAgeSeconds = MaximumTelemetryAge.TotalSeconds,
                maximumInterSampleGapSeconds = MaximumInterSampleGap.TotalSeconds,
                ecEvidenceIntervalSamples = EcEvidenceIntervalSamples
            },
            window = new
            {
                representativeSamples = windowSummary.RepresentativeSamples,
                maximumConsecutiveRepresentative =
                    windowSummary.MaximumConsecutiveRepresentative,
                passed = windowSummary.Passed
            },
            acceptedSamples = samples.Count,
            samples,
            ecChecks
        };

        try
        {
            var fullResultPath = Path.GetFullPath(resultPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(fullResultPath) ??
                throw new InvalidOperationException(
                    "M8A result path has no parent directory."));

            var json = JsonSerializer.Serialize(
                evidenceObject,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

            await File.WriteAllTextAsync(
                    fullResultPath,
                    json,
                    CancellationToken.None)
                .ConfigureAwait(false);

            Console.WriteLine($"M8A evidence: {fullResultPath}");
        }
        catch (Exception evidenceFailure)
        {
            Console.Error.WriteLine(
                $"M8A evidence write failed: " +
                $"{evidenceFailure.GetType().Name}: {evidenceFailure.Message}");

            if (exitCode == 0)
            {
                exitCode = 160;
            }
        }

        return exitCode;
    }

    internal static bool IsRepresentativeLoadValues(
        double gpuLoadPercent,
        double gpuPowerW,
        double cpuLoadPercent,
        double cpuPackagePowerW) =>
        gpuLoadPercent >= MinimumGpuLoadPercent &&
        gpuPowerW >= MinimumGpuPowerW &&
        (cpuLoadPercent >= MinimumCpuLoadPercent ||
         cpuPackagePowerW >= MinimumCpuPackagePowerW);

    internal static WindowEvaluation EvaluateRepresentativeWindow(
        IReadOnlyList<bool> representativeSamples)
    {
        var representativeCount = 0;
        var currentConsecutive = 0;
        var maximumConsecutive = 0;

        foreach (var representative in representativeSamples)
        {
            if (representative)
            {
                representativeCount++;
                currentConsecutive++;
                maximumConsecutive = Math.Max(
                    maximumConsecutive,
                    currentConsecutive);
            }
            else
            {
                currentConsecutive = 0;
            }
        }

        var passed =
            representativeSamples.Count == QualificationSamples &&
            representativeCount >= MinimumRepresentativeSamples &&
            maximumConsecutive >= MinimumConsecutiveRepresentativeSamples;

        return new WindowEvaluation(
            representativeCount,
            maximumConsecutive,
            passed);
    }

    private static bool IsRepresentativeLoad(TelemetrySnapshot snapshot) =>
        IsRepresentativeLoadValues(
            snapshot.GpuLoadPercent!.Value,
            snapshot.GpuPowerW!.Value,
            snapshot.CpuLoadPercent!.Value,
            snapshot.CpuPackagePowerW!.Value);

    private static SafetyGateResult EvaluateAndValidateSnapshot(
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot,
        DateTimeOffset? previousTimestamp)
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
                $"M8A telemetry read exceeded freshness limit: " +
                $"age={age.TotalSeconds:0.000}s.");
        }

        if (previousTimestamp.HasValue)
        {
            var gap = snapshot.Timestamp - previousTimestamp.Value;

            if (gap < TimeSpan.Zero ||
                gap > MaximumInterSampleGap)
            {
                throw new InvalidOperationException(
                    "M8A detected an invalid/lifecycle-like " +
                    $"inter-sample gap: {gap.TotalSeconds:0.000}s.");
            }
        }

        if (!snapshot.IsComplete ||
            !snapshot.CpuCoreTelemetryComplete)
        {
            throw new InvalidOperationException(
                "M8A requires complete package/core/power/load/GPU/tach telemetry for every sample.");
        }

        if (!snapshot.GpuTemperatureC.HasValue ||
            snapshot.GpuTemperatureC.Value <= 0)
        {
            throw new InvalidOperationException(
                "M8A rejects missing/zero GPU temperature telemetry.");
        }

        var effectiveCpu = snapshot.CpuControlTemperatureC;

        if (!effectiveCpu.HasValue)
        {
            throw new InvalidOperationException(
                "M8A effective CPU safety aggregate is unavailable.");
        }

        if (effectiveCpu.Value >= CpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"M8A physical CPU abort: {effectiveCpu.Value:0.0} C >= " +
                $"{CpuPhysicalAbortC:0} C.");
        }

        if (snapshot.GpuTemperatureC.Value >= GpuPhysicalAbortC)
        {
            throw new InvalidOperationException(
                $"M8A physical GPU abort: {snapshot.GpuTemperatureC.Value:0.0} C >= " +
                $"{GpuPhysicalAbortC:0} C.");
        }

        var safety = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            now,
            fanWritePathPresent: false);

        if (!safety.PreconditionsReady ||
            !safety.TelemetryDeviceIdentityValid ||
            safety.ThermalEmergency)
        {
            throw new InvalidOperationException(
                "M8A production SafetyGate refused read-only " +
                "representative-load telemetry: " +
                string.Join(" | ", safety.Reasons));
        }

        return safety;
    }

    private static void EnsurePowerStatus(SystemPowerStatusSample status)
    {
        if (!status.AcOnline)
        {
            throw new InvalidOperationException(
                $"M8A requires AC online; observed {status}.");
        }

        if (!status.BatteryPresent ||
            status.BatteryPercent > 100 ||
            status.BatteryPercent < MinimumBatteryPercent)
        {
            throw new InvalidOperationException(
                $"M8A battery sanity refused: {status}.");
        }
    }

    private static async Task<ConfirmedEcEvidence>
        ReadFirmwareEvidenceConfirmedAsync(
            Hp8C40EcControlStateProbe probe,
            string context,
            CancellationToken cancellationToken)
    {
        var initial = probe.ReadControlEvidence();

        if (FirmwareEvidenceHealthy(initial))
        {
            return new ConfirmedEcEvidence(
                initial,
                RecoveredTransient: false,
                Reads: 1);
        }

        var previous = initial;
        var consecutiveUnexpected = 1;

        for (var read = 2;
             read <= MaximumUnexpectedEcConfirmationReads;
             read++)
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        UnexpectedEcConfirmationDelayMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);

            var next = probe.ReadControlEvidence();

            if (FirmwareEvidenceHealthy(next))
            {
                Console.WriteLine(
                    $"M8A EC transient during {context} " +
                    $"recovered to FF/FF + guards 00/00 on " +
                    $"read {read}/{MaximumUnexpectedEcConfirmationReads}.");

                return new ConfirmedEcEvidence(
                    next,
                    RecoveredTransient: true,
                    Reads: read);
            }

            if (SameUnexpectedOwnershipAndGuards(previous, next))
            {
                consecutiveUnexpected++;

                if (consecutiveUnexpected >=
                    RequiredConsecutiveUnexpectedEcSamples)
                {
                    return new ConfirmedEcEvidence(
                        next,
                        RecoveredTransient: false,
                        Reads: read);
                }
            }
            else
            {
                consecutiveUnexpected = 1;
            }

            previous = next;
        }

        return new ConfirmedEcEvidence(
            previous,
            RecoveredTransient: false,
            Reads: MaximumUnexpectedEcConfirmationReads);
    }

    private static bool FirmwareEvidenceHealthy(Hp8C40EcControlState state) =>
        state.CpuSetpoint == byte.MaxValue &&
        state.GpuSetpoint == byte.MaxValue &&
        state.MaxFan == 0x00 &&
        state.FanSwitch == 0x00 &&
        state.CpuRpm <= 10_000 &&
        state.GpuRpm <= 10_000;

    private static bool SameUnexpectedOwnershipAndGuards(
        Hp8C40EcControlState left,
        Hp8C40EcControlState right) =>
        left.CpuSetpoint == right.CpuSetpoint &&
        left.GpuSetpoint == right.GpuSetpoint &&
        left.MaxFan == right.MaxFan &&
        left.FanSwitch == right.FanSwitch;

    private static void EnsureFirmwareEvidenceHealthy(
        Hp8C40EcControlState state,
        string context)
    {
        if (!FirmwareEvidenceHealthy(state))
        {
            throw new InvalidOperationException(
                $"M8A firmware/guard evidence refused during {context}: " +
                $"setpoint={state.CpuSetpoint}/{state.GpuSetpoint}, " +
                $"MaxFan=0x{state.MaxFan:X2}, FanSwitch=0x{state.FanSwitch:X2}, " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}.");
        }
    }

    private static EcEvidence ToEcEvidence(
        string context,
        int sampleIndex,
        ConfirmedEcEvidence confirmed) =>
        new(
            Context: context,
            SampleIndex: sampleIndex,
            TimestampUtc: DateTimeOffset.UtcNow,
            CpuSetpoint: confirmed.State.CpuSetpoint,
            GpuSetpoint: confirmed.State.GpuSetpoint,
            MaxFan: confirmed.State.MaxFan,
            FanSwitch: confirmed.State.FanSwitch,
            CpuRpm: confirmed.State.CpuRpm,
            GpuRpm: confirmed.State.GpuRpm,
            RecoveredTransient: confirmed.RecoveredTransient,
            Reads: confirmed.Reads);

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        return principal.IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static string? FindKnownConflictingControllerProcess()
    {
        foreach (var name in new[]
                 {
                     "OmenMon",
                     "OmenMon-Reborn",
                     "VictusFanControl.App"
                 })
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

    internal sealed record WindowEvaluation(
        int RepresentativeSamples,
        int MaximumConsecutiveRepresentative,
        bool Passed);

    private sealed record ConfirmedEcEvidence(
        Hp8C40EcControlState State,
        bool RecoveredTransient,
        int Reads);

    private sealed record SampleEvidence(
        int Index,
        DateTimeOffset TimestampUtc,
        bool Representative,
        double CpuEffectiveC,
        double CpuPackageC,
        double CpuCoreMaxC,
        double CpuPowerW,
        double CpuLoadPercent,
        double GpuTemperatureC,
        double GpuPowerW,
        double GpuLoadPercent,
        double CpuFanRpm,
        double GpuFanRpm,
        bool SafetyPreconditionsReady,
        bool GpuIdentityValid,
        bool ThermalEmergency,
        bool AcOnline,
        byte BatteryPercent);

    private sealed record EcEvidence(
        string Context,
        int SampleIndex,
        DateTimeOffset TimestampUtc,
        byte CpuSetpoint,
        byte GpuSetpoint,
        byte MaxFan,
        byte FanSwitch,
        ushort CpuRpm,
        ushort GpuRpm,
        bool RecoveredTransient,
        int Reads);
}
