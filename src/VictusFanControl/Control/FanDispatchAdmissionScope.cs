using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Control;

/// <summary>
/// Per-command admission flowing through async/native preparation. A scoped
/// Automatic command rechecks admission after queueing and at the native setter;
/// telemetry and firmware-release requests remain available during recovery.
/// </summary>
internal sealed class FanDispatchAdmissionScope : IDisposable
{
    private static readonly AsyncLocal<Action?> Current = new();
    private readonly Action? _previous;

    internal FanDispatchAdmissionScope(Action ensureAllowed)
    {
        _previous = Current.Value;
        Current.Value = ensureAllowed;
    }

    internal static void EnsureAllowed() => Current.Value?.Invoke();

    internal static void EnsureNativeRequestAllowed(HpBiosRequest request)
    {
        if (request.Command == Hp8C40BiosFanControl.DefaultCommand &&
            request.CommandType == Hp8C40BiosFanControl.SetFanLevelCommandType &&
            !request.Payload.SequenceEqual(new byte[] { 255, 255, 0, 0 }))
            EnsureAllowed();
    }

    public void Dispose() => Current.Value = _previous;
}
