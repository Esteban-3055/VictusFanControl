using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Safety;

/// <summary>
/// Exact-target temporal confirmation layer for HP 8C40 CPU thermal spikes.
///
/// SafetyGate remains the stateless raw detector at 95 C CPU / 87 C GPU.
/// On the validated HP 8C40 target only, CPU readings from 95 C through
/// 98.x C require five unique consecutive fresh telemetry snapshots before
/// they are promoted to an effective thermal emergency. GPU >= 87 C and
/// CPU >= 99 C remain immediate.
///
/// Re-evaluating the same telemetry timestamp never advances the streak.
/// Any unsafe non-thermal state, a below-threshold CPU sample, a lifecycle
/// gap, or a target mismatch resets the CPU confirmation streak.
/// </summary>
public sealed class Hp8C40ThermalEmergencyConfirmation
{
    public const int RequiredConsecutiveCpuSamples = 5;
    public const double CpuHardEmergencyC = 99.0;

    private static readonly TimeSpan MaximumConfirmationSampleGap =
        SafetyGate.MaximumTelemetryAge;

    private readonly object _gate = new();

    private DateTimeOffset? _lastCountedHighTimestamp;
    private DateTimeOffset? _lastObservedSnapshotTimestamp;
    private int _cpuConsecutiveHighSamples;

    public int CurrentCpuConsecutiveHighSamples
    {
        get
        {
            lock (_gate)
            {
                return _cpuConsecutiveHighSamples;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ResetLocked();
        }
    }

    public SafetyGateResult Apply(
        HardwareIdentity hardware,
        TelemetrySnapshot? snapshot,
        SafetyGateResult raw)
    {
        lock (_gate)
        {
            return ProcessLocked(
                hardware,
                snapshot,
                raw,
                mutate: true);
        }
    }

    public SafetyGateResult Preview(
        HardwareIdentity hardware,
        TelemetrySnapshot? snapshot,
        SafetyGateResult raw)
    {
        lock (_gate)
        {
            return ProcessLocked(
                hardware,
                snapshot,
                raw,
                mutate: false);
        }
    }

    private SafetyGateResult ProcessLocked(
        HardwareIdentity hardware,
        TelemetrySnapshot? snapshot,
        SafetyGateResult raw,
        bool mutate)
    {
        if (!Hp8C40TargetProfile.Matches(hardware, out _))
        {
            if (mutate)
            {
                ResetLocked();
            }

            return raw;
        }

        if (snapshot is null)
        {
            if (mutate)
            {
                ResetLocked();
            }

            return raw;
        }

        var safeApartFromThermal =
            raw.BoardAllowed &&
            raw.RuntimeHealthy &&
            raw.SnapshotComplete &&
            raw.SnapshotFresh &&
            raw.TelemetryDeviceIdentityValid &&
            raw.SensorsPlausible &&
            snapshot.CpuCoreTelemetryComplete;

        if (!safeApartFromThermal)
        {
            if (mutate)
            {
                ResetLocked();
            }

            return raw;
        }

        var effectiveCpu = snapshot.CpuControlTemperatureC;
        var gpuTemperature = snapshot.GpuTemperatureC;

        if (!effectiveCpu.HasValue ||
            !gpuTemperature.HasValue)
        {
            if (mutate)
            {
                ResetLocked();
            }

            return raw;
        }

        if (gpuTemperature.Value >= SafetyGate.GpuEmergencyC)
        {
            if (mutate)
            {
                ResetLocked();
            }

            return AddReason(
                raw,
                $"HP 8C40 immediate GPU thermal handoff: {gpuTemperature.Value:0.0} C >= {SafetyGate.GpuEmergencyC:0} C.");
        }

        if (effectiveCpu.Value >= CpuHardEmergencyC)
        {
            if (mutate)
            {
                ResetLocked();
            }

            return AddReason(
                raw,
                $"HP 8C40 immediate CPU hard thermal handoff: {effectiveCpu.Value:0.0} C >= {CpuHardEmergencyC:0} C.");
        }

        if (effectiveCpu.Value < SafetyGate.CpuEmergencyC)
        {
            if (mutate)
            {
                ResetLocked(snapshot.Timestamp);
            }

            return raw;
        }

        if (!raw.ThermalEmergency)
        {
            if (mutate)
            {
                ResetLocked(snapshot.Timestamp);
            }

            return raw;
        }

        var projectedStreak = _cpuConsecutiveHighSamples;

        var isNewerSnapshot =
            !_lastObservedSnapshotTimestamp.HasValue ||
            snapshot.Timestamp > _lastObservedSnapshotTimestamp.Value;

        if (isNewerSnapshot)
        {
            var continuesStreak =
                _lastCountedHighTimestamp.HasValue &&
                snapshot.Timestamp > _lastCountedHighTimestamp.Value &&
                snapshot.Timestamp - _lastCountedHighTimestamp.Value <=
                    MaximumConfirmationSampleGap;

            projectedStreak = continuesStreak
                ? Math.Min(
                    RequiredConsecutiveCpuSamples,
                    _cpuConsecutiveHighSamples + 1)
                : 1;

            if (mutate)
            {
                _cpuConsecutiveHighSamples = projectedStreak;
                _lastCountedHighTimestamp = snapshot.Timestamp;
                _lastObservedSnapshotTimestamp = snapshot.Timestamp;
            }
        }

        if (projectedStreak >= RequiredConsecutiveCpuSamples)
        {
            return AddReason(
                raw,
                $"HP 8C40 CPU thermal handoff confirmed: {projectedStreak}/{RequiredConsecutiveCpuSamples} unique consecutive snapshots >= {SafetyGate.CpuEmergencyC:0} C.");
        }

        var reasons = raw.Reasons
            .Where(reason =>
                !reason.StartsWith(
                    "Thermal handoff threshold reached",
                    StringComparison.Ordinal))
            .Concat(
            [
                $"HP 8C40 transient CPU thermal threshold pending confirmation ({projectedStreak}/{RequiredConsecutiveCpuSamples} unique consecutive snapshots >= {SafetyGate.CpuEmergencyC:0} C; immediate hard handoff remains >= {CpuHardEmergencyC:0} C)."
            ])
            .ToArray();

        return raw with
        {
            ThermalEmergency = false,
            PreconditionsReady = true,
            CustomControlPermitted = raw.FanWritePathPresent,
            Reasons = reasons
        };
    }

    private static SafetyGateResult AddReason(
        SafetyGateResult result,
        string reason) =>
        result with
        {
            Reasons = result.Reasons
                .Concat([reason])
                .ToArray()
        };

    private void ResetLocked(
        DateTimeOffset? observedTimestamp = null)
    {
        _cpuConsecutiveHighSamples = 0;
        _lastCountedHighTimestamp = null;
        _lastObservedSnapshotTimestamp = observedTimestamp;
    }
}
