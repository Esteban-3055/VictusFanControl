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
    internal void Observe(ProductRuntimeState state, long now)
    {
        if (state.Runtime == "Healthy" && state.FanMode == "Automatic" && !state.LifecycleBlocked && state.PerformanceRecovery?.Pending != true)
        {
            _healthySince ??= now;
            if (now >= _healthySince && now - _healthySince >= 60000)
            { _attempts = 0; _attempted.Clear(); _readDeferrals.Clear(); _readRetryAt.Clear(); _lastRequest = null; _readDeferralCount = 0; }
        }
        else _healthySince = null;
    }
    internal void Pause() { _candidate = null; }
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
