namespace VictusFanControl.Runtime;

/// <summary>
/// Thread-safe runtime state machine shared by the GUI and the future safety supervisor.
/// No fan-control write path exists yet.
/// </summary>
public sealed class RuntimeStateMachine
{
    private readonly object _gate = new();
    private SystemState _state = SystemState.Starting;
    private string _reason = "Application starting.";
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    public event EventHandler<SystemStateChangedEventArgs>? StateChanged;

    public SystemState State
    {
        get { lock (_gate) return _state; }
    }

    public string Reason
    {
        get { lock (_gate) return _reason; }
    }

    public DateTimeOffset ChangedAt
    {
        get { lock (_gate) return _changedAt; }
    }

    public void Transition(SystemState next, string reason)
    {
        SystemStateChangedEventArgs? args = null;

        lock (_gate)
        {
            if (_state == next && string.Equals(_reason, reason, StringComparison.Ordinal))
            {
                return;
            }

            var previous = _state;
            _state = next;
            _reason = reason;
            _changedAt = DateTimeOffset.UtcNow;
            args = new SystemStateChangedEventArgs(previous, next, reason, _changedAt);
        }

        StateChanged?.Invoke(this, args);
    }
}
