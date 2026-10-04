using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Runtime;

internal sealed record WmiFanThermalAdmissionSettings(
    int RequiredConsecutiveCpuSamples = Hp8C40ThermalEmergencyConfirmation.RequiredConsecutiveCpuSamples,
    int MaximumConfirmationMilliseconds = 2000,
    double CpuConfirmationC = SafetyGate.CpuEmergencyC,
    double CpuImmediateHandoffC = Hp8C40ThermalEmergencyConfirmation.CpuHardEmergencyC,
    double GpuImmediateHandoffC = SafetyGate.GpuEmergencyC,
    int NormalDelayMilliseconds = 1000,
    int PendingDelayMilliseconds = 0,
    string Scope = "experiment-only;healthy-start;unique-fresh-samples;monotonic-deadline;sequential-acquisition");

internal sealed record WmiFanThermalAdmissionDecision(
    SafetyGateResult RawSafety,
    SafetyGateResult EffectiveSafety,
    int CpuHighSamples,
    bool CpuConfirmationPending,
    long? ConfirmationElapsedMilliseconds,
    int? RemainingConfirmationMilliseconds,
    bool Closed,
    string? StopReason);

/// <summary>
/// Experimental wrapper around the unchanged exact-target CPU confirmation.
/// Observe counts a new acquisition; Preview only rechecks its immutable epoch.
/// Expiry permanently closes admission, independently of sample-count progress.
/// This deadline cannot cancel an already-dispatched firmware call.
/// </summary>
internal sealed class WmiFanThermalAdmission
{
    internal static WmiFanThermalAdmissionSettings Settings { get; } = new();
    private readonly object _gate = new();
    private readonly HardwareIdentity _hardware;
    private readonly Func<long> _milliseconds;
    private readonly Hp8C40ThermalEmergencyConfirmation _confirmation = new();
    private TelemetrySnapshot? _latest;
    private long? _firstHighMilliseconds;
    private long? _lastMilliseconds;
    private bool _normalEstablished;
    private bool _thermalStop;
    private string? _stopReason;

    internal WmiFanThermalAdmission(HardwareIdentity hardware, Func<long>? milliseconds = null)
    {
        if (!Hp8C40TargetProfile.Matches(hardware, out _))
            throw new ArgumentException("Thermal admission requires exact HP 8C40/F.18.", nameof(hardware));
        _hardware = hardware;
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    }

    internal WmiFanThermalAdmissionDecision Observe(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            var elapsed = CheckClock(); // Check expiry BEFORE a late cool sample could reset it.
            var raw = SafetyGate.Evaluate(_hardware, SystemState.Healthy, snapshot, now, true);
            if (_stopReason is not null) return Decision(raw, raw, elapsed);
            if (_latest is not null && (snapshot.Timestamp <= _latest.Timestamp ||
                (_normalEstablished && snapshot.Timestamp - _latest.Timestamp > SafetyGate.MaximumTelemetryAge)))
            {
                CloseLocked("Thermal admission rejected a duplicate, backwards or discontinuous acquisition.", false);
                return Decision(raw, raw, elapsed);
            }
            _latest = snapshot;

            if (snapshot.CpuControlTemperatureC >= Settings.CpuImmediateHandoffC ||
                snapshot.GpuTemperatureC >= Settings.GpuImmediateHandoffC)
            {
                CloseLocked("Immediate thermal handoff: CPU >=99 C or GPU >=87 C.", true);
                return Decision(raw, raw, elapsed);
            }

            // Do not initialize custom control on a high-temperature epoch.
            // Incomplete initial power/load counters may still warm up in firmware.
            if (!_normalEstablished && raw.ThermalEmergency)
            {
                CloseLocked("Thermal admission requires a healthy below-threshold startup sample.", true);
                return Decision(raw, raw, elapsed);
            }
            var effective = _confirmation.Apply(_hardware, snapshot, raw);
            if (!effective.CustomControlPermitted)
            {
                if (_normalEstablished)
                    CloseLocked(string.Join("; ", effective.Reasons), effective.ThermalEmergency);
                return Decision(raw, effective, elapsed);
            }
            _normalEstablished = true;
            if (raw.ThermalEmergency)
                _firstHighMilliseconds ??= _lastMilliseconds!.Value;
            else
                _firstHighMilliseconds = null;
            return Decision(raw, effective, _firstHighMilliseconds.HasValue ?
                _lastMilliseconds!.Value - _firstHighMilliseconds.Value : null);
        }
    }

    internal WmiFanThermalAdmissionDecision Preview(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            var elapsed = CheckClock();
            var raw = SafetyGate.Evaluate(_hardware, SystemState.Healthy, snapshot, now, true);
            if (!ReferenceEquals(snapshot, _latest))
                CloseLocked("Thermal dispatch preview must use the last observed immutable acquisition.", false);
            var effective = _stopReason is null ? _confirmation.Preview(_hardware, snapshot, raw) : raw;
            if (!effective.CustomControlPermitted)
                CloseLocked(string.Join("; ", effective.Reasons), effective.ThermalEmergency);
            return Decision(raw, effective, elapsed);
        }
    }

    internal void EnsureDispatchAllowed(TelemetrySnapshot snapshot, DateTimeOffset now)
    {
        var decision = Preview(snapshot, now);
        if (!decision.EffectiveSafety.CustomControlPermitted)
            throw new InvalidOperationException("Thermal dispatch admission lost: " +
                string.Join("; ", decision.EffectiveSafety.Reasons));
    }

    internal int? RemainingConfirmationMilliseconds
    {
        get
        {
            lock (_gate)
            {
                var elapsed = CheckClock();
                ThrowIfClosed();
                return elapsed.HasValue ? (int)(Settings.MaximumConfirmationMilliseconds - elapsed.Value) : null;
            }
        }
    }

    internal void EnsureOpen()
    {
        lock (_gate) { CheckClock(); ThrowIfClosed(); }
    }

    internal TelemetrySnapshot? LastObservedSnapshot
    {
        get { lock (_gate) return _latest; }
    }

    internal void Close(string reason, bool thermalEmergency = false)
    {
        lock (_gate) CloseLocked(reason, thermalEmergency);
    }

    private long? CheckClock()
    {
        var current = _milliseconds();
        if (_lastMilliseconds.HasValue && current < _lastMilliseconds.Value)
            CloseLocked("Thermal admission monotonic clock regressed.", false);
        _lastMilliseconds = current;
        var elapsed = _firstHighMilliseconds.HasValue ? current - _firstHighMilliseconds.Value : (long?)null;
        if (elapsed >= Settings.MaximumConfirmationMilliseconds)
            CloseLocked("CPU thermal confirmation deadline expired (2000 ms); normal admission closed.", true);
        return elapsed;
    }

    private void CloseLocked(string reason, bool thermalEmergency)
    {
        _stopReason ??= reason;
        _thermalStop |= thermalEmergency;
    }

    private void ThrowIfClosed()
    {
        if (_stopReason is not null) throw new InvalidOperationException(_stopReason);
    }

    private WmiFanThermalAdmissionDecision Decision(SafetyGateResult raw, SafetyGateResult effective, long? elapsed)
    {
        if (_stopReason is not null)
            effective = effective with
            {
                PreconditionsReady = false, CustomControlPermitted = false,
                ThermalEmergency = effective.ThermalEmergency || _thermalStop,
                Reasons = effective.Reasons.Concat([_stopReason]).Distinct().ToArray()
            };
        var pending = _stopReason is null && _firstHighMilliseconds.HasValue;
        return new(raw, effective, _confirmation.CurrentCpuConsecutiveHighSamples, pending,
            elapsed, pending ? (int)(Settings.MaximumConfirmationMilliseconds - elapsed!.Value) : null,
            _stopReason is not null, _stopReason);
    }
}
