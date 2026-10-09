using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Safety;

public sealed record SafetyGateResult(
    bool BoardAllowed,
    bool RuntimeHealthy,
    bool SnapshotComplete,
    bool SnapshotFresh,
    bool TelemetryDeviceIdentityValid,
    bool SensorsPlausible,
    bool ThermalEmergency,
    bool PreconditionsReady,
    bool FanWritePathPresent,
    bool CustomControlPermitted,
    DateTimeOffset? SnapshotTimestamp,
    DateTimeOffset EvaluatedAt,
    long EvaluationSequence,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Read-only pre-control safety gate. This class does not write fan state.
/// It centralizes the conditions a write-capable controller must satisfy before
/// it can request custom authority.
/// </summary>
public static class SafetyGate
{
    private static long _evaluationSequence;

    // Kept for compatibility with old diagnostics/tests that reference the
    // first physically validated board explicitly.
    public const string InitialValidatedBoardProduct = Hp88F8TargetProfile.BoardProduct;

    public static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(3);

    // Conservative pre-control handoff thresholds. These are not the final fan
    // curve targets. CPU safety uses the hotter of package and hottest-core
    // telemetry.
    public const double CpuEmergencyC = 95.0;
    public const double GpuEmergencyC = 87.0;

    public static SafetyGateResult Evaluate(
        HardwareIdentity hardware,
        SystemState state,
        TelemetrySnapshot? snapshot,
        DateTimeOffset now,
        bool fanWritePathPresent = false) =>
        EvaluateCore(
            hardware,
            state,
            snapshot,
            now,
            fanWritePathPresent,
            Interlocked.Increment(ref _evaluationSequence));

    /// <summary>
    /// Presentation-only evaluation. It deliberately does not consume a
    /// control-order sequence number, so UI refreshes can never supersede a
    /// SafetyGate result that is actually being enforced by the coordinator.
    /// </summary>
    public static SafetyGateResult EvaluateForDisplay(
        HardwareIdentity hardware,
        SystemState state,
        TelemetrySnapshot? snapshot,
        DateTimeOffset now,
        bool fanWritePathPresent = false) =>
        EvaluateCore(
            hardware,
            state,
            snapshot,
            now,
            fanWritePathPresent,
            evaluationSequence: 0);

    private static SafetyGateResult EvaluateCore(
        HardwareIdentity hardware,
        SystemState state,
        TelemetrySnapshot? snapshot,
        DateTimeOffset now,
        bool fanWritePathPresent,
        long evaluationSequence)
    {
        var reasons = new List<string>();

        var targetProfile =
            HpHardwareTargetResolver.Resolve(hardware, out var hardwareReason);
        var boardAllowed = targetProfile is not null;

        if (!boardAllowed)
        {
            reasons.Add($"Target hardware fingerprint mismatch: {hardwareReason}");
        }

        var runtimeHealthy = state == SystemState.Healthy;
        if (!runtimeHealthy)
        {
            reasons.Add($"Runtime state is {state}, not Healthy.");
        }

        var snapshotComplete = snapshot?.IsComplete == true;
        if (!snapshotComplete)
        {
            reasons.Add("Telemetry snapshot is incomplete or unavailable.");
        }

        var age = snapshot is null ? TimeSpan.MaxValue : now - snapshot.Timestamp;
        var snapshotFresh = snapshot is not null &&
                            age >= TimeSpan.Zero &&
                            age <= MaximumTelemetryAge;
        if (!snapshotFresh)
        {
            reasons.Add(snapshot is null
                ? "No telemetry snapshot has been received."
                : $"Telemetry is stale ({Math.Max(0, age.TotalSeconds):0.0} s old).");
        }

        if (snapshot is not null && !snapshot.IsFanTelemetryFreshAt(now))
        {
            snapshotFresh = false;
            reasons.Add("HP WMI fan acquisition expired or lacks freshness metadata.");
        }

        var telemetryDeviceIdentityValid =
            snapshot is not null &&
            targetProfile is not null &&
            targetProfile.MatchesExpectedGpu(snapshot.GpuName);

        if (snapshotComplete &&
            targetProfile is not null &&
            !telemetryDeviceIdentityValid)
        {
            reasons.Add(
                $"GPU identity '{snapshot!.GpuName ?? "unknown"}' does not match validated target " +
                $"'{targetProfile.ExpectedGpuName}'.");
        }

        var coreTelemetryComplete =
            snapshot is not null &&
            snapshot.CpuCoreTelemetryComplete;

        if (snapshotComplete && !coreTelemetryComplete)
        {
            var expected = snapshot?.CpuExpectedPhysicalCoreCount?.ToString() ?? "unknown";
            var actual = snapshot?.CpuCoreTemperatures.Count ?? 0;
            reasons.Add(
                $"Per-core CPU temperature telemetry is incomplete ({actual}/{expected} physical cores).");
        }

        var sensorsPlausible =
            snapshotComplete &&
            coreTelemetryComplete &&
            AreSensorsPlausible(snapshot!);

        if (snapshotComplete &&
            coreTelemetryComplete &&
            !sensorsPlausible)
        {
            reasons.Add(
                "One or more telemetry values are outside the pre-control plausibility envelope.");
        }

        var effectiveCpuTemperature = snapshot?.CpuControlTemperatureC;
        var thermalEmergency = snapshotComplete &&
            effectiveCpuTemperature.HasValue &&
            (effectiveCpuTemperature.Value >= CpuEmergencyC ||
             snapshot!.GpuTemperatureC!.Value >= GpuEmergencyC);

        if (thermalEmergency)
        {
            reasons.Add(
                $"Thermal handoff threshold reached (effective CPU >= {CpuEmergencyC:0} C or GPU >= {GpuEmergencyC:0} C). " +
                $"Effective CPU={effectiveCpuTemperature:0.0} C, package={snapshot!.CpuTemperatureC:0.0} C, " +
                $"hottest-core={snapshot.CpuCoreMaxTemperatureC:0.0} C.");
        }

        var preconditionsReady =
            boardAllowed &&
            runtimeHealthy &&
            snapshotComplete &&
            snapshotFresh &&
            telemetryDeviceIdentityValid &&
            coreTelemetryComplete &&
            sensorsPlausible &&
            !thermalEmergency;

        var customControlPermitted = preconditionsReady && fanWritePathPresent;

        if (!fanWritePathPresent)
        {
            reasons.Add("Fan write/restore backend is intentionally absent in this build.");
        }

        return new SafetyGateResult(
            BoardAllowed: boardAllowed,
            RuntimeHealthy: runtimeHealthy,
            SnapshotComplete: snapshotComplete,
            SnapshotFresh: snapshotFresh,
            TelemetryDeviceIdentityValid: telemetryDeviceIdentityValid,
            SensorsPlausible: sensorsPlausible,
            ThermalEmergency: thermalEmergency,
            PreconditionsReady: preconditionsReady,
            FanWritePathPresent: fanWritePathPresent,
            CustomControlPermitted: customControlPermitted,
            SnapshotTimestamp: snapshot?.Timestamp,
            EvaluatedAt: now,
            EvaluationSequence: evaluationSequence,
            Reasons: reasons);
    }

    private static bool AreSensorsPlausible(TelemetrySnapshot snapshot)
    {
        if (!InRange(snapshot.CpuTemperatureC, 10, 110) ||
            !InRange(snapshot.CpuPackagePowerW, 0, 500) ||
            !InRange(snapshot.CpuLoadPercent, 0, 100) ||
            !InRange(snapshot.GpuTemperatureC, 10, 105) ||
            !InRange(snapshot.GpuPowerW, 0, 300) ||
            !InRange(snapshot.GpuLoadPercent, 0, 100) ||
            !InRange(snapshot.CpuFanRpm, 0, 10_000) ||
            !InRange(snapshot.GpuFanRpm, 0, 10_000))
        {
            return false;
        }

        if (!snapshot.CpuCoreTelemetryComplete)
        {
            return false;
        }

        var coreIndices = new HashSet<int>();
        foreach (var core in snapshot.CpuCoreTemperatures)
        {
            if (core.CoreIndex < 0 ||
                core.LogicalProcessorIndex < 0 ||
                !double.IsFinite(core.TemperatureC) ||
                core.TemperatureC is < 0 or > 125 ||
                !coreIndices.Add(core.CoreIndex))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InRange(double? value, double minimum, double maximum) =>
        value.HasValue &&
        double.IsFinite(value.Value) &&
        value.Value >= minimum &&
        value.Value <= maximum;
}
