namespace VictusFanControl.Performance;

internal enum CpuPowerLimiterState
{
    Unsupported,
    Disabled,
    Applying,
    Active,
    Recovering,
    Failed
}

internal readonly record struct CpuPowerLimitRequest(
    double Pl1Watts,
    double Pl2Watts);

internal readonly record struct CpuPowerLimitSnapshot(
    ulong Raw,
    double Pl1Watts,
    double Pl2Watts,
    bool Locked);

internal readonly record struct CpuPowerLimitApplyPlan(
    ulong RequestedRaw,
    double AppliedPl1Watts,
    double AppliedPl2Watts);

internal readonly record struct CpuPowerLimitRestorePlan(
    ulong Value,
    string Status);

internal interface ICpuPowerLimitBackend
{
    bool IsSupported { get; }
    CpuPowerLimitSnapshot Read();
    CpuPowerLimitApplyPlan BuildApplyPlan(
        CpuPowerLimitSnapshot baseline,
        CpuPowerLimitRequest request);
    CpuPowerLimitRestorePlan PlanRestore(
        CpuPowerLimitSnapshot baseline,
        ulong appliedRaw,
        CpuPowerLimitSnapshot current);
    void Write(ulong raw);
}

internal sealed class CpuPowerLimiter : IDisposable
{
    private readonly ICpuPowerLimitBackend _backend;
    private CpuPowerLimitSnapshot? _baseline;
    private ulong? _appliedRaw;
    private bool _disposed;

    internal CpuPowerLimiter(ICpuPowerLimitBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        State = backend.IsSupported
            ? CpuPowerLimiterState.Disabled
            : CpuPowerLimiterState.Unsupported;
    }

    internal CpuPowerLimiterState State { get; private set; }
    internal string? LastError { get; private set; }
    internal CpuPowerLimitSnapshot? Baseline => _baseline;
    internal ulong? AppliedRaw => _appliedRaw;

    internal bool Apply(CpuPowerLimitRequest request)
    {
        ThrowIfDisposed();
        LastError = null;
        if (State == CpuPowerLimiterState.Unsupported)
            return Fail("CPU_POWER_LIMIT_UNSUPPORTED");
        if (State != CpuPowerLimiterState.Disabled)
            return Fail("CPU_POWER_LIMIT_APPLY_REQUIRES_DISABLED_STATE");
        if (!ValidRequest(request))
            return Fail("CPU_POWER_LIMIT_INVALID_REQUEST");

        var baseline = _backend.Read();
        if (baseline.Locked)
            return Fail("CPU_POWER_LIMIT_LOCKED");

        var plan = _backend.BuildApplyPlan(baseline, request);
        if (plan.RequestedRaw == baseline.Raw ||
            plan.AppliedPl1Watts < 10 ||
            plan.AppliedPl2Watts < plan.AppliedPl1Watts ||
            plan.AppliedPl1Watts >= baseline.Pl1Watts ||
            plan.AppliedPl2Watts >= baseline.Pl2Watts)
        {
            return Fail("CPU_POWER_LIMIT_BACKEND_PLAN_REJECTED");
        }

        _baseline = baseline;
        _appliedRaw = plan.RequestedRaw;
        State = CpuPowerLimiterState.Applying;

        try
        {
            _backend.Write(plan.RequestedRaw);
            var readback = _backend.Read();
            if (readback.Raw != plan.RequestedRaw)
            {
                LastError = "CPU_POWER_LIMIT_READBACK_MISMATCH";
                return RecoverAfterFailedApply();
            }

            State = CpuPowerLimiterState.Active;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "CPU_POWER_LIMIT_APPLY_ERROR: " + ex.Message;
            return RecoverAfterFailedApply();
        }
    }

    internal bool VerifyActive()
    {
        ThrowIfDisposed();
        if (State != CpuPowerLimiterState.Active ||
            !_appliedRaw.HasValue)
            return false;

        try
        {
            var current = _backend.Read();
            if (current.Raw == _appliedRaw.Value)
                return true;

            State = CpuPowerLimiterState.Failed;
            LastError = "CPU_POWER_LIMIT_CHANGED_EXTERNALLY__NO_REAPPLY";
            return false;
        }
        catch (Exception ex)
        {
            State = CpuPowerLimiterState.Failed;
            LastError = "CPU_POWER_LIMIT_VERIFY_ERROR: " + ex.Message;
            return false;
        }
    }

    internal bool Release()
    {
        ThrowIfDisposed();
        if (!_baseline.HasValue || !_appliedRaw.HasValue)
        {
            if (State != CpuPowerLimiterState.Unsupported)
                State = CpuPowerLimiterState.Disabled;
            LastError = null;
            return true;
        }

        return RecoverOwnedState();
    }

    private bool RecoverAfterFailedApply()
    {
        var previousError = LastError;
        var restored = RecoverOwnedState();
        if (!restored && previousError is not null)
            LastError = previousError + " | " + LastError;
        else if (restored)
            LastError = previousError;
        return false;
    }

    private bool RecoverOwnedState()
    {
        if (!_baseline.HasValue || !_appliedRaw.HasValue)
            return false;

        State = CpuPowerLimiterState.Recovering;
        try
        {
            var baseline = _baseline.Value;
            var current = _backend.Read();
            var plan = _backend.PlanRestore(
                baseline,
                _appliedRaw.Value,
                current);

            if (plan.Value != current.Raw)
            {
                var secondRead = _backend.Read();
                if (secondRead.Raw != current.Raw)
                    return FailRecovery("CPU_POWER_LIMIT_RESTORE_CONCURRENT_CHANGE");
                _backend.Write(plan.Value);
            }

            var final = _backend.Read();
            if (final.Raw != baseline.Raw)
                return FailRecovery(
                    "CPU_POWER_LIMIT_RESTORE_NOT_BASELINE: " + plan.Status);

            _baseline = null;
            _appliedRaw = null;
            State = CpuPowerLimiterState.Disabled;
            return true;
        }
        catch (Exception ex)
        {
            return FailRecovery("CPU_POWER_LIMIT_RESTORE_ERROR: " + ex.Message);
        }
    }

    private bool Fail(string error)
    {
        LastError = error;
        if (State != CpuPowerLimiterState.Unsupported)
            State = CpuPowerLimiterState.Failed;
        return false;
    }

    private bool FailRecovery(string error)
    {
        LastError = error;
        State = CpuPowerLimiterState.Failed;
        return false;
    }

    private static bool ValidRequest(CpuPowerLimitRequest request) =>
        double.IsFinite(request.Pl1Watts) &&
        double.IsFinite(request.Pl2Watts) &&
        request.Pl1Watts >= 10 &&
        request.Pl2Watts >= request.Pl1Watts;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_baseline.HasValue && _appliedRaw.HasValue)
        {
            try { _ = RecoverOwnedState(); }
            catch { State = CpuPowerLimiterState.Failed; }
        }

        _disposed = true;
    }
}
