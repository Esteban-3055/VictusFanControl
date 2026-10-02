using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

public enum AdaptiveFanProductionMode
{
    Firmware = 0,
    Manual = 1,
    Automatic = 2
}

public enum AdaptiveFanProductionActionKind
{
    Blocked = 0,
    HoldFirmware = 1,
    EnterCustomAndApply = 2,
    ApplyChangedLevel = 3,
    HoldCustom = 4,
    RestoreFirmware = 5
}

public sealed record AdaptiveFanProductionResult(
    AdaptiveFanProductionMode Mode,
    AdaptiveFanProductionActionKind Action,
    bool ExecutionAuthorized,
    int? EqualFanLevel,
    double? RawDemandLevel,
    FanAuthority Authority,
    string Detail);

/// <summary>
/// Narrow production-facing adapter between the pure adaptive policy and
/// FanControlCoordinator. It has no direct WMI, EC, PawnIO, watchdog or HP
/// backend access. Manual and Automatic execution require separate explicit
/// post-M9 authorizations.
/// </summary>
public sealed class AdaptiveFanProductionController
{
    private readonly FanControlCoordinator _coordinator;
    private readonly AdaptiveFanPolicyEngine _engine;
    private readonly AdaptiveFanControlIntentPlanner _planner = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly bool _manualExecutionAuthorized;
    private readonly bool _automaticExecutionAuthorized;
    private readonly int _minimumLevel;
    private readonly int _maximumLevel;

    private AdaptiveFanProductionMode _mode = AdaptiveFanProductionMode.Firmware;
    private int? _lastManualAppliedLevel;

    public AdaptiveFanProductionController(
        FanControlCoordinator coordinator,
        AdaptiveFanPolicyConfig config,
        bool manualExecutionAuthorized,
        bool automaticExecutionAuthorized)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _engine = new AdaptiveFanPolicyEngine(
            config ?? throw new ArgumentNullException(nameof(config)));
        _manualExecutionAuthorized = manualExecutionAuthorized;
        _automaticExecutionAuthorized = automaticExecutionAuthorized;
        _minimumLevel = config.MinimumLevel;
        _maximumLevel = config.MaximumLevel;
    }

    public AdaptiveFanProductionMode Mode => _mode;
    public bool ManualExecutionAuthorized => _manualExecutionAuthorized;
    public bool AutomaticExecutionAuthorized => _automaticExecutionAuthorized;

    public async ValueTask<AdaptiveFanProductionResult> SetModeAsync(
        AdaptiveFanProductionMode requestedMode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requestedMode == AdaptiveFanProductionMode.Manual &&
                !_manualExecutionAuthorized)
            {
                return Result(
                    AdaptiveFanProductionActionKind.Blocked,
                    false,
                    null,
                    null,
                    "Manual mode remains blocked by the post-M9 hardware gate.");
            }

            if (requestedMode == AdaptiveFanProductionMode.Automatic &&
                !_automaticExecutionAuthorized)
            {
                return Result(
                    AdaptiveFanProductionActionKind.Blocked,
                    false,
                    null,
                    null,
                    "Automatic mode remains blocked by the post-M9 hardware gate.");
            }

            if (requestedMode == _mode)
            {
                return Result(
                    _coordinator.Authority == FanAuthority.Custom
                        ? AdaptiveFanProductionActionKind.HoldCustom
                        : AdaptiveFanProductionActionKind.HoldFirmware,
                    true,
                    null,
                    null,
                    $"Production fan mode is already {_mode}.");
            }

            var restored = false;
            if (_coordinator.Authority == FanAuthority.Custom)
            {
                await _coordinator.RestoreFirmwareAsync(
                    $"Fan mode transition {_mode} -> {requestedMode}.",
                    cancellationToken).ConfigureAwait(false);
                restored = true;
            }

            ResetPolicyStateLocked();
            _mode = requestedMode;

            return Result(
                restored
                    ? AdaptiveFanProductionActionKind.RestoreFirmware
                    : AdaptiveFanProductionActionKind.HoldFirmware,
                true,
                null,
                null,
                $"Production fan mode changed to {_mode}; no new fan command was issued.");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private const int ManualFreshSafetyMaximumAttempts = 4;

    public ValueTask<AdaptiveFanProductionResult> ApplyManualAsync(
        int equalFanLevel,
        SafetyGateResult effectiveSafety,
        CancellationToken cancellationToken) =>
        ApplyManualCoreAsync(
            equalFanLevel,
            effectiveSafety,
            refreshSafetyProvider: null,
            cancellationToken);

    public ValueTask<AdaptiveFanProductionResult> ApplyManualAsync(
        int equalFanLevel,
        SafetyGateResult effectiveSafety,
        Func<SafetyGateResult?> refreshSafetyProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshSafetyProvider);

        return ApplyManualCoreAsync(
            equalFanLevel,
            effectiveSafety,
            refreshSafetyProvider,
            cancellationToken);
    }

    private async ValueTask<AdaptiveFanProductionResult> ApplyManualCoreAsync(
        int equalFanLevel,
        SafetyGateResult initialSafety,
        Func<SafetyGateResult?>? refreshSafetyProvider,
        CancellationToken cancellationToken)
    {
        if (equalFanLevel < _minimumLevel || equalFanLevel > _maximumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(equalFanLevel),
                equalFanLevel,
                $"Manual equal fan level must remain inside {_minimumLevel}..{_maximumLevel}.");
        }

        if (!_manualExecutionAuthorized)
        {
            return Result(
                AdaptiveFanProductionActionKind.Blocked,
                false,
                equalFanLevel,
                null,
                "Manual fan execution remains blocked by the post-M9 hardware gate.");
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mode != AdaptiveFanProductionMode.Manual)
            {
                return Result(
                    AdaptiveFanProductionActionKind.Blocked,
                    true,
                    equalFanLevel,
                    null,
                    "Manual command refused because Manual mode is not selected.");
            }

            if (!initialSafety.CustomControlPermitted ||
                !initialSafety.SnapshotTimestamp.HasValue)
            {
                _lastManualAppliedLevel = null;
                return await RestoreIfOwnedLockedAsync(
                    "Manual command refused because effective SafetyGate admission is unavailable.",
                    true,
                    cancellationToken).ConfigureAwait(false);
            }

            var safety = initialSafety;
            var enteredDuringRequest = false;
            var maximumAttempts =
                refreshSafetyProvider is null
                    ? 1
                    : ManualFreshSafetyMaximumAttempts;

            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    safety =
                        refreshSafetyProvider?.Invoke() ??
                        throw new InvalidOperationException(
                            "Manual SafetyGate refresh provider unexpectedly became unavailable.");

                    if (!safety.CustomControlPermitted ||
                        !safety.SnapshotTimestamp.HasValue)
                    {
                        _lastManualAppliedLevel = null;
                        return await RestoreIfOwnedLockedAsync(
                            "Manual command released Custom authority because refreshed SafetyGate admission is unavailable.",
                            true,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                if (_coordinator.Authority != FanAuthority.Custom)
                {
                    var entered = await _coordinator.TryEnterCustomAsync(
                        safety,
                        cancellationToken).ConfigureAwait(false);

                    if (!entered)
                    {
                        _lastManualAppliedLevel = null;

                        if (refreshSafetyProvider is not null &&
                            !_coordinator.IsSafetyEvaluationCurrent(safety) &&
                            attempt < maximumAttempts)
                        {
                            await Task.Yield();
                            continue;
                        }

                        return Result(
                            AdaptiveFanProductionActionKind.Blocked,
                            true,
                            equalFanLevel,
                            null,
                            "Manual command could not acquire current Custom authority.");
                    }

                    enteredDuringRequest = true;
                    _lastManualAppliedLevel = null;

                    // EnterCustomMode is deliberately read-only but can spend
                    // enough time in EC/watchdog admission for the runtime
                    // supervisor to accept a newer SafetyGate evaluation. The
                    // real GUI path therefore refreshes safety after admission
                    // before the first fan command is allowed to reach ApplyAsync.
                    if (refreshSafetyProvider is not null)
                    {
                        var refreshed = refreshSafetyProvider();
                        if (refreshed is null ||
                            !refreshed.CustomControlPermitted ||
                            !refreshed.SnapshotTimestamp.HasValue)
                        {
                            _lastManualAppliedLevel = null;
                            return await RestoreIfOwnedLockedAsync(
                                "Manual command released read-only Custom preparation because post-admission SafetyGate refresh is unavailable.",
                                true,
                                cancellationToken).ConfigureAwait(false);
                        }

                        safety = refreshed;
                    }
                }

                if (_lastManualAppliedLevel == equalFanLevel)
                {
                    return Result(
                        AdaptiveFanProductionActionKind.HoldCustom,
                        true,
                        equalFanLevel,
                        null,
                        $"Holding equal {equalFanLevel}/{equalFanLevel}; unchanged manual target was not retransmitted.");
                }

                try
                {
                    await _coordinator.ApplyAsync(
                        new FanCommand(
                            equalFanLevel,
                            equalFanLevel,
                            $"manual equal target {equalFanLevel}/{equalFanLevel}"),
                        safety,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (FanControlStaleSafetyException)
                    when (refreshSafetyProvider is not null &&
                          attempt < maximumAttempts)
                {
                    _lastManualAppliedLevel = null;
                    await Task.Yield();
                    continue;
                }
                catch (FanControlStaleSafetyException)
                {
                    _lastManualAppliedLevel = null;
                    return await RestoreIfOwnedLockedAsync(
                        $"Manual command could not obtain a current SafetyGate evaluation after {maximumAttempts} bounded attempt(s).",
                        true,
                        cancellationToken).ConfigureAwait(false);
                }

                _lastManualAppliedLevel = equalFanLevel;
                _engine.Reset();
                _planner.Reset();

                return Result(
                    enteredDuringRequest
                        ? AdaptiveFanProductionActionKind.EnterCustomAndApply
                        : AdaptiveFanProductionActionKind.ApplyChangedLevel,
                    true,
                    equalFanLevel,
                    null,
                    $"Applied one equal manual target {equalFanLevel}/{equalFanLevel} through FanControlCoordinator.");
            }

            _lastManualAppliedLevel = null;
            return await RestoreIfOwnedLockedAsync(
                "Manual command exhausted its bounded fresh-Safety attempts.",
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<AdaptiveFanProductionResult> ProcessAutomaticAsync(
        TelemetrySnapshot snapshot,
        SafetyGateResult effectiveSafety,
        CancellationToken cancellationToken)
    {
        if (!_automaticExecutionAuthorized)
        {
            return Result(
                AdaptiveFanProductionActionKind.Blocked,
                false,
                null,
                null,
                "Automatic fan execution remains blocked by the post-M9 hardware gate.");
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mode != AdaptiveFanProductionMode.Automatic)
            {
                return Result(
                    AdaptiveFanProductionActionKind.Blocked,
                    true,
                    null,
                    null,
                    "Automatic policy is not the selected production fan mode.");
            }

            if (!effectiveSafety.SnapshotTimestamp.HasValue ||
                effectiveSafety.SnapshotTimestamp.Value != snapshot.Timestamp)
            {
                ResetPolicyStateLocked();
                return await RestoreIfOwnedLockedAsync(
                    "Automatic policy refused a SafetyGate/telemetry epoch mismatch.",
                    true,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!effectiveSafety.CustomControlPermitted)
            {
                ResetPolicyStateLocked();
                return await RestoreIfOwnedLockedAsync(
                    "Automatic policy released Custom authority because effective SafetyGate preconditions are not ready.",
                    true,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!TryBuildPolicyInput(snapshot, out var input, out var inputFailure))
            {
                ResetPolicyStateLocked();
                return await RestoreIfOwnedLockedAsync(
                    inputFailure,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }

            // If another safety/lifecycle path restored Firmware since the last
            // policy epoch, discard notional ownership before evaluating again.
            if (_coordinator.Authority != FanAuthority.Custom &&
                _planner.NotionalCustom)
            {
                _engine.Reset();
                _planner.Reset();
            }

            var decision = _engine.Evaluate(input);
            if (!decision.Accepted || !decision.EqualFanLevel.HasValue)
            {
                ResetPolicyStateLocked();
                return await RestoreIfOwnedLockedAsync(
                    $"Automatic policy refused the telemetry epoch: {decision.Detail}",
                    true,
                    cancellationToken).ConfigureAwait(false);
            }

            var intent = _planner.Plan(true, decision);

            if (intent.Kind == AdaptiveFanControlIntentKind.HoldCustom &&
                _coordinator.Authority != FanAuthority.Custom)
            {
                ResetPolicyStateLocked();
                return Result(
                    AdaptiveFanProductionActionKind.Blocked,
                    true,
                    decision.EqualFanLevel,
                    decision.RawDemandLevel,
                    "Automatic policy observed loss of Custom authority after planning; no command was issued.");
            }

            if (intent.Kind == AdaptiveFanControlIntentKind.EnterCustomAndApply ||
                (intent.Kind == AdaptiveFanControlIntentKind.ApplyChangedLevel &&
                 _coordinator.Authority != FanAuthority.Custom))
            {
                var entered = await _coordinator.TryEnterCustomAsync(
                    effectiveSafety,
                    cancellationToken).ConfigureAwait(false);

                if (!entered)
                {
                    ResetPolicyStateLocked();
                    return Result(
                        AdaptiveFanProductionActionKind.Blocked,
                        true,
                        decision.EqualFanLevel,
                        decision.RawDemandLevel,
                        "Automatic policy could not acquire current Custom authority.");
                }
            }

            if (intent.Kind == AdaptiveFanControlIntentKind.EnterCustomAndApply ||
                intent.Kind == AdaptiveFanControlIntentKind.ApplyChangedLevel)
            {
                try
                {
                    var level = decision.EqualFanLevel.Value;
                    await _coordinator.ApplyAsync(
                        new FanCommand(
                            level,
                            level,
                            $"adaptive equal target {level}/{level}; rawDemand={decision.RawDemandLevel:0.00}"),
                        effectiveSafety,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (FanControlStaleSafetyException)
                {
                    ResetPolicyStateLocked();
                    return Result(
                        AdaptiveFanProductionActionKind.Blocked,
                        true,
                        decision.EqualFanLevel,
                        decision.RawDemandLevel,
                        "Automatic command was superseded by a newer SafetyGate evaluation.");
                }
            }

            var action = intent.Kind switch
            {
                AdaptiveFanControlIntentKind.EnterCustomAndApply =>
                    AdaptiveFanProductionActionKind.EnterCustomAndApply,
                AdaptiveFanControlIntentKind.ApplyChangedLevel =>
                    AdaptiveFanProductionActionKind.ApplyChangedLevel,
                AdaptiveFanControlIntentKind.HoldCustom =>
                    AdaptiveFanProductionActionKind.HoldCustom,
                _ => AdaptiveFanProductionActionKind.Blocked
            };

            return Result(
                action,
                true,
                decision.EqualFanLevel,
                decision.RawDemandLevel,
                $"{decision.Detail} {intent.Detail}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<AdaptiveFanProductionResult> ReleaseToFirmwareAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetPolicyStateLocked();
            _mode = AdaptiveFanProductionMode.Firmware;
            return await RestoreIfOwnedLockedAsync(
                reason,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<AdaptiveFanProductionResult> RestoreIfOwnedLockedAsync(
        string reason,
        bool executionAuthorized,
        CancellationToken cancellationToken)
    {
        if (_coordinator.Authority == FanAuthority.Custom)
        {
            await _coordinator.RestoreFirmwareAsync(
                reason,
                cancellationToken).ConfigureAwait(false);

            return Result(
                AdaptiveFanProductionActionKind.RestoreFirmware,
                executionAuthorized,
                null,
                null,
                $"{reason} Firmware restore completed.");
        }

        return Result(
            _coordinator.Authority == FanAuthority.Firmware
                ? AdaptiveFanProductionActionKind.HoldFirmware
                : AdaptiveFanProductionActionKind.Blocked,
            executionAuthorized,
            null,
            null,
            $"{reason} No new fan write was issued; authority={_coordinator.Authority}.");
    }

    private void ResetPolicyStateLocked()
    {
        _engine.Reset();
        _planner.Reset();
        _lastManualAppliedLevel = null;
    }

    private static bool TryBuildPolicyInput(
        TelemetrySnapshot snapshot,
        out AdaptiveFanPolicyInput input,
        out string failure)
    {
        if (!snapshot.IsComplete ||
            !snapshot.CpuControlTemperatureC.HasValue ||
            !snapshot.CpuPackagePowerW.HasValue ||
            !snapshot.CpuLoadPercent.HasValue ||
            !snapshot.GpuTemperatureC.HasValue ||
            !snapshot.GpuPowerW.HasValue ||
            !snapshot.GpuLoadPercent.HasValue)
        {
            input = default!;
            failure = "Automatic policy refused incomplete telemetry before any fan command.";
            return false;
        }

        input = new AdaptiveFanPolicyInput(
            snapshot.Timestamp,
            snapshot.CpuControlTemperatureC.Value,
            snapshot.CpuPackagePowerW.Value,
            snapshot.CpuLoadPercent.Value,
            snapshot.GpuTemperatureC.Value,
            snapshot.GpuPowerW.Value,
            snapshot.GpuLoadPercent.Value);

        failure = string.Empty;
        return true;
    }

    private AdaptiveFanProductionResult Result(
        AdaptiveFanProductionActionKind action,
        bool executionAuthorized,
        int? equalFanLevel,
        double? rawDemandLevel,
        string detail) =>
        new(
            _mode,
            action,
            executionAuthorized,
            equalFanLevel,
            rawDemandLevel,
            _coordinator.Authority,
            detail);
}
