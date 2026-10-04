using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
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
    string Detail)
{
    public double? SmoothedDemandLevel { get; init; }
    public double? ActuationDemandLevel { get; init; }
    public bool ThermalOverride { get; init; }
}

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
    private AdaptiveFanInertiaPolicy? _preparedEngine;
    private FanConfiguration? _automaticConfiguration;
    public FanConfiguration? AutomaticConfiguration => _automaticConfiguration is null ? null : FanConfigurationStore.Copy(_automaticConfiguration);
    public int AutomaticNormalPollingDelayMilliseconds => _automaticConfiguration?.Tuning.NormalPollingDelayMilliseconds ?? 1000;
    private readonly HardwareIdentity? _automaticHardware;
    private readonly Func<long>? _automaticMilliseconds;
    private readonly Func<DateTimeOffset> _utcNow;
    private Hp8C40AutomaticThermalAdmission? _automaticAdmission;
    private readonly AdaptiveFanControlIntentPlanner _planner = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Hp8C40P16QualificationSession? _qualificationSession;
    private readonly bool _manualExecutionAuthorized;
    private readonly bool _automaticExecutionAuthorized;
    private readonly int _minimumLevel;
    private readonly int _maximumLevel;

    private AdaptiveFanProductionMode _mode = AdaptiveFanProductionMode.Firmware;
    private int? _lastManualAppliedLevel;
    private AdaptiveFanProductionResult? _lastAutomaticResult;
    public AdaptiveFanProductionResult? LastAutomaticResult => Volatile.Read(ref _lastAutomaticResult);

    public AdaptiveFanProductionController(
        FanControlCoordinator coordinator,
        AdaptiveFanPolicyConfig config,
        bool manualExecutionAuthorized,
        bool automaticExecutionAuthorized,
        Hp8C40P16QualificationSession? qualificationSession = null,
        HardwareIdentity? automaticHardware = null,
        Func<long>? automaticMilliseconds = null,
        Func<DateTimeOffset>? utcNow = null,
        FanConfiguration? automaticConfiguration = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _engine = new AdaptiveFanPolicyEngine(
            config ?? throw new ArgumentNullException(nameof(config)));
        _automaticHardware = automaticHardware;
        _automaticMilliseconds = automaticMilliseconds;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (automaticHardware is not null)
        {
            // Validate the exact target even while execution remains gated off.
            _ = new Hp8C40AutomaticThermalAdmission(automaticHardware, automaticMilliseconds);
            _automaticConfiguration = automaticConfiguration is null ? null : FanConfigurationStore.Copy(automaticConfiguration);
            _preparedEngine = _automaticConfiguration is null
                ? new AdaptiveFanInertiaPolicy(Hp8C40AutomaticPolicy.Create(config))
                : new AdaptiveFanInertiaPolicy(_automaticConfiguration.BuildPolicy(), _automaticConfiguration.Tuning);
        }
        _qualificationSession = qualificationSession;
        _manualExecutionAuthorized = manualExecutionAuthorized;
        _automaticExecutionAuthorized = automaticExecutionAuthorized;
        _minimumLevel = config.MinimumLevel;
        _maximumLevel = config.MaximumLevel;
    }

    public AdaptiveFanProductionMode Mode => _mode;
    public bool ManualExecutionAuthorized =>
        _manualExecutionAuthorized && !(_qualificationSession?.IsInterrupted ?? false);
    public bool AutomaticExecutionAuthorized => _automaticExecutionAuthorized;
    public bool AutomaticFreshAcquisitionRequired => _automaticExecutionAuthorized &&
        _mode == AdaptiveFanProductionMode.Automatic && _automaticAdmission is { IsClosed: false };
    public int? AutomaticAcquisitionBudgetMilliseconds
    {
        get
        {
            var admission = _automaticAdmission;
            if (!_automaticExecutionAuthorized || _mode != AdaptiveFanProductionMode.Automatic ||
                admission is null || admission.IsClosed) return null;
            // Expiry can race this getter. A closed session uses ordinary read-only
            // telemetry; it cannot terminate the worker or reopen write admission.
            try { return admission.RemainingConfirmationMilliseconds; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public async ValueTask<AdaptiveFanProductionResult> SetModeAsync(
        AdaptiveFanProductionMode requestedMode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }

        if (requestedMode != AdaptiveFanProductionMode.Firmware && (_qualificationSession?.IsInterrupted ?? false))
        {
            return Result(AdaptiveFanProductionActionKind.Blocked, false, null, null,
                "Qualification session was permanently interrupted; Firmware release remains available.");
        }

        // Firmware release stays available after an interrupted qualification.
        using var sessionCancellation = requestedMode == AdaptiveFanProductionMode.Firmware || _qualificationSession is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _qualificationSession.InterruptionToken);
        cancellationToken = sessionCancellation?.Token ?? cancellationToken;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requestedMode == AdaptiveFanProductionMode.Manual &&
                !ManualExecutionAuthorized)
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
            _automaticAdmission = requestedMode == AdaptiveFanProductionMode.Automatic && _automaticHardware is not null
                ? new Hp8C40AutomaticThermalAdmission(_automaticHardware, _automaticMilliseconds)
                : null;

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

        if (!ManualExecutionAuthorized)
        {
            return Result(
                AdaptiveFanProductionActionKind.Blocked,
                false,
                equalFanLevel,
                null,
                "Manual fan execution remains blocked by the post-M9 hardware gate.");
        }

        using var sessionCancellation = _qualificationSession is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _qualificationSession.InterruptionToken);
        cancellationToken = sessionCancellation?.Token ?? cancellationToken;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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

                cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException) when (_qualificationSession?.IsInterrupted == true)
        {
            _lastManualAppliedLevel = null;
            await RestoreIfOwnedLockedAsync(
                "Qualification session interrupted during Manual preparation/execution.",
                true, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask ConfigureAutomaticAsync(FanConfiguration configuration, CancellationToken cancellationToken,
        Action<FanConfiguration>? persist = null)
    {
        var copy = FanConfigurationStore.Copy(configuration);
        var engine = new AdaptiveFanInertiaPolicy(copy.BuildPolicy(), copy.Tuning);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mode != AdaptiveFanProductionMode.Firmware || _coordinator.Authority != FanAuthority.Firmware)
                throw new InvalidOperationException("Vuelve a Firmware antes de aplicar ajustes de Automatic.");
            if (_automaticHardware is null)
                throw new InvalidOperationException("Los ajustes requieren el destino HP 8C40/F.18.");
            persist?.Invoke(FanConfigurationStore.Copy(copy)); // A failed save leaves the current configuration intact.
            _automaticConfiguration = copy;
            _preparedEngine = engine;
            ResetPolicyStateLocked();
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>All Automatic consumers use this session; display/dispatch never count a sample.</summary>
    public SafetyGateResult EvaluateAutomaticSafety(
        TelemetrySnapshot? snapshot, SafetyGateResult raw, bool observe)
    {
        var admission = _automaticAdmission;
        if (admission is null) return raw;
        if (!observe && snapshot is not null && !ReferenceEquals(snapshot, admission.LastObservedSnapshot))
        {
            // A queued UI frame may hold either side of the worker's publication.
            // It cannot count, authorize or poison that acquisition. Native
            // dispatch uses strict Preview below, not this presentation branch.
            _ = admission.IsClosed; // Still advance the monotonic expiry check.
            return raw with { PreconditionsReady = false, CustomControlPermitted = false };
        }
        if (snapshot is null)
        {
            admission.Close("Automatic telemetry is missing.");
            return raw with { PreconditionsReady = false, CustomControlPermitted = false };
        }
        return (observe ? admission.ObserveOrPreview(snapshot, raw) : admission.Preview(snapshot, raw)).EffectiveSafety;
    }

    public async ValueTask<AdaptiveFanProductionResult> ProcessAutomaticAsync(
        TelemetrySnapshot snapshot,
        SafetyGateResult effectiveSafety,
        CancellationToken cancellationToken,
        Func<SafetyGateResult?>? refreshRawSafetyProvider = null)
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

            if (_automaticAdmission is not null)
            {
                // Rebuild stateless thermal/freshness checks even if the caller
                // already supplied the effective confirmation decision.
                if (effectiveSafety.SnapshotTimestamp != snapshot.Timestamp)
                    _automaticAdmission.Close("Automatic policy refused a SafetyGate/telemetry epoch mismatch.");
                var raw = SafetyGate.EvaluateForDisplay(_automaticHardware!,
                    effectiveSafety.RuntimeHealthy ? SystemState.Healthy : SystemState.Degraded,
                    snapshot, _utcNow(), effectiveSafety.FanWritePathPresent && _coordinator.BackendCanWrite)
                    with { EvaluationSequence = effectiveSafety.EvaluationSequence };
                effectiveSafety = EvaluateAutomaticSafety(snapshot, raw, observe: true);
            }

            void EnsureAutomaticDispatchAllowed()
            {
                if (_automaticAdmission is null) return;
                var raw = refreshRawSafetyProvider is not null ? refreshRawSafetyProvider() :
                    SafetyGate.EvaluateForDisplay(_automaticHardware!,
                        effectiveSafety.RuntimeHealthy ? SystemState.Healthy : SystemState.Degraded,
                        snapshot, _utcNow(), _coordinator.BackendCanWrite);
                if (raw is null)
                {
                    _automaticAdmission.Close("Automatic dispatch raw SafetyGate is unavailable.");
                    throw new InvalidOperationException("Automatic raw dispatch safety is unavailable.");
                }
                if (raw.SnapshotTimestamp != snapshot.Timestamp)
                    _automaticAdmission.Close("Automatic dispatch was superseded by another telemetry epoch.");
                var checkedSafety = _automaticAdmission.Preview(snapshot, raw).EffectiveSafety;
                if (!checkedSafety.CustomControlPermitted)
                    throw new InvalidOperationException("Automatic dispatch admission lost: " +
                        string.Join("; ", checkedSafety.Reasons));
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
                if (_automaticAdmission is not null)
                {
                    _automaticAdmission.Close("Automatic session unexpectedly lost Custom authority.");
                    ResetPolicyStateLocked();
                    return await RestoreIfOwnedLockedAsync("Automatic authority was lost; explicit mode restart required.",
                        true, cancellationToken).ConfigureAwait(false);
                }
                _engine.Reset();
                _preparedEngine?.Reset();
                _planner.Reset();
            }

            var preparedDecision = _preparedEngine?.Evaluate(input);
            var decision = preparedDecision is null ? _engine.Evaluate(input) :
                new AdaptiveFanPolicyDecision(preparedDecision.Accepted, preparedDecision.EqualFanLevel,
                    preparedDecision.RawDemandLevel, preparedDecision.Detail);
            if (!decision.Accepted || !decision.EqualFanLevel.HasValue)
            {
                _automaticAdmission?.Close("Automatic policy refused the acquisition: " + decision.Detail);
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
                    using var dispatchAdmission = new FanDispatchAdmissionScope(EnsureAutomaticDispatchAllowed);
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

            var result = Result(
                action,
                true,
                decision.EqualFanLevel,
                decision.RawDemandLevel,
                $"{decision.Detail} {intent.Detail}") with
            {
                SmoothedDemandLevel = preparedDecision?.SmoothedDemandLevel,
                ActuationDemandLevel = preparedDecision?.ActuationDemandLevel,
                ThermalOverride = preparedDecision?.ThermalOverride ?? false
            };
            Volatile.Write(ref _lastAutomaticResult, result);
            return result;
        }
        catch (Exception ex) when (_automaticAdmission is not null)
        {
            _automaticAdmission.Close("Automatic operation failed: " + ex.Message);
            ResetPolicyStateLocked();
            await RestoreIfOwnedLockedAsync("Automatic operation failed; session admission is closed.",
                true, CancellationToken.None).ConfigureAwait(false);
            throw;
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
            _automaticAdmission = null;
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
        _preparedEngine?.Reset();
        _planner.Reset();
        _lastManualAppliedLevel = null;
    }

    private bool TryBuildPolicyInput(
        TelemetrySnapshot snapshot,
        out AdaptiveFanPolicyInput input,
        out string failure)
    {
        var cpuDemand = CpuDemandTemperature.Select(snapshot,
            _automaticConfiguration?.Tuning.CpuTemperatureSource ?? CpuDemandTemperatureSource.PackageOrHottestCore,
            _automaticConfiguration?.Tuning.HottestPerformanceCoreCount ?? 3);
        if (!snapshot.IsComplete ||
            !cpuDemand.HasValue ||
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
            cpuDemand.Value,
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
        string detail)
    {
        var result = new AdaptiveFanProductionResult(_mode, action, executionAuthorized,
            equalFanLevel, rawDemandLevel, _coordinator.Authority, detail);
        if (_mode == AdaptiveFanProductionMode.Automatic)
            Volatile.Write(ref _lastAutomaticResult, result);
        return result;
    }
}
