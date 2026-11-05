namespace VictusFanControl.App;

/// <summary>One recovery per exact inventory with a shared, stability-qualified episode budget.</summary>
internal sealed class ProductAutomaticRecovery
{
    private readonly ProductRecoveryEpisode _episode;
    internal ProductAutomaticRecovery(ProductRecoveryEpisode? episode=null)=>_episode=episode??new();
    private string? _candidate;
    private long _since;
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejectedReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readDeferrals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _readRetryAt = new(StringComparer.Ordinal);
    private string? _lastRequest;
    private bool _lastRequestNative;
    private long _globalReadRetryAt;
    private int _readDeferralCount;
    private long? _sessionSince;
    private bool _sessionRequested;
    // Failed release stays latched. Only a successfully opened replacement or
    // a fully qualified healthy Automatic interval permits a new session episode.
    internal void SessionOpened() { _sessionRequested = false; _sessionSince = null; _episode.ConfirmOwnerReleased();_episode.Finish(); }
    internal bool TakeSessionRequest(ProductRuntimeState state, long now)
    {
        if (state.PerformanceRecovery?.Pending == true || state.AutomaticReview ||
            state.Runtime is not ("Degraded" or "Failed" or "Recovering") ||
            state.FanMode == "Automatic" && !state.LifecycleBlocked ||
            state.AutomaticPreparing || state.PerformanceUpdating)
        { _sessionSince = null; return false; }
        if (_sessionSince is null || now < _sessionSince) { _sessionSince = now; return false; }
        if (_sessionRequested || now - _sessionSince < _episode.DelayMilliseconds || !_episode.TryReserve(now)) return false;
        _sessionRequested = true; return true;
    }
    internal void Observe(ProductRuntimeState state, long now)
    {
        if (_episode.Observe(state,DateTimeOffset.UtcNow))
        {
            _attempted.Clear();_rejectedReads.Clear();_readDeferrals.Clear();_readRetryAt.Clear();_lastRequest=null;_readDeferralCount=0;_globalReadRetryAt=0;
            _sessionRequested=false;_sessionSince=null;
        }
    }
    internal void Pause() { _candidate = null; _sessionSince = null; }
    internal void RecordAttempt(PerformanceRecoveryPreview expected)
    {
        var key = InventoryKey(expected);
        if (_attempted.Add(key)&&!_episode.Reserved)_episode.TryReserve(Environment.TickCount64);
        _lastRequest = key;_lastRequestNative=true;
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
        if (_attempted.Contains(key) || _rejectedReads.Contains(key) || now - _since < _episode.DelayMilliseconds || now < _readRetryAt.GetValueOrDefault(key)||now<_globalReadRetryAt||!_episode.TryReserve(now)) return false;
        _lastRequest = key;_lastRequestNative=false;return true;
    }

    // Call only when the inventory read failed BEFORE draining a runtime or
    // invoking any recovery helper. An unconfirmed release is never retried.
    internal bool DeferInventoryRead(PerformanceRecoveryPreview expected, long now)
    {
        var key = InventoryKey(expected);
        if (key != _lastRequest || _lastRequestNative || !_episode.Reserved) return false;
        var count = _readDeferrals.GetValueOrDefault(key);
        _readDeferrals[key] = Math.Min(3,count+1);
        _readDeferralCount=Math.Min(6,_readDeferralCount+1);
        _episode.DeferRead();
        _candidate = key; _since = now;
        var delay=count>=2||_readDeferralCount>=6?300000:count==0?30000:60000;
        _readRetryAt[key] = now+delay;
        if(_readDeferralCount>=6)_globalReadRetryAt=now+delay;
        return true;
    }
    internal void AbandonInventoryRead()
    {if(!_lastRequestNative)_episode.DeferRead();}
    internal void RejectInventoryRead(PerformanceRecoveryPreview expected)
    {
        if(_lastRequestNative)return;
        _rejectedReads.Add(InventoryKey(expected));
        _episode.DeferRead();
    }

    private static string InventoryKey(PerformanceRecoveryPreview p) => p.CpuSession + "/" + p.GpuSession + "/" + string.Join(";", (p.FanRecords ?? [])
        .OrderBy(r => r.Path, StringComparer.Ordinal).Select(r => r.Path + ":" + r.Sha256));
}
