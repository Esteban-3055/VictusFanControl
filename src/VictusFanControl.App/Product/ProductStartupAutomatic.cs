using VictusFanControl.Product;

namespace VictusFanControl.App;

/// <summary>Persistent startup or explicit retry request; admission still requires three distinct fresh observations.</summary>
internal sealed class ProductStartupAutomatic
{
    internal const int MaximumWaitMilliseconds = 30000;
    private readonly long _started;
    private DateTimeOffset? _lastSample;
    private string? _source;
    private DateTimeOffset? _firstSample;
    private int _samples;
    internal bool Finished { get; private set; }
    internal bool Cancelled { get; private set; }
    private readonly bool _manualRetry, _resumption;
    private readonly ProductProtectionSettings _protections;
    private string _status="Esperando sensores para Automático al iniciar…";
    internal string Status {get=>_resumption?_status.Replace("Automático al iniciar","Reanudación de Automático",StringComparison.OrdinalIgnoreCase):_manualRetry?_status.Replace("Automático al iniciar","Reintento manual de Automático",StringComparison.OrdinalIgnoreCase):_status;private set=>_status=value;}
    internal ProductStartupAutomatic(long now,bool manualRetry=false,ProductProtectionSettings? protections=null,bool resumption=false) { _started=now;_manualRetry=manualRetry;_resumption=resumption;_protections=protections??new(); }
    internal void Cancel(string reason) { Finished=true; Cancelled=true; Status=reason; }
    internal bool Observe(ProductRuntimeState state, DateTimeOffset now, long clock)
    {
        if(Finished)return false;
        if(clock<_started)
        { Cancel("Automático al iniciar: reloj regresivo; permanece en Firmware."); return false; }
        if(state.LifecycleBlocked||state.PerformanceRecovery?.Pending==true||state.Target=="Unsupported")
        { Cancel("Automático al iniciar bloqueado; revisa el estado y la recuperación."); return false; }
        if(state.FanMode!="Firmware"||state.FanAuthority!="Firmware"||state.AutomaticPreparing||state.PerformanceProcessPresent)
        { Cancel("Automático al iniciar cancelado por otra operación."); return false; }
        var s=state.Snapshot;
        if(!ProductRelease.IsAutomaticAuthorized(state.Target)||!state.AutomaticAuthorized||!state.CanApplyPerformance||
            state.Runtime!="Healthy"||state.Source is not ("Ac" or "Battery")||s is null||!s.IsComplete||!s.IsFanTelemetryFreshAt(now,3000)||s.RetainedTelemetry is not null||
            now<s.Timestamp||now-s.Timestamp>=TimeSpan.FromSeconds(3)||
            !Within(s.CpuControlTemperatureC,_protections.CpuThermalHandoff?90:110)||!Within(s.GpuTemperatureC,_protections.GpuThermalHandoff?82:105)||
            !Within(s.CpuPackagePowerW,_protections.PowerEnvelopeHandoff?60:500)||!Within(s.GpuPowerW,_protections.PowerEnvelopeHandoff?75:300))
        { _samples=0; _lastSample=null; _firstSample=null; Status="Esperando sensores para Automático al iniciar… "+(state.Runtime!="Healthy"?"Telemetría: "+state.Runtime:s?.RetainedTelemetry is not null?"Lecturas necesarias retrasadas.":"Verificando temperaturas, RPM y límites de entrada."); return false; }
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
