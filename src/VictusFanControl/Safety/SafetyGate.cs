using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Safety;

public sealed record SafetyGateResult(
    bool BoardAllowed,
    bool RuntimeHealthy,
    bool SnapshotComplete,
    bool SnapshotFresh,
    bool SensorsPlausible,
    bool ThermalEmergency,
    bool PreconditionsReady,
    bool FanWritePathPresent,
    bool CustomControlPermitted,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Read-only pre-control safety gate. This class does not write fan state.
/// It centralizes the conditions that a future write-capable controller must
/// satisfy before it can request custom authority.
/// </summary>
public static class SafetyGate
{
    public const string InitialValidatedBoardProduct = "88F8";
    public static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(3);

    // Conservative pre-control handoff thresholds. These are deliberately not
    // the final fan-curve targets. Crossing them must keep/return HP authority.
    public const double CpuEmergencyC = 95.0;
    public const double GpuEmergencyC = 87.0;

    public static SafetyGateResult Evaluate(
        HardwareIdentity hardware,
        SystemState state,
        TelemetrySnapshot? snapshot,
        DateTimeOffset now)
    {
        var reasons = new List<string>();

        var boardAllowed = string.Equals(
            hardware.BoardProduct,
            InitialValidatedBoardProduct,
            StringComparison.OrdinalIgnoreCase);

        if (!boardAllowed)
        {
            reasons.Add($"Board '{hardware.BoardProduct}' is not on the control allowlist.");
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

        var sensorsPlausible = snapshotComplete && AreSensorsPlausible(snapshot!);
        if (snapshotComplete && !sensorsPlausible)
        {
            reasons.Add("One or more telemetry values are outside the pre-control plausibility envelope.");
        }

        var thermalEmergency = snapshotComplete &&
            (snapshot!.CpuTemperatureC!.Value >= CpuEmergencyC ||
             snapshot.GpuTemperatureC!.Value >= GpuEmergencyC);

        if (thermalEmergency)
        {
            reasons.Add(
                $"Thermal handoff threshold reached (CPU >= {CpuEmergencyC:0} C or GPU >= {GpuEmergencyC:0} C).");
        }

        var preconditionsReady =
            boardAllowed &&
            runtimeHealthy &&
            snapshotComplete &&
            snapshotFresh &&
            sensorsPlausible &&
            !thermalEmergency;

        // Intentional hard block until the separately reviewed write/restore
        // backend and watchdog milestone exists.
        const bool fanWritePathPresent = false;
        const bool customControlPermitted = false;

        if (!fanWritePathPresent)
        {
            reasons.Add("Fan write/restore backend is intentionally absent in this build.");
        }

        return new SafetyGateResult(
            BoardAllowed: boardAllowed,
            RuntimeHealthy: runtimeHealthy,
            SnapshotComplete: snapshotComplete,
            SnapshotFresh: snapshotFresh,
            SensorsPlausible: sensorsPlausible,
            ThermalEmergency: thermalEmergency,
            PreconditionsReady: preconditionsReady,
            FanWritePathPresent: fanWritePathPresent,
            CustomControlPermitted: customControlPermitted,
            Reasons: reasons);
    }

    private static bool AreSensorsPlausible(TelemetrySnapshot snapshot)
    {
        return InRange(snapshot.CpuTemperatureC, 0, 110) &&
               InRange(snapshot.CpuPackagePowerW, 0, 500) &&
               InRange(snapshot.CpuLoadPercent, 0, 100) &&
               InRange(snapshot.GpuTemperatureC, 0, 105) &&
               InRange(snapshot.GpuPowerW, 0, 300) &&
               InRange(snapshot.GpuLoadPercent, 0, 100) &&
               InRange(snapshot.CpuFanRpm, 0, 10_000) &&
               InRange(snapshot.GpuFanRpm, 0, 10_000);
    }

    private static bool InRange(double? value, double minimum, double maximum) =>
        value.HasValue &&
        double.IsFinite(value.Value) &&
        value.Value >= minimum &&
        value.Value <= maximum;
}
