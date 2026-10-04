using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

public sealed record AdaptiveFanPolicyShadowEvaluation(
    DateTimeOffset Timestamp,
    bool SafetyPreconditionsReady,
    bool EffectiveThermalEmergency,
    bool PolicyAccepted,
    int? RecommendedEqualLevel,
    double? RawDemandLevel,
    AdaptiveFanControlIntent Intent,
    string Detail,
    IReadOnlyList<string> SafetyReasons)
{
    public bool HardwareWriteCapable => false;
}

/// <summary>
/// Read-only adaptive-policy shadow evaluator.
///
/// This combines the real SafetyGate, exact-target HP 8C40 temporal CPU
/// confirmation, pure policy engine, and pure intent planner. It has no
/// coordinator/backend/watchdog/WMI/EC write dependency and therefore cannot
/// acquire or alter physical fan authority.
/// </summary>
public sealed class AdaptiveFanPolicyShadowEvaluator
{
    private readonly HardwareIdentity _hardware;
    private readonly AdaptiveFanPolicyEngine _engine;
    private readonly AdaptiveFanInertiaPolicy? _preparedEngine;
    private readonly bool _preparedAutomatic;
    private Hp8C40AutomaticThermalAdmission? _admission;
    private readonly AdaptiveFanControlIntentPlanner _planner = new();
    private readonly Hp8C40ThermalEmergencyConfirmation _thermalConfirmation =
        new();

    public AdaptiveFanPolicyShadowEvaluator(
        HardwareIdentity hardware,
        AdaptiveFanPolicyConfig config,
        bool preparedAutomatic = false,
        FanConfiguration? configuration = null)
    {
        _hardware = hardware;
        _engine = new AdaptiveFanPolicyEngine(config);
        _preparedAutomatic = preparedAutomatic;
        if (preparedAutomatic)
        {
            _preparedEngine = configuration is null
                ? new AdaptiveFanInertiaPolicy(Hp8C40AutomaticPolicy.Create(config))
                : new AdaptiveFanInertiaPolicy(configuration.BuildPolicy(), configuration.Tuning);
            _admission = new Hp8C40AutomaticThermalAdmission(hardware);
        }
    }

    public void Reset()
    {
        _thermalConfirmation.Reset();
        _engine.Reset();
        _preparedEngine?.Reset();
        // Reset is an explicit preview/profile/lifecycle restart, never a write authorization.
        if (_preparedAutomatic) _admission = new Hp8C40AutomaticThermalAdmission(_hardware);
        _planner.Reset();
    }

    public AdaptiveFanPolicyShadowEvaluation Evaluate(
        SystemState state,
        TelemetrySnapshot snapshot,
        DateTimeOffset evaluatedAt)
    {
        var raw =
            SafetyGate.Evaluate(
                _hardware,
                state,
                snapshot,
                evaluatedAt,
                fanWritePathPresent: false);

        var effective = _admission is not null
            ? _admission.ObserveOrPreview(snapshot, raw).EffectiveSafety
            : _thermalConfirmation.Apply(_hardware, snapshot, raw);

        if (!effective.PreconditionsReady)
        {
            _engine.Reset();
            _preparedEngine?.Reset();

            var intent =
                _planner.Plan(
                    controlPreconditionsReady: false,
                    decision: null);

            return new AdaptiveFanPolicyShadowEvaluation(
                snapshot.Timestamp,
                SafetyPreconditionsReady: false,
                EffectiveThermalEmergency:
                    effective.ThermalEmergency,
                PolicyAccepted: false,
                RecommendedEqualLevel: null,
                RawDemandLevel: null,
                Intent: intent,
                Detail:
                    "Shadow policy held/released firmware authority because " +
                    "the effective SafetyGate preconditions are not ready.",
                SafetyReasons: effective.Reasons);
        }

        var input =
            new AdaptiveFanPolicyInput(
                Timestamp: snapshot.Timestamp,
                CpuEffectiveTemperatureC:
                    snapshot.CpuControlTemperatureC!.Value,
                CpuPackagePowerW:
                    snapshot.CpuPackagePowerW!.Value,
                CpuLoadPercent:
                    snapshot.CpuLoadPercent!.Value,
                GpuTemperatureC:
                    snapshot.GpuTemperatureC!.Value,
                GpuPowerW:
                    snapshot.GpuPowerW!.Value,
                GpuLoadPercent:
                    snapshot.GpuLoadPercent!.Value);

        var preparedDecision = _preparedEngine?.Evaluate(input);
        var decision = preparedDecision is null ? _engine.Evaluate(input) :
            new AdaptiveFanPolicyDecision(preparedDecision.Accepted, preparedDecision.EqualFanLevel,
                preparedDecision.RawDemandLevel, preparedDecision.Detail);

        if (!decision.Accepted ||
            !decision.EqualFanLevel.HasValue)
        {
            _engine.Reset();
            _preparedEngine?.Reset();

            var release =
                _planner.Plan(
                    controlPreconditionsReady: false,
                    decision: null);

            return new AdaptiveFanPolicyShadowEvaluation(
                snapshot.Timestamp,
                SafetyPreconditionsReady: true,
                EffectiveThermalEmergency:
                    effective.ThermalEmergency,
                PolicyAccepted: false,
                RecommendedEqualLevel: null,
                RawDemandLevel:
                    decision.RawDemandLevel,
                Intent: release,
                Detail:
                    $"Shadow policy refused the telemetry epoch: {decision.Detail}",
                SafetyReasons: effective.Reasons);
        }

        var planned =
            _planner.Plan(
                controlPreconditionsReady: true,
                decision);

        return new AdaptiveFanPolicyShadowEvaluation(
            snapshot.Timestamp,
            SafetyPreconditionsReady: true,
            EffectiveThermalEmergency:
                effective.ThermalEmergency,
            PolicyAccepted: true,
            RecommendedEqualLevel:
                decision.EqualFanLevel,
            RawDemandLevel:
                decision.RawDemandLevel,
            Intent: planned,
            Detail:
                $"{decision.Detail} {planned.Detail}",
            SafetyReasons: effective.Reasons);
    }
}
