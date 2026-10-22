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
    internal IReadOnlyList<ProductAutomaticAdmissionIssue> Issues { get; private set; } = [];
    internal string Phase => _resumption ? "resumption" : _manualRetry ? "manual-retry" : "startup";
    private readonly bool _manualRetry, _resumption;
    private readonly ProductProtectionSettings _protections;
    private string _status="Esperando sensores para Automático al iniciar…";
    internal string Status {get=>_resumption?_status.Replace("Automático al iniciar","Reanudación de Automático",StringComparison.OrdinalIgnoreCase):_manualRetry?_status.Replace("Automático al iniciar","Reintento manual de Automático",StringComparison.OrdinalIgnoreCase):_status;private set=>_status=value;}
    internal ProductStartupAutomatic(long now,bool manualRetry=false,ProductProtectionSettings? protections=null,bool resumption=false) { _started=now;_manualRetry=manualRetry;_resumption=resumption;_protections=protections??new(); }
    internal void Cancel(string reason) { Finished=true; Cancelled=true; Status=reason; Issues=[]; }
    internal bool Observe(ProductRuntimeState state, DateTimeOffset now, long clock)
    {
        if(Finished)return false;
        if(clock<_started)
        { Cancel("Automático al iniciar: reloj regresivo; permanece en Firmware."); return false; }
        if(state.LifecycleBlocked)
        { Cancel("Automático al iniciar bloqueado por interrupción: "+(state.LifecycleBlockReason??state.Failure??"controlador bloqueado")+"."); return false; }
        if(state.PerformanceRecovery?.Checking==true)
        {
            _samples=0;_lastSample=null;_firstSample=null;Issues=[];
            Status="Automático al iniciar en espera: comprobando registros de recuperación…";
            return false;
        }
        if(state.PerformanceRecovery?.Pending==true)
        { Cancel("Automático al iniciar bloqueado: hay una sesión pendiente de recuperación."); return false; }
        if(state.Target=="Unsupported")
        { Cancel("Automático al iniciar bloqueado: equipo no autorizado."); return false; }
        if(state.FanMode!="Firmware"||state.FanAuthority!="Firmware"||state.AutomaticPreparing||state.PerformanceProcessPresent)
        { Cancel($"Automático al iniciar cancelado por otra operación: modo {state.FanMode}, autoridad {state.FanAuthority}, preparación {state.AutomaticPreparing}, proceso CPU/GPU {state.PerformanceProcessPresent}."); return false; }
        var s=state.Snapshot;
        Issues=ProductAutomaticAdmission.StartupIssues(state,_protections,now);
        // A timer rechecking an already counted acquisition is not a new bad
        // acquisition. Keep its qualification, but never admit expired data.
        // Changed source/runtime, invalid values and future dates still reset it.
        if(s is not null && _lastSample==s.Timestamp && _source==state.Source &&
            Issues.All(i=>i.Code.EndsWith("-stale",StringComparison.Ordinal)))
        {
            if(Issues.Count>0)Status="Automático al iniciar en espera: "+string.Join(" ",Issues.Select(i=>i.Detail));
            return false;
        }
        if(Issues.Count>0)
        { _samples=0; _lastSample=null; _firstSample=null; Status="Automático al iniciar en espera: "+string.Join(" ",Issues.Select(i=>i.Detail)); return false; }
        if(s is null)throw new InvalidOperationException("La entrada requiere una muestra confirmada.");
        if(_lastSample==s.Timestamp)return false;
        if(_lastSample.HasValue&&(s.Timestamp<_lastSample||s.Timestamp-_lastSample>TimeSpan.FromSeconds(3))||_source!=state.Source)
        { _samples=0; _firstSample=null; }
        _source=state.Source; _lastSample=s.Timestamp; _firstSample??=s.Timestamp;
        if(++_samples<3||s.Timestamp-_firstSample<TimeSpan.FromSeconds(2))
        { Status=$"Verificando sensores para Automático al iniciar: {Math.Min(_samples,3)}/3 adquisiciones nuevas; mínimo 2 s entre primera y última."; return false; }
        Finished=true; Status="Preparando Automático al iniciar: CPU/GPU antes de ventiladores…";
        return true;
    }
}
