using VictusFanControl.Product;

namespace VictusFanControl.App;

/// <summary>One startup request, never a retry or resume policy. Uses observations, not UI ticks.</summary>
internal sealed class ProductStartupAutomatic
{
    internal const int MaximumWaitMilliseconds = 30000;
    private readonly long _started;
    private DateTimeOffset? _lastSample;
    private string? _source;
    private DateTimeOffset? _firstSample;
    private int _samples;
    internal bool Finished { get; private set; }
    internal string Status { get; private set; } = "Esperando sensores para Automático al iniciar…";
    internal ProductStartupAutomatic(long now) => _started=now;
    internal void Cancel(string reason) { Finished=true; Status=reason; }
    internal bool Observe(ProductRuntimeState state, DateTimeOffset now, long clock)
    {
        if(Finished)return false;
        if(clock<_started||clock-_started>=MaximumWaitMilliseconds)
        { Cancel("Automático al iniciar: no hubo admisión en 30 s; permanece en Firmware."); return false; }
        if(state.LifecycleBlocked||state.Failure is not null||state.PerformanceRecovery?.Pending==true||state.Target=="Unsupported")
        { Cancel("Automático al iniciar bloqueado; revisa el estado y la recuperación."); return false; }
        if(state.FanMode!="Firmware"||state.FanAuthority!="Firmware"||state.AutomaticPreparing||state.PerformanceProcessPresent)
        { Cancel("Automático al iniciar cancelado por otra operación."); return false; }
        var s=state.Snapshot;
        if(!ProductRelease.IsAutomaticAuthorized(state.Target)||!state.AutomaticAuthorized||!state.CanApplyPerformance||
            state.Runtime!="Healthy"||state.Source is not ("Ac" or "Battery")||s is null||!s.IsComplete||!s.IsFanTelemetryFreshAt(now)||
            now<s.Timestamp||now-s.Timestamp>=TimeSpan.FromSeconds(3)||
            !Within(s.CpuControlTemperatureC,90)||!Within(s.GpuTemperatureC,82)||
            !Within(s.CpuPackagePowerW,60)||!Within(s.GpuPowerW,75))
        { _samples=0; _lastSample=null; _firstSample=null; return false; }
        if(_lastSample==s.Timestamp)return false;
        if(_lastSample.HasValue&&(s.Timestamp<_lastSample||s.Timestamp-_lastSample>TimeSpan.FromSeconds(3))||_source!=state.Source)
        { _samples=0; _firstSample=null; }
        _source=state.Source; _lastSample=s.Timestamp; _firstSample??=s.Timestamp;
        if(++_samples<3||s.Timestamp-_firstSample<TimeSpan.FromSeconds(2))return false;
        Finished=true; Status="Preparando Automático al iniciar: CPU/GPU antes de ventiladores…";
        return true;
    }
    private static bool Within(double? value,double max) => value.HasValue&&double.IsFinite(value.Value)&&value>=0&&value<=max;
}
