namespace VictusFanControl.Control.Adaptive;

/// <summary>
/// One ordinary-app P16 qualification session. Interruption is irreversible;
/// telemetry recovery and Firmware requests must never renew this authorization.
/// The parent durable per-HEAD fence separately prevents a second app attempt.
/// </summary>
public sealed class Hp8C40P16QualificationSession
{
    private sealed record Interruption(string Source, DateTimeOffset Utc);
    private Interruption? _interruption;
    private readonly CancellationTokenSource _interrupted = new();

    public bool IsInterrupted => Volatile.Read(ref _interruption) is not null;
    public CancellationToken InterruptionToken => _interrupted.Token;
    public string? InterruptionSource => Volatile.Read(ref _interruption)?.Source;
    public DateTimeOffset? InterruptedUtc => Volatile.Read(ref _interruption)?.Utc;

    public bool Interrupt(string source, DateTimeOffset utc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Interlocked.CompareExchange(ref _interruption, new Interruption(source, utc), null) is not null)
            return false;
        _interrupted.Cancel();
        return true;
    }
}
