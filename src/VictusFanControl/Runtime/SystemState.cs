namespace VictusFanControl.Runtime;

public enum SystemState
{
    Starting,
    Healthy,
    Suspending,
    Suspended,
    Resuming,
    Recovering,
    Degraded,
    Faulted
}

public sealed record SystemStateChangedEventArgs(
    SystemState Previous,
    SystemState Current,
    string Reason,
    DateTimeOffset Timestamp);
