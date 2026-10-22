namespace VictusFanControl.App;

/// <summary>Presentation only. Native admission still reads the journals afresh.</summary>
internal sealed class RecoveryPreviewCache : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<PerformanceRecoveryPreview> _read;
    private readonly Func<long> _clock;
    private readonly long _maximumAge;
    private PerformanceRecoveryPreview? _value;
    private long _completedAt;
    private Task? _scan;
    private bool _disposed;
    private static readonly PerformanceRecoveryPreview Unknown = new(true,
        "Comprobando registros de recuperación en segundo plano…") { Checking = true };

    internal RecoveryPreviewCache(Func<PerformanceRecoveryPreview>? read = null, Func<long>? clock = null, long maximumAge = 5000)
    {
        _read = read ?? (() => PerformanceRecoveryPreview.Read());
        _clock = clock ?? (() => Environment.TickCount64);
        _maximumAge = maximumAge;
    }

    internal PerformanceRecoveryPreview Get()
    {
        lock (_sync)
        {
            if (_disposed) return Unknown;
            var age = _clock() - _completedAt;
            // Refresh ahead of expiry so normal polling does not interrupt the
            // recovery coordinator's observation windows with a loading state.
            if ((_value is null || age >= _maximumAge / 2) && (_scan is null || _scan.IsCompleted))
                // Blocking filesystem calls get one dedicated worker, not the telemetry/pool worker.
                _scan = Task.Factory.StartNew(Scan, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return _value is not null && age >= 0 && age < _maximumAge ? _value : Unknown;
        }
    }

    private void Scan()
    {
        PerformanceRecoveryPreview value;
        try { value = _read(); }
        catch (Exception ex) { value = new(true, "No se pudo comprobar la recuperación: " + ex.Message); }
        var completedAt = _clock();
        lock (_sync)
        {
            if (_disposed) return;
            _value = value;
            _completedAt = completedAt;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? scan;
        lock (_sync) { _disposed = true; _value = null; scan = _scan; }
        // No callbacks or new scans after disposal. A runtime handoff must drain this reader too.
        if (scan is not null) await scan.ConfigureAwait(false);
    }
}
