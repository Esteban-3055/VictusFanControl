using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed partial class MainForm
{
    private Hp8C40WmiFanControlBackend? _wmiFanBackend;
    private bool AutomaticFanJournalPresent => File.Exists(WmiFanGuiGuardianHost.LeasePath) ||
        File.Exists(P15CJournalPath) || File.Exists(WmiFanExperiment.LeasePath);
    private readonly bool _automaticFinalQualificationHardwareTest;
    private readonly string? _automaticFinalQualificationMarkerRoot;
    private readonly object _automaticFinalEvidenceSync = new();

    private int _automaticFinalReadyGate;
    private int _automaticFinalReadySafetyStreak;
    private DateTimeOffset? _automaticFinalLastReadySafetyTimestamp;
    private volatile bool _automaticFinalReadyPublished;
    private volatile bool _automaticFinalCompleted;
    private volatile bool _automaticFinalEverActive;
    private volatile bool _automaticFinalFirmwareReleaseRequested;
    private int _automaticFinalAutomaticModeRequests;
    private int _automaticFinalFirmwareModeRequests;
    private int _automaticFinalManualModeRequests;
    private int _automaticFinalDecisionCount;
    private int _automaticFinalHardwareCommandDecisions;
    private int _automaticFinalHoldDecisions;
    private int _automaticFinalMinObservedLevel = int.MaxValue;
    private int _automaticFinalMaxObservedLevel = int.MinValue;
    private DateTimeOffset? _automaticFinalAutomaticSelectedUtc;
    private DateTimeOffset? _automaticFinalFirstActiveUtc;
    private DateTimeOffset? _automaticFinalLastDecisionUtc;
    private string? _automaticFinalFailureDetail;
    private string? _automaticFinalReadyConfigurationJson;

    private static readonly JsonSerializerOptions AutomaticFinalJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

    private string AutomaticFinalMarkerRoot =>
        _automaticFinalQualificationMarkerRoot ??
        throw new InvalidOperationException(
            "Final Automatic qualification marker root was not configured.");

    private string AutomaticFinalReadyPath =>
        Path.Combine(
            AutomaticFinalMarkerRoot,
            Hp8C40AutomaticFinalQualificationGate.ReadyFileName);

    private string AutomaticFinalEventsPath =>
        Path.Combine(
            AutomaticFinalMarkerRoot,
            Hp8C40AutomaticFinalQualificationGate.EventsFileName);

    private string AutomaticFinalResultPath =>
        Path.Combine(
            AutomaticFinalMarkerRoot,
            Hp8C40AutomaticFinalQualificationGate.ResultFileName);

    private void InitializeAutomaticFinalQualificationEvidence()
    {
        if (!_automaticFinalQualificationHardwareTest)
        {
            return;
        }

        if (!Hp8C40AutomaticFinalQualificationGate.PhysicalExecutionAuthorized ||
            !Hp8C40AutomaticFinalQualificationGate.NormalUserAutomaticRemainsClosed())
        {
            throw new InvalidOperationException(
                "Final Automatic qualification requires its dedicated gate OPEN while normal user Automatic remains CLOSED.");
        }

        Directory.CreateDirectory(
            AutomaticFinalMarkerRoot);

        foreach (var path in new[]
                 {
                     AutomaticFinalReadyPath,
                     AutomaticFinalEventsPath,
                     AutomaticFinalResultPath
                 })
        {
            if (File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Final Automatic qualification refuses to overwrite existing evidence '{path}'.");
            }
        }
    }

    private bool IsAutomaticFinalControlInteractionAuthorized(
        P13ControlInteractionKind kind,
        AdaptiveFanProductionMode? requestedMode,
        int? equalFanLevel)
    {
        // Firmware is an escape action, not a qualification-success assertion.
        // Never require a completed first actuation or an open test to release.
        if (_automaticFinalQualificationHardwareTest && kind == P13ControlInteractionKind.ModeRequest &&
            requestedMode == AdaptiveFanProductionMode.Firmware && !equalFanLevel.HasValue)
        {
            // Set before the synchronous coordinator callback during the real
            // Firmware action; the interaction observer still validates PASS.
            _automaticFinalFirmwareReleaseRequested = true;
            return true;
        }

        if (!_automaticFinalQualificationHardwareTest ||
            _automaticFinalCompleted ||
            !_automaticFinalReadyPublished)
        {
            return false;
        }

        if (kind != P13ControlInteractionKind.ModeRequest ||
            equalFanLevel.HasValue)
        {
            return false;
        }

        if (requestedMode == AdaptiveFanProductionMode.Automatic)
        {
            if (AutomaticPerformanceLimitsRequired &&
                (!_performanceControlSurface.LimitsActive || _performanceControlSurface.AppliedConfiguration is not { CpuEnabled: true, GpuEnabled: true }))
                throw new InvalidOperationException("Aplica CPU y GPU en Rendimiento y espera estado Active / ActiveUnverified antes de Automatic.");
            AppendAutomaticFinalEvent(new { kind = "performance-session-bound", timestampUtc = DateTimeOffset.UtcNow,
                required = AutomaticPerformanceLimitsRequired, configuration = _performanceControlSurface.AppliedConfiguration,
                status = _performanceControlSurface.LastStatus, guardianReportPath = _performanceControlSurface.GuardianReportPath });
            EnsureAutomaticFinalQualificationConfiguration();

            var currentConfiguration =
                _fanProductionController.AutomaticConfiguration ??
                throw new InvalidOperationException(
                    "Automatic configuration disappeared after READY.");

            var currentConfigurationJson =
                FanConfigurationStore.Serialize(
                    currentConfiguration);

            if (string.IsNullOrWhiteSpace(
                    _automaticFinalReadyConfigurationJson) ||
                !string.Equals(
                    currentConfigurationJson,
                    _automaticFinalReadyConfigurationJson,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Fan configuration changed after READY. Restart the qualification so the exact applied curves/tuning remain bound to the evidence.");
            }

            return _automaticFinalAutomaticModeRequests == 0 &&
                   _automaticFinalFirmwareModeRequests == 0 &&
                   _automaticFinalManualModeRequests == 0 &&
                   !_automaticFinalEverActive &&
                   _fanProductionController.Mode == AdaptiveFanProductionMode.Firmware &&
                   _fanCoordinator.Authority == FanAuthority.Firmware;
        }

        return false;
    }

    private void OnAutomaticFinalControlInteraction(
        P13ControlInteractionObservation observation)
    {
        if (!_automaticFinalQualificationHardwareTest ||
            _automaticFinalCompleted)
        {
            return;
        }

        AppendAutomaticFinalEvent(
            new
            {
                kind = "ui-interaction",
                timestampUtc = DateTimeOffset.UtcNow,
                interactionKind = observation.Kind.ToString(),
                requestedMode = observation.RequestedMode?.ToString(),
                observation.EqualFanLevel,
                failure = observation.Failure,
                result = observation.Result is null
                    ? null
                    : new
                    {
                        mode = observation.Result.Mode.ToString(),
                        action = observation.Result.Action.ToString(),
                        observation.Result.ExecutionAuthorized,
                        observation.Result.EqualFanLevel,
                        observation.Result.RawDemandLevel,
                        observation.Result.SmoothedDemandLevel,
                        observation.Result.ActuationDemandLevel,
                        observation.Result.ThermalOverride,
                        authority = observation.Result.Authority.ToString(),
                        observation.Result.Detail
                    }
            });

        try
        {
            if (!string.IsNullOrWhiteSpace(
                    observation.Failure))
            {
                throw new InvalidOperationException(
                    $"Real P13 interaction failed: {observation.Failure}");
            }

            var result =
                observation.Result ??
                throw new InvalidOperationException(
                    "Real P13 interaction did not return a production result.");

            if (observation.Kind != P13ControlInteractionKind.ModeRequest)
            {
                throw new InvalidOperationException(
                    "Final Automatic qualification permits mode requests only; Manual Apply is forbidden.");
            }

            switch (observation.RequestedMode)
            {
                case AdaptiveFanProductionMode.Automatic:
                    _automaticFinalAutomaticModeRequests++;

                    if (!_automaticFinalReadyPublished ||
                        _automaticFinalAutomaticModeRequests != 1 ||
                        _automaticFinalFirmwareModeRequests != 0 ||
                        _automaticFinalManualModeRequests != 0 ||
                        !result.ExecutionAuthorized ||
                        result.Mode != AdaptiveFanProductionMode.Automatic ||
                        result.Action != AdaptiveFanProductionActionKind.HoldFirmware ||
                        result.Authority != FanAuthority.Firmware)
                    {
                        throw new InvalidOperationException(
                            "Automatic selection did not remain the expected no-write Firmware-authority transition.");
                    }

                    _automaticFinalAutomaticSelectedUtc =
                        DateTimeOffset.UtcNow;

                    Ui(() =>
                        AppendEvent(
                            "AUTOMATIC FINAL: real P13 Automatic selected; waiting for fresh Automatic decisions through the final backend path."));
                    return;

                case AdaptiveFanProductionMode.Firmware:
                    _automaticFinalFirmwareModeRequests++;

                    if (_automaticFinalAutomaticModeRequests != 1 ||
                        _automaticFinalFirmwareModeRequests != 1 ||
                        _automaticFinalManualModeRequests != 0 ||
                        !_automaticFinalEverActive ||
                        _automaticFinalDecisionCount < 30 ||
                        _automaticFinalHardwareCommandDecisions < 2 ||
                        !result.ExecutionAuthorized ||
                        result.Mode != AdaptiveFanProductionMode.Firmware ||
                        result.Action != AdaptiveFanProductionActionKind.RestoreFirmware ||
                        result.Authority != FanAuthority.Firmware)
                    {
                        throw new InvalidOperationException(
                            "Firmware return does not satisfy the normal Automatic qualification sequence or evidence minimums.");
                    }

                    var release = _wmiFanBackend?.ReleaseEvidence;
                    if (release is not { ReleaseRequestAccepted: true, LegacyDefaultRequestAccepted: true,
                        GuardianLeaseRetired: true, IndependentFirmwareOwnershipVerified: false } || AutomaticFanJournalPresent)
                        throw new InvalidOperationException("WMI Firmware return lacks accepted release/default requests and retired guardian lease.");
                    CompleteAutomaticFinalQualification(release);
                    return;

                case AdaptiveFanProductionMode.Manual:
                    _automaticFinalManualModeRequests++;
                    throw new InvalidOperationException(
                        "Manual was requested during the Automatic-only qualification.");

                default:
                    throw new InvalidOperationException(
                        "Unknown mode request during final Automatic qualification.");
            }
        }
        catch (Exception ex)
        {
            FailAutomaticFinalQualification(
                $"GUI interaction sequence failed closed: {ex.Message}");
        }
    }

    private Task TryPublishAutomaticFinalReadyAsync()
    {
        if (!_automaticFinalQualificationHardwareTest ||
            _automaticFinalCompleted ||
            _automaticFinalReadyPublished ||
            Interlocked.CompareExchange(
                ref _automaticFinalReadyGate,
                1,
                0) != 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (!Hp8C40AutomaticFinalQualificationGate.IsAuthorizedForTarget(
                    _targetProfile?.Id))
            {
                throw new InvalidOperationException(
                    "Dedicated final Automatic gate is not authorized for this exact target.");
            }

            if (_fanProductionController.ManualExecutionAuthorized ||
                !_fanProductionController.AutomaticExecutionAuthorized)
            {
                throw new InvalidOperationException(
                    "Final Automatic qualification isolation is invalid: Manual must be closed and dedicated Automatic must be open.");
            }

            if (_worker.StateMachine.State != SystemState.Healthy ||
                _fanCoordinator.Authority != FanAuthority.Firmware ||
                _lastSnapshot is null)
            {
                _automaticFinalReadySafetyStreak = 0;
                _automaticFinalLastReadySafetyTimestamp = null;
                return Task.CompletedTask;
            }

            if (!_fanCoordinator.BackendCanWrite)
            {
                throw new InvalidOperationException(
                    "The final fan backend is not writable; qualification cannot exercise the product path.");
            }

            EnsureAutomaticFinalQualificationConfiguration();
            EnsureAutomaticFinalReadyEnvelope(
                _lastSnapshot);

            var safety =
                EvaluateControlSafety(
                    _hardwareIdentity,
                    _worker.StateMachine.State,
                    _lastSnapshot,
                    DateTimeOffset.UtcNow,
                    fanWritePathPresent:
                        _fanCoordinator.BackendCanWrite);

            if (!safety.CustomControlPermitted ||
                !safety.SnapshotTimestamp.HasValue)
            {
                _automaticFinalReadySafetyStreak = 0;
                _automaticFinalLastReadySafetyTimestamp = null;
                return Task.CompletedTask;
            }

            var timestamp =
                safety.SnapshotTimestamp.Value;

            if (_automaticFinalLastReadySafetyTimestamp.HasValue &&
                timestamp <=
                _automaticFinalLastReadySafetyTimestamp.Value)
            {
                return Task.CompletedTask;
            }

            _automaticFinalLastReadySafetyTimestamp =
                timestamp;

            if (AutomaticFanJournalPresent)
            {
                throw new InvalidOperationException(
                    "Final Automatic qualification cannot publish READY while a durable fan watchdog journal exists.");
            }

            _automaticFinalReadySafetyStreak++;

            if (_automaticFinalReadySafetyStreak <
                Hp8C40AutomaticFinalQualificationGate.RequiredHealthyPreWriteSamples)
            {
                return Task.CompletedTask;
            }

            using var process =
                Process.GetCurrentProcess();

            var configuration =
                _fanProductionController.AutomaticConfiguration ??
                throw new InvalidOperationException(
                    "Automatic configuration disappeared before READY.");

            _automaticFinalReadyConfigurationJson =
                FanConfigurationStore.Serialize(
                    configuration);

            WriteAutomaticFinalJson(
                AutomaticFinalReadyPath,
                new
                {
                    schemaVersion = 1,
                    gate = "HP-8C40-AUTOMATIC-WMI-NORMAL",
                    result = "READY",
                    timestampUtc = DateTimeOffset.UtcNow,
                    processId = Environment.ProcessId,
                    processStartUtcTicks =
                        process.StartTime.ToUniversalTime().Ticks.ToString(
                            CultureInfo.InvariantCulture),
                    targetProfileId = _targetProfile?.Id,
                    backend = _fanCoordinator.BackendName,
                    backendCanWrite = _fanCoordinator.BackendCanWrite,
                    directEcProhibited = WmiOnlyInvestigationPolicy.Enabled,
                    fanGuardianReportPath = _wmiFanBackend?.GuardianReportPath,
                    authority = _fanCoordinator.Authority.ToString(),
                    runtimeState = _worker.StateMachine.State.ToString(),
                    normalUserAutomaticAuthorized =
                        Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized,
                    dedicatedAutomaticAuthorized =
                        _fanProductionController.AutomaticExecutionAuthorized,
                    manualAuthorized =
                        _fanProductionController.ManualExecutionAuthorized,
                    healthySamples =
                        _automaticFinalReadySafetyStreak,
                    journalPresent =
                        AutomaticFanJournalPresent,
                    configuration
                });

            _automaticFinalReadyPublished = true;

            Ui(() =>
                AppendEvent(
                    "AUTOMATIC FINAL READY: exact target, final writable backend, Firmware authority, three fresh healthy samples, qualification profile and journal absence verified. Click Automatic once."));
        }
        catch (Exception ex)
        {
            FailAutomaticFinalQualification(
                $"READY preflight failed closed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(
                ref _automaticFinalReadyGate,
                0);
        }

        return Task.CompletedTask;
    }

    private static void EnsureAutomaticFinalReadyEnvelope(
        TelemetrySnapshot snapshot)
    {
        if (!snapshot.IsComplete ||
            !snapshot.CpuControlTemperatureC.HasValue ||
            !snapshot.CpuPackagePowerW.HasValue ||
            !snapshot.GpuTemperatureC.HasValue ||
            !snapshot.GpuPowerW.HasValue)
        {
            throw new InvalidOperationException(
                "Final Automatic qualification requires complete CPU/GPU temperature and power telemetry before READY.");
        }

        var demandTemperature =
            CpuDemandTemperature.Select(
                snapshot,
                CpuDemandTemperatureSource.HottestPerformanceCoresAverage,
                hottestPerformanceCoreCount: 3);

        if (!demandTemperature.HasValue)
        {
            throw new InvalidOperationException(
                "Final Automatic qualification requires complete typed CPU core telemetry for the hottest-3-P-Core demand source.");
        }

        if (snapshot.CpuControlTemperatureC.Value >
                Hp8C40AutomaticFinalQualificationGate.MaximumCpuPhysicalC ||
            snapshot.GpuTemperatureC.Value >
                Hp8C40AutomaticFinalQualificationGate.MaximumGpuPhysicalC ||
            snapshot.CpuPackagePowerW.Value >
                Hp8C40AutomaticFinalQualificationGate.MaximumCpuPackagePowerW ||
            snapshot.GpuPowerW.Value >
                Hp8C40AutomaticFinalQualificationGate.MaximumGpuPowerW)
        {
            throw new InvalidOperationException(
                $"Final Automatic preflight envelope refused READY: " +
                $"CPU={snapshot.CpuControlTemperatureC.Value:0.0}C/" +
                $"{snapshot.CpuPackagePowerW.Value:0.0}W, " +
                $"GPU={snapshot.GpuTemperatureC.Value:0.0}C/" +
                $"{snapshot.GpuPowerW.Value:0.0}W.");
        }
    }

    private void EnsureAutomaticFinalQualificationConfiguration()
    {
        var configuration =
            _fanProductionController.AutomaticConfiguration ??
            throw new InvalidOperationException(
                "Final Automatic qualification requires persisted fan configuration.");

        var tuning =
            configuration.Tuning;

        static bool Same(
            double left,
            double right) =>
            Math.Abs(
                left -
                right) <
            0.0001;

        if (tuning.CpuTemperatureSource !=
                CpuDemandTemperatureSource.HottestPerformanceCoresAverage ||
            tuning.HottestPerformanceCoreCount != 3 ||
            tuning.MinimumLevel != 30 ||
            tuning.MaximumLevel != 50 ||
            tuning.NormalMaximumUpStepLevels != 1 ||
            tuning.MaximumDownStepLevels != 1 ||
            tuning.NormalPollingDelayMilliseconds != 1000 ||
            tuning.RememberThermalDemand ||
            !tuning.AdaptiveDescentEnabled ||
            !Same(tuning.RiseTimeConstantSeconds, 8) ||
            !Same(tuning.IncreaseConfirmationSeconds, 3) ||
            !Same(tuning.ShortLoadFallTimeConstantSeconds, 6) ||
            !Same(tuning.ShortLoadDecreaseConfirmationSeconds, 4) ||
            !Same(tuning.FallTimeConstantSeconds, 20) ||
            !Same(tuning.DecreaseConfirmationSeconds, 16) ||
            !Same(tuning.SustainedLoadSeconds, 1200) ||
            !Same(tuning.LoadThresholdPercent, 50) ||
            !Same(tuning.CpuLoadPowerThresholdW, 25) ||
            !Same(tuning.GpuLoadPowerThresholdW, 40) ||
            !Same(tuning.LoadPauseToleranceSeconds, 30) ||
            !Same(tuning.SustainedLoadCooldownSeconds, 120) ||
            !Same(tuning.CpuThermalOverrideC, 85) ||
            !Same(tuning.GpuThermalOverrideC, 78))
        {
            throw new InvalidOperationException(
                "Persisted qualification profile mismatch. Required: hottest 3 P-Cores, 30..50, normal step +1/-1, 1000 ms polling, no thermal-demand memory, adaptive descent, rise 8s/3s, short descent 6s/4s, sustained descent 20s/16s, 1200s load qualification, thresholds 50%/25W/40W, pause 30s, cooldown 120s and thermal overrides 85C/78C.");
        }

        _ =
            configuration.BuildPolicy();
    }

    private void RecordAutomaticFinalAuthorityChange(FanAuthorityChangedEventArgs change)
    {
        if (!_automaticFinalQualificationHardwareTest || _automaticFinalCompleted)
            return;

        AppendAutomaticFinalEvent(new
        {
            kind = "fan-authority-transition",
            timestampUtc = change.Timestamp,
            previous = change.Previous.ToString(),
            current = change.Current.ToString(),
            reason = change.Reason,
            supervisedFirmwareRelease = _automaticFinalFirmwareReleaseRequested
        });

        // This callback runs before recovery starts, including transitions
        // between policy decisions. Failure cleanup is queued, never awaited
        // from the coordinator's operation gate.
        if (_automaticFinalEverActive && !_automaticFinalFirmwareReleaseRequested &&
            change.Current != FanAuthority.Custom)
            FailAutomaticFinalQualification(
                $"Automatic lost fan authority before the supervised Firmware button: " +
                $"{change.Previous} -> {change.Current}; {change.Reason}");
    }

    private void RecordAutomaticFinalDecision(
        TelemetrySnapshot snapshot,
        AdaptiveFanProductionResult result)
    {
        if (!_automaticFinalQualificationHardwareTest ||
            _automaticFinalCompleted)
        {
            return;
        }

        string? failure =
            null;

        lock (_automaticFinalEvidenceSync)
        {
            if (_automaticFinalCompleted)
            {
                return;
            }

            _automaticFinalDecisionCount++;
            _automaticFinalLastDecisionUtc =
                DateTimeOffset.UtcNow;

            if (result.EqualFanLevel is int level)
            {
                _automaticFinalMinObservedLevel =
                    Math.Min(
                        _automaticFinalMinObservedLevel,
                        level);
                _automaticFinalMaxObservedLevel =
                    Math.Max(
                        _automaticFinalMaxObservedLevel,
                        level);
            }

            if (result.Action is
                AdaptiveFanProductionActionKind.EnterCustomAndApply or
                AdaptiveFanProductionActionKind.ApplyChangedLevel)
            {
                _automaticFinalHardwareCommandDecisions++;
            }

            if (result.Action ==
                AdaptiveFanProductionActionKind.HoldCustom)
            {
                _automaticFinalHoldDecisions++;
            }

            if (result.ExecutionAuthorized &&
                result.Authority ==
                    FanAuthority.Custom &&
                result.Action is
                    AdaptiveFanProductionActionKind.EnterCustomAndApply or
                    AdaptiveFanProductionActionKind.ApplyChangedLevel or
                    AdaptiveFanProductionActionKind.HoldCustom)
            {
                if (!_automaticFinalEverActive)
                {
                    _automaticFinalFirstActiveUtc =
                        DateTimeOffset.UtcNow;
                }

                _automaticFinalEverActive = true;
            }

            AppendAutomaticFinalEventLocked(
                new
                {
                    kind = "automatic-decision",
                    timestampUtc = DateTimeOffset.UtcNow,
                    snapshotUtc = snapshot.Timestamp,
                    mode = result.Mode.ToString(),
                    action = result.Action.ToString(),
                    result.ExecutionAuthorized,
                    result.EqualFanLevel,
                    result.RawDemandLevel,
                    result.SmoothedDemandLevel,
                    result.ActuationDemandLevel,
                    result.ThermalOverride,
                    authority = result.Authority.ToString(),
                    result.Detail,
                    performanceLimitsRequired = AutomaticPerformanceLimitsRequired,
                    performanceStatus = _performanceControlSurface.LastStatus
                });

            if (!result.ExecutionAuthorized ||
                result.Action ==
                    AdaptiveFanProductionActionKind.Blocked)
            {
                failure =
                    $"Automatic decision lost execution authorization: action={result.Action}; detail={result.Detail}";
            }
            else if (_automaticFinalEverActive &&
                     !_automaticFinalFirmwareReleaseRequested &&
                     (result.Authority != FanAuthority.Custom ||
                      result.Action is AdaptiveFanProductionActionKind.RestoreFirmware or
                          AdaptiveFanProductionActionKind.HoldFirmware))
            {
                failure =
                    $"Automatic returned to Firmware before the supervised Firmware button: {result.Detail}";
            }
        }

        if (failure is null && AutomaticPerformanceLimitsRequired && !_performanceControlSurface.LimitsActive)
            failure = "CPU/GPU performance session lost active status during Automatic qualification.";
        if (failure is not null)
        {
            FailAutomaticFinalQualification(failure);
        }
    }

    private void CompleteAutomaticFinalQualification(FanWmiReleaseEvidence release)
    {
        lock (_automaticFinalEvidenceSync)
        {
            if (_automaticFinalCompleted)
            {
                return;
            }

            _automaticFinalCompleted = true;

            using var process =
                Process.GetCurrentProcess();

            WriteAutomaticFinalJsonLocked(
                AutomaticFinalResultPath,
                new
                {
                    schemaVersion = 1,
                    gate = "HP-8C40-AUTOMATIC-WMI-NORMAL",
                    result = "PASS",
                    timestampUtc = DateTimeOffset.UtcNow,
                    processId = Environment.ProcessId,
                    processStartUtcTicks =
                        process.StartTime.ToUniversalTime().Ticks.ToString(
                            CultureInfo.InvariantCulture),
                    targetProfileId = _targetProfile?.Id,
                    backend = _fanCoordinator.BackendName,
                    automaticModeRequests =
                        _automaticFinalAutomaticModeRequests,
                    firmwareModeRequests =
                        _automaticFinalFirmwareModeRequests,
                    manualModeRequests =
                        _automaticFinalManualModeRequests,
                    decisions =
                        _automaticFinalDecisionCount,
                    hardwareCommandDecisions =
                        _automaticFinalHardwareCommandDecisions,
                    holdDecisions =
                        _automaticFinalHoldDecisions,
                    minObservedLevel =
                        _automaticFinalMinObservedLevel == int.MaxValue
                            ? (int?)null
                            : _automaticFinalMinObservedLevel,
                    maxObservedLevel =
                        _automaticFinalMaxObservedLevel == int.MinValue
                            ? (int?)null
                            : _automaticFinalMaxObservedLevel,
                    automaticSelectedUtc =
                        _automaticFinalAutomaticSelectedUtc,
                    firstActiveUtc =
                        _automaticFinalFirstActiveUtc,
                    lastDecisionUtc =
                        _automaticFinalLastDecisionUtc,
                    finalMode =
                        _fanProductionController.Mode.ToString(),
                    finalAuthority =
                        _fanCoordinator.Authority.ToString(),
                    directEcProhibited = true,
                    deniedEcAccesses = WmiOnlyInvestigationPolicy.DeniedEcAccesses,
                    release.ReleaseRequestAccepted,
                    release.LegacyDefaultRequestAccepted,
                    release.GuardianLeaseRetired,
                    release.IndependentFirmwareOwnershipVerified,
                    fanGuardianReportPath = release.ReportPath,
                    journalPresentAfterRestore =
                        AutomaticFanJournalPresent,

                    normalUserAutomaticAuthorized =
                        Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized
                });
        }

        Environment.ExitCode = 0;

        Ui(() =>
        {
            AppendEvent(
                "AUTOMATIC FINAL RESULT: PASS normal route. Real P13 Automatic produced live final-backend decisions and real Firmware completed accepted WMI release/default requests with retired guardian lease; hardware ownership unverified.");
            _allowExit = true;
            Close();
        });
    }

    private void FailAutomaticFinalQualification(
        string detail)
    {
        lock (_automaticFinalEvidenceSync)
        {
            if (_automaticFinalCompleted)
            {
                return;
            }

            _automaticFinalCompleted = true;
            _automaticFinalFailureDetail = detail;

            WriteAutomaticFinalJsonLocked(
                AutomaticFinalResultPath,
                new
                {
                    schemaVersion = 1,
                    gate = "HP-8C40-AUTOMATIC-WMI-NORMAL",
                    result = "FAIL_CLOSED",
                    timestampUtc = DateTimeOffset.UtcNow,
                    failure = detail,
                    targetProfileId = _targetProfile?.Id,
                    backend = _fanCoordinator.BackendName,
                    mode = _fanProductionController.Mode.ToString(),
                    authority = _fanCoordinator.Authority.ToString(),
                    automaticModeRequests =
                        _automaticFinalAutomaticModeRequests,
                    firmwareModeRequests =
                        _automaticFinalFirmwareModeRequests,
                    manualModeRequests =
                        _automaticFinalManualModeRequests,
                    decisions =
                        _automaticFinalDecisionCount,
                    hardwareCommandDecisions =
                        _automaticFinalHardwareCommandDecisions,
                    everActive =
                        _automaticFinalEverActive,
                    journalPresent =
                        AutomaticFanJournalPresent
                });
        }

        Environment.ExitCode = 170;
        AppLog.Write(
            $"AUTOMATIC FINAL FAIL_CLOSED: {detail}");

        // A qualification failure is not permission to abandon Custom authority.
        // Perform the same production-controller Firmware transition before closing.
        _ = Task.Run(
            async () =>
            {
                string cleanup;
                try
                {
                    var release =
                        await _fanProductionController.SetModeAsync(
                                AdaptiveFanProductionMode.Firmware,
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    cleanup =
                        $"fail-cleanup action={release.Action}; authority={release.Authority}; detail={release.Detail}";

                    AppendAutomaticFinalEvent(
                        new
                        {
                            kind = "fail-cleanup",
                            timestampUtc = DateTimeOffset.UtcNow,
                            action = release.Action.ToString(),
                            authority = release.Authority.ToString(),
                            release.Detail,
                            journalPresent =
                                AutomaticFanJournalPresent
                        });
                }
                catch (Exception ex)
                {
                    cleanup =
                        "fail-cleanup exception=" +
                        ex.Message;
                    AppLog.Write(
                        $"AUTOMATIC FINAL fail-cleanup exception: {ex}");
                }

                Ui(() =>
                {
                    AppendEvent(
                        $"AUTOMATIC FINAL FAIL_CLOSED: {detail}. {cleanup}");
                    _allowExit = true;
                    Enabled = false;
                    Close();
                });
            });
    }

    private void AppendAutomaticFinalEvent(
        object value)
    {
        lock (_automaticFinalEvidenceSync)
        {
            AppendAutomaticFinalEventLocked(
                value);
        }
    }

    private void AppendAutomaticFinalEventLocked(
        object value)
    {
        Directory.CreateDirectory(
            AutomaticFinalMarkerRoot);

        var json =
            JsonSerializer.Serialize(
                value,
                AutomaticFinalJsonOptions) +
            Environment.NewLine;

        var bytes =
            Encoding.UTF8.GetBytes(
                json);

        using var stream =
            new FileStream(
                AutomaticFinalEventsPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);

        stream.Write(
            bytes,
            0,
            bytes.Length);
        stream.Flush(
            flushToDisk: true);
    }

    private void WriteAutomaticFinalJson(
        string path,
        object value)
    {
        lock (_automaticFinalEvidenceSync)
        {
            WriteAutomaticFinalJsonLocked(
                path,
                value);
        }
    }

    private static void WriteAutomaticFinalJsonLocked(
        string path,
        object value)
    {
        var directory =
            Path.GetDirectoryName(
                path);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        var temporary =
            path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            var bytes =
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        value,
                        AutomaticFinalJsonOptions));

            using (var stream =
                   new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(
                    bytes,
                    0,
                    bytes.Length);
                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(
                    temporary))
            {
                File.Delete(
                    temporary);
            }
        }
    }
}
