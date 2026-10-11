namespace VictusFanControl.App;

/// <summary>One recovery per exact inventory, at most three episodes until a minute of healthy Automatic.</summary>
internal sealed class ProductAutomaticRecovery
{
    private string? _candidate;
    private long _since;
    private long? _healthySince;
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private int _attempts;
    internal void Observe(ProductRuntimeState state, long now)
    {
        if (state.Runtime == "Healthy" && state.FanMode == "Automatic" && !state.LifecycleBlocked && state.PerformanceRecovery?.Pending != true)
        {
            _healthySince ??= now;
            if (now >= _healthySince && now - _healthySince >= 60000) { _attempts = 0; _attempted.Clear(); }
        }
        else _healthySince = null;
    }
    internal void Pause() { _candidate = null; }
    internal bool TakeRequest(ProductRuntimeState state, long now)
    {
        var p = state.PerformanceRecovery;
        if (p is not { Pending: true, Actionable: true } || state.AutomaticReview ||
            p.CpuSession == Guid.Empty || p.GpuSession == Guid.Empty ||
            state.FanMode == "Automatic" && !state.LifecycleBlocked || state.AutomaticPreparing || state.PerformanceUpdating ||
            p.FanRecords?.Any(r => !r.Recoverable) == true)
        { Pause(); return false; }
        var key = p.CpuSession + "/" + p.GpuSession + "/" + string.Join(";", (p.FanRecords ?? [])
            .OrderBy(r => r.Path, StringComparer.Ordinal).Select(r => r.Path + ":" + r.Sha256));
        if (_candidate != key || now < _since) { _candidate = key; _since = now; return false; }
        if (_attempts >= 3 || _attempted.Contains(key) || now - _since < 10000) return false;
        _attempted.Add(key); _attempts++; return true;
    }
}
