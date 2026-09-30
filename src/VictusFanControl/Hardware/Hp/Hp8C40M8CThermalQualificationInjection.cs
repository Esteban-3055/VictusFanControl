using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Internal M8C-only synthetic thermal evidence generator.
///
/// This type never reads or writes hardware. It exists only to exercise the
/// production SafetyGate + exact-target HP 8C40 temporal confirmation path
/// without deliberately heating real silicon. Production runtime/GUI code must
/// never reference this type.
/// </summary>
internal static class Hp8C40M8CThermalQualificationInjection
{
    public const string EvidenceMarker =
        "M8C_SYNTHETIC_QUALIFICATION_ONLY";

    public static Hp8C40M8CSyntheticFrame CpuConfirmedThreshold(
        DateTimeOffset timestamp,
        int ordinal) =>
        Create(
            timestamp,
            ordinal,
            "CPU_CONFIRMED_95C",
            effectiveCpuC: SafetyGate.CpuEmergencyC,
            gpuC: 70.0);

    public static Hp8C40M8CSyntheticFrame GpuImmediateThreshold(
        DateTimeOffset timestamp) =>
        Create(
            timestamp,
            ordinal: 1,
            "GPU_IMMEDIATE_87C",
            effectiveCpuC: 80.0,
            gpuC: SafetyGate.GpuEmergencyC);

    public static Hp8C40M8CSyntheticFrame CpuHardImmediateThreshold(
        DateTimeOffset timestamp) =>
        Create(
            timestamp,
            ordinal: 1,
            "CPU_HARD_IMMEDIATE_99C",
            effectiveCpuC:
                Hp8C40ThermalEmergencyConfirmation.CpuHardEmergencyC,
            gpuC: 70.0);

    public static Hp8C40M8CSyntheticEvaluation Evaluate(
        HardwareIdentity hardware,
        Hp8C40M8CSyntheticFrame frame,
        Hp8C40ThermalEmergencyConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(confirmation);

        var raw = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            frame.Snapshot,
            frame.Snapshot.Timestamp,
            fanWritePathPresent: true);

        var effective = confirmation.Apply(
            hardware,
            frame.Snapshot,
            raw);

        return new Hp8C40M8CSyntheticEvaluation(
            EvidenceMarker,
            frame.Case,
            frame.Ordinal,
            frame.Snapshot.Timestamp,
            raw,
            effective);
    }

    private static Hp8C40M8CSyntheticFrame Create(
        DateTimeOffset timestamp,
        int ordinal,
        string @case,
        double effectiveCpuC,
        double gpuC)
    {
        if (ordinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var packageC = Math.Min(effectiveCpuC, 90.0);

        var coreTemperatures =
            Enumerable.Range(
                    0,
                    Hp8C40TargetProfile.ExpectedPhysicalCoreCount)
                .Select(index =>
                    new CpuCoreTemperatureSample(
                        CoreIndex: index,
                        LogicalProcessorIndex: index,
                        CoreType: index < 6
                            ? "Performance"
                            : "Efficiency",
                        TemperatureC: index == 0
                            ? effectiveCpuC
                            : Math.Min(effectiveCpuC - 2.0, 88.0)))
                .ToArray();

        var snapshot = new TelemetrySnapshot(
            Timestamp: timestamp,
            CpuName: "Intel Core i7-13700H",
            CpuTemperatureC: packageC,
            CpuPackagePowerW: 45.0,
            CpuLoadPercent: 35.0,
            GpuName: Hp8C40TargetProfile.ExpectedGpuName,
            GpuTemperatureC: gpuC,
            GpuPowerW: 55.0,
            GpuLoadPercent: 80.0,
            CpuFanRpm: 5000.0,
            GpuFanRpm: 5000.0)
        {
            CpuExpectedPhysicalCoreCount =
                Hp8C40TargetProfile.ExpectedPhysicalCoreCount,
            CpuCoreTemperatures = coreTemperatures
        };

        return new Hp8C40M8CSyntheticFrame(
            EvidenceMarker,
            @case,
            ordinal,
            snapshot);
    }
}

internal sealed record Hp8C40M8CSyntheticFrame(
    string EvidenceKind,
    string Case,
    int Ordinal,
    TelemetrySnapshot Snapshot);

internal sealed record Hp8C40M8CSyntheticEvaluation(
    string EvidenceKind,
    string Case,
    int Ordinal,
    DateTimeOffset Timestamp,
    SafetyGateResult Raw,
    SafetyGateResult Effective);
