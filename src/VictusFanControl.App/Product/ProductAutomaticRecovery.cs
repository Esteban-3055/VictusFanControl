namespace VictusFanControl.App;

/// <summary>One recovery per exact inventory, at most three episodes until a minute of healthy Automatic.</summary>
internal sealed class ProductAutomaticRecovery
{
    private string? _candidate;
    private long _since;
    private long? _healthySince;
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readDeferrals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _readRetryAt = new(StringComparer.Ordinal);
    private string? _lastRequest;
    private int _readDeferralCount;
    private int _attempts;
    private long? _sessionSince;
    private int _sessionAttempts;
    private bool _sessionRequested;
    // Failed release stays latched. Only a successfully opened replacement or
    // a full minute of healthy operation permits a new session episode.
    internal void SessionOpened() { _sessionRequested = false; _sessionSince = null; }
    internal bool TakeSessionRequest(ProductRuntimeState state, long now)
    {
        if (state.PerformanceRecovery?.Pending == true || state.AutomaticReview ||
            state.Runtime is not ("Degraded" or "Failed" or "Recovering") ||
            state.FanMode == "Automatic" && !state.LifecycleBlocked ||
            state.AutomaticPreparing || state.PerformanceUpdating)
        { _sessionSince = null; return false; }
        if (_sessionSince is null || now < _sessionSince) { _sessionSince = now; return false; }
        if (_sessionRequested || _sessionAttempts >= 3 || now - _sessionSince < new[] { 15000, 30000, 60000 }[_sessionAttempts]) return false;
        _sessionRequested = true; _sessionAttempts++; return true;
    }
    internal void Observe(ProductRuntimeState state, long now)
    {
        if (state.Runtime == "Healthy" && !state.LifecycleBlocked && state.PerformanceRecovery?.Pending != true)
        {
            _healthySince ??= now;
            if (now >= _healthySince && now - _healthySince >= 60000)
            { _attempts = 0; _attempted.Clear(); _readDeferrals.Clear(); _readRetryAt.Clear(); _lastRequest = null; _readDeferralCount = 0;
                _sessionAttempts = 0; _sessionRequested = false; _sessionSince = null; }
        }
        else _healthySince = null;
    }
    internal void Pause() { _candidate = null; _sessionSince = null; }
    internal void RecordAttempt(PerformanceRecoveryPreview expected)
    {
        var key = InventoryKey(expected);
        if (_attempted.Add(key)) _attempts++;
        _lastRequest = key;
    }
    internal bool TakeRequest(ProductRuntimeState state, long now)
    {
        var p = state.PerformanceRecovery;
        if (p is not { Pending: true, Actionable: true } || state.AutomaticReview ||
            p.CpuSession == Guid.Empty || p.GpuSession == Guid.Empty ||
            state.FanMode == "Automatic" && !state.LifecycleBlocked || state.AutomaticPreparing || state.PerformanceUpdating ||
            p.FanRecords?.Any(r => !r.Recoverable) == true)
        { Pause(); return false; }
        var key = InventoryKey(p);
        if (_candidate != key || now < _since) { _candidate = key; _since = now; return false; }
        if (_attempts >= 3 || _attempted.Contains(key) || now - _since < 10000 || now < _readRetryAt.GetValueOrDefault(key)) return false;
        _attempted.Add(key); _attempts++; _lastRequest = key; return true;
    }

    // Call only when the inventory read failed BEFORE draining a runtime or
    // invoking any recovery helper. An unconfirmed release is never retried.
    internal bool DeferInventoryRead(PerformanceRecoveryPreview expected, long now)
    {
        var key = InventoryKey(expected);
        if (key != _lastRequest || !_attempted.Contains(key) || _readDeferralCount >= 6) return false;
        var count = _readDeferrals.GetValueOrDefault(key);
        if (count >= 2) return false;
        _readDeferrals[key] = count + 1;
        _readDeferralCount++;
        _attempted.Remove(key); _attempts--;
        _candidate = key; _since = now;
        _readRetryAt[key] = now + (count == 0 ? 30000 : 60000);
        return true;
    }

    private static string InventoryKey(PerformanceRecoveryPreview p) => p.CpuSession + "/" + p.GpuSession + "/" + string.Join(";", (p.FanRecords ?? [])
        .OrderBy(r => r.Path, StringComparer.Ordinal).Select(r => r.Path + ":" + r.Sha256));
}
