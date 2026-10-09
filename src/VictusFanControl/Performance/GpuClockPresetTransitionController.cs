namespace VictusFanControl.Performance;

internal enum GpuClockPresetTransitionDisposition
{
    EnabledPresetSwitched,
    EnabledPresetUnchanged,
    DisabledPresetReleased,
    UnknownSourceReleased,
    NoActiveSession,
    BlockedBySessionState,
    Failed
}

internal readonly record struct GpuClockPresetTransitionResult(
    GpuClockPresetTransitionDisposition Disposition,
    PerformancePowerSourceKind Source,
    PerformancePresetSlot? Slot,
    bool Succeeded,
    GpuClockSessionState SessionState,
    string Status);

/// <summary>
/// Connects a confirmed AC/Battery source change to an already-established GPU
/// clock session.
///
/// This dispatcher deliberately has no startup Apply authority. If the session
/// is Disabled, merely observing AC or Battery performs zero writes. An
/// explicit user/guardian action must establish the first session separately.
///
/// While ActiveUnverified, enabled AC/Battery presets use one direct Set via
/// SwitchPreset. Disabled/Unknown destinations use the normal in-process
/// Release path and therefore one journaled Reset.
/// </summary>
internal sealed class GpuClockPresetTransitionController :
    IGpuClockSourceTransitionSink
{
    private readonly GpuClockPresetPolicy _policy;
    private readonly GpuClockSessionController _session;

    internal GpuClockPresetTransitionController(
        GpuClockPresetPolicy policy,
        GpuClockSessionController session)
    {
        _policy =
            policy ??
            throw new ArgumentNullException(
                nameof(policy));

        _session =
            session ??
            throw new ArgumentNullException(
                nameof(session));
    }

    GpuClockPresetTransitionResult IGpuClockSourceTransitionSink.HandleConfirmedSourceChange(
        PerformancePowerSourceKind source) =>
        HandleConfirmedSourceChange(source);

    internal GpuClockPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source)
    {
        var selection =
            _policy.Resolve(source);

        if (!selection.SourceKnown)
        {
            return ReleaseAuthorityOrNoop(
                source,
                slot: null,
                GpuClockPresetTransitionDisposition.UnknownSourceReleased,
                "GPU_CLOCK_SOURCE_UNKNOWN");
        }

        if (!selection.Enabled)
        {
            return ReleaseAuthorityOrNoop(
                source,
                selection.Slot,
                GpuClockPresetTransitionDisposition.DisabledPresetReleased,
                "GPU_CLOCK_DESTINATION_PRESET_DISABLED");
        }

        if (!selection.Request.HasValue)
        {
            return Result(
                GpuClockPresetTransitionDisposition.Failed,
                source,
                selection.Slot,
                succeeded: false,
                "GPU_CLOCK_ENABLED_PRESET_WITHOUT_REQUEST");
        }

        if (_session.State ==
            GpuClockSessionState.Disabled)
        {
            // Source detection alone must never become startup authority.
            return Result(
                GpuClockPresetTransitionDisposition.NoActiveSession,
                source,
                selection.Slot,
                succeeded: true,
                "GPU_CLOCK_SOURCE_CHANGE_NO_ACTIVE_SESSION__NO_STARTUP_APPLY");
        }

        if (_session.State !=
            GpuClockSessionState.ActiveUnverified)
        {
            return Result(
                GpuClockPresetTransitionDisposition.BlockedBySessionState,
                source,
                selection.Slot,
                succeeded: false,
                "GPU_CLOCK_SOURCE_CHANGE_BLOCKED_BY_SESSION_STATE");
        }

        var requested =
            selection.Request.Value;

        var unchanged =
            _session.CurrentRequest ==
            requested;

        var switched =
            _session.SwitchPreset(
                requested);

        if (!switched)
        {
            return Result(
                GpuClockPresetTransitionDisposition.Failed,
                source,
                selection.Slot,
                succeeded: false,
                _session.LastStatus ??
                "GPU_CLOCK_PRESET_SWITCH_FAILED");
        }

        return Result(
            unchanged
                ? GpuClockPresetTransitionDisposition.EnabledPresetUnchanged
                : GpuClockPresetTransitionDisposition.EnabledPresetSwitched,
            source,
            selection.Slot,
            succeeded: true,
            unchanged
                ? "GPU_CLOCK_PRESET_ALREADY_SELECTED__NO_WRITE"
                : "GPU_CLOCK_PRESET_SWITCHED");
    }

    private GpuClockPresetTransitionResult ReleaseAuthorityOrNoop(
        PerformancePowerSourceKind source,
        PerformancePresetSlot? slot,
        GpuClockPresetTransitionDisposition successDisposition,
        string reason)
    {
        if (_session.State ==
            GpuClockSessionState.Disabled)
        {
            return Result(
                GpuClockPresetTransitionDisposition.NoActiveSession,
                source,
                slot,
                succeeded: true,
                reason +
                "__NO_ACTIVE_SESSION");
        }

        if (_session.State !=
            GpuClockSessionState.ActiveUnverified)
        {
            return Result(
                GpuClockPresetTransitionDisposition.BlockedBySessionState,
                source,
                slot,
                succeeded: false,
                reason +
                "__BLOCKED_BY_SESSION_STATE");
        }

        if (!_session.Release())
        {
            return Result(
                GpuClockPresetTransitionDisposition.Failed,
                source,
                slot,
                succeeded: false,
                _session.LastStatus ??
                reason +
                "__RELEASE_FAILED");
        }

        return Result(
            successDisposition,
            source,
            slot,
            succeeded: true,
            reason +
            "__RELEASED_TO_NVIDIA_DEFAULT");
    }

    private GpuClockPresetTransitionResult Result(
        GpuClockPresetTransitionDisposition disposition,
        PerformancePowerSourceKind source,
        PerformancePresetSlot? slot,
        bool succeeded,
        string status) =>
        new(
            disposition,
            source,
            slot,
            succeeded,
            _session.State,
            status);
}
