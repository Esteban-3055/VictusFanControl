namespace VictusFanControl.Performance;

internal readonly record struct CpuPowerPreset(
    bool Enabled,
    double Pl1Watts,
    double Pl2Watts)
{
    internal static CpuPowerPreset Disabled =>
        new(
            Enabled: false,
            Pl1Watts: 0,
            Pl2Watts: 0);

    internal CpuPowerLimitRequest ToRequest()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "A disabled CPU power preset has no active request.");
        }

        return new CpuPowerLimitRequest(
            Pl1Watts,
            Pl2Watts);
    }
}

internal readonly record struct CpuPowerPresetSet(
    CpuPowerPreset Ac,
    CpuPowerPreset Battery)
{
    internal static CpuPowerPresetSet Disabled =>
        new(
            CpuPowerPreset.Disabled,
            CpuPowerPreset.Disabled);
}

internal readonly record struct CpuPowerPresetSelection(
    PerformancePowerSourceKind Source,
    PerformancePresetSlot? Slot,
    bool SourceKnown,
    bool Enabled,
    CpuPowerLimitRequest? Request,
    string Status);

/// <summary>
/// Pure source-to-preset selector. It owns no power notifications, timers,
/// journal, limiter state or hardware writes.
///
/// Runtime authority is intentionally separate: resolving a Battery preset
/// does not by itself authorize applying it at startup, after guardian restart,
/// or after a Yielded external-writer episode.
/// </summary>
internal sealed class CpuPowerPresetPolicy
{
    private readonly CpuPowerPresetSet _presets;

    internal CpuPowerPresetPolicy(
        CpuPowerPresetSet presets)
    {
        ValidatePreset(
            PerformancePresetSlot.Ac,
            presets.Ac);

        ValidatePreset(
            PerformancePresetSlot.Battery,
            presets.Battery);

        _presets = presets;
    }

    internal CpuPowerPresetSet Presets =>
        _presets;

    internal CpuPowerPresetSelection Resolve(
        PerformancePowerSourceKind source) =>
        source switch
        {
            PerformancePowerSourceKind.Ac =>
                ResolveKnown(
                    source,
                    PerformancePresetSlot.Ac,
                    _presets.Ac),

            PerformancePowerSourceKind.Battery =>
                ResolveKnown(
                    source,
                    PerformancePresetSlot.Battery,
                    _presets.Battery),

            PerformancePowerSourceKind.Unknown =>
                new CpuPowerPresetSelection(
                    Source: source,
                    Slot: null,
                    SourceKnown: false,
                    Enabled: false,
                    Request: null,
                    Status:
                        "CPU_POWER_SOURCE_UNKNOWN__NO_PRESET_AUTHORITY"),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(source),
                    source,
                    "Unknown CPU power source kind.")
        };

    private static CpuPowerPresetSelection ResolveKnown(
        PerformancePowerSourceKind source,
        PerformancePresetSlot slot,
        CpuPowerPreset preset)
    {
        if (!preset.Enabled)
        {
            return new CpuPowerPresetSelection(
                Source: source,
                Slot: slot,
                SourceKnown: true,
                Enabled: false,
                Request: null,
                Status:
                    $"CPU_POWER_PRESET_{slot.ToString().ToUpperInvariant()}_DISABLED");
        }

        return new CpuPowerPresetSelection(
            Source: source,
            Slot: slot,
            SourceKnown: true,
            Enabled: true,
            Request: preset.ToRequest(),
            Status:
                $"CPU_POWER_PRESET_{slot.ToString().ToUpperInvariant()}_SELECTED");
    }

    private static void ValidatePreset(
        PerformancePresetSlot slot,
        CpuPowerPreset preset)
    {
        if (!preset.Enabled)
            return;

        if (!double.IsFinite(preset.Pl1Watts) ||
            !double.IsFinite(preset.Pl2Watts) ||
            preset.Pl1Watts < 10 ||
            preset.Pl2Watts <
                preset.Pl1Watts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset),
                $"Enabled {slot} CPU power preset must have finite PL1/PL2, PL1 >= 10 W and PL2 >= PL1.");
        }
    }
}


internal enum CpuPowerPresetTransitionDisposition
{
    SourceUnknownReleased,
    SourceUnknownNoOwnership,
    EnabledPresetSwitched,
    EnabledPresetUnchanged,
    DisabledPresetReleased,
    NoActiveOwnership,
    BlockedByLimiterState,
    Failed
}

internal readonly record struct CpuPowerPresetTransitionResult(
    CpuPowerPresetTransitionDisposition Disposition,
    PerformancePowerSourceKind Source,
    PerformancePresetSlot? Slot,
    bool Succeeded,
    string Status);

/// <summary>
/// Executes only a confirmed AC/DC source transition against an already
/// established CPU power-limit session.
///
/// This helper intentionally owns no Windows power notifications and has no
/// startup authority. An enabled preset can switch only an already Active
/// VFC-owned session. A disabled/unknown source gives up preset authority via
/// the existing conditional Release path when that path is safe to invoke.
/// </summary>
internal sealed class CpuPowerPresetTransitionController
{
    private readonly CpuPowerPresetPolicy _policy;
    private readonly CpuPowerLimiter _limiter;

    internal CpuPowerPresetTransitionController(
        CpuPowerPresetPolicy policy,
        CpuPowerLimiter limiter)
    {
        _policy =
            policy ??
            throw new ArgumentNullException(nameof(policy));

        _limiter =
            limiter ??
            throw new ArgumentNullException(nameof(limiter));
    }

    internal CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source)
    {
        var selection =
            _policy.Resolve(source);

        if (!selection.SourceKnown)
        {
            return ReleaseAuthorityOrNoop(
                selection,
                CpuPowerPresetTransitionDisposition.SourceUnknownReleased,
                CpuPowerPresetTransitionDisposition.SourceUnknownNoOwnership,
                "CPU_POWER_SOURCE_UNKNOWN__PRESET_AUTHORITY_RELEASED");
        }

        if (!selection.Enabled)
        {
            return ReleaseAuthorityOrNoop(
                selection,
                CpuPowerPresetTransitionDisposition.DisabledPresetReleased,
                CpuPowerPresetTransitionDisposition.NoActiveOwnership,
                $"CPU_POWER_PRESET_{selection.Slot!.Value.ToString().ToUpperInvariant()}_DISABLED__AUTHORITY_RELEASED");
        }

        if (!selection.Request.HasValue)
        {
            return Result(
                selection,
                CpuPowerPresetTransitionDisposition.Failed,
                succeeded: false,
                "CPU_POWER_PRESET_ENABLED_WITHOUT_REQUEST");
        }

        if (_limiter.State !=
            CpuPowerLimiterState.Active)
        {
            return Result(
                selection,
                _limiter.State ==
                    CpuPowerLimiterState.Disabled
                    ? CpuPowerPresetTransitionDisposition.NoActiveOwnership
                    : CpuPowerPresetTransitionDisposition.BlockedByLimiterState,
                succeeded: false,
                $"CPU_POWER_PRESET_SOURCE_CHANGE_NO_SWITCH_AUTHORITY__STATE_{_limiter.State}");
        }

        var before =
            _limiter.AppliedRaw;

        if (!_limiter.SwitchOwnedPreset(
                selection.Request.Value))
        {
            return Result(
                selection,
                _limiter.State is
                    CpuPowerLimiterState.Contested or
                    CpuPowerLimiterState.Yielded or
                    CpuPowerLimiterState.ReacquiredPendingStability
                    ? CpuPowerPresetTransitionDisposition.BlockedByLimiterState
                    : CpuPowerPresetTransitionDisposition.Failed,
                succeeded: false,
                _limiter.LastError ??
                "CPU_POWER_PRESET_SWITCH_FAILED");
        }

        var changed =
            before !=
            _limiter.AppliedRaw;

        return Result(
            selection,
            changed
                ? CpuPowerPresetTransitionDisposition.EnabledPresetSwitched
                : CpuPowerPresetTransitionDisposition.EnabledPresetUnchanged,
            succeeded: true,
            changed
                ? "CPU_POWER_PRESET_OWNED_TO_OWNED_SWITCHED"
                : "CPU_POWER_PRESET_ALREADY_ACTIVE");
    }

    private CpuPowerPresetTransitionResult ReleaseAuthorityOrNoop(
        CpuPowerPresetSelection selection,
        CpuPowerPresetTransitionDisposition releasedDisposition,
        CpuPowerPresetTransitionDisposition noOwnershipDisposition,
        string releasedStatus)
    {
        if (_limiter.State is
            CpuPowerLimiterState.Disabled or
            CpuPowerLimiterState.Unsupported)
        {
            return Result(
                selection,
                noOwnershipDisposition,
                succeeded: true,
                "CPU_POWER_PRESET_NO_ACTIVE_OWNERSHIP");
        }

        if (_limiter.State is not (
                CpuPowerLimiterState.Active or
                CpuPowerLimiterState.Contested or
                CpuPowerLimiterState.ReacquiredPendingStability or
                CpuPowerLimiterState.Yielded))
        {
            return Result(
                selection,
                CpuPowerPresetTransitionDisposition.BlockedByLimiterState,
                succeeded: false,
                $"CPU_POWER_PRESET_RELEASE_BLOCKED__STATE_{_limiter.State}");
        }

        if (!_limiter.Release())
        {
            return Result(
                selection,
                CpuPowerPresetTransitionDisposition.Failed,
                succeeded: false,
                _limiter.LastError ??
                "CPU_POWER_PRESET_CONDITIONAL_RELEASE_FAILED");
        }

        return Result(
            selection,
            releasedDisposition,
            succeeded: true,
            releasedStatus);
    }

    private static CpuPowerPresetTransitionResult Result(
        CpuPowerPresetSelection selection,
        CpuPowerPresetTransitionDisposition disposition,
        bool succeeded,
        string status) =>
        new(
            disposition,
            selection.Source,
            selection.Slot,
            succeeded,
            status);
}
