using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Performance;
using VictusFanControl.Product;

namespace VictusFanControl.App;

/// <summary>One optional AC-only phase before accepting a Windows session-end query.
/// Uses the normal supervised Manual path, including its safety and native dispatch gates.
/// Expiry fences late commands; it never asserts that an in-flight native call has drained.</summary>
internal sealed class ProductShutdownCooling
{
    internal const int DurationMilliseconds=20_000;
    internal const int NominalRpm=5000;
    internal const int Level=50;
    internal const string CoolingReason="Refrigerando el equipo antes de apagar (20 segundos).";
    internal const string ReleaseReason="Liberando controles de ventiladores y CPU/GPU.";
    private readonly IProductRuntime runtime;
    private readonly Func<PerformancePowerSourceKind> source;
    private readonly Func<long> clock;
    private readonly Func<bool> ownerCurrent;
    private readonly long started;
    private int cancelled;
    internal Task Preparation {get;private set;}=Task.CompletedTask;
    internal bool Attempted {get;private set;}
    internal ProductShutdownCooling(IProductRuntime runtime,Func<PerformancePowerSourceKind> source,Func<long> clock,Func<bool> ownerCurrent)
    {this.runtime=runtime;this.source=source;this.clock=clock;this.ownerCurrent=ownerCurrent;started=clock();}
    internal static bool Eligible(ProductRuntimeState state)=>ProductRelease.IsAutomaticAuthorized(state.Target)&&
        state.Runtime=="Healthy"&&state.ManualAuthorized&&!state.LifecycleBlocked&&!state.AutomaticPreparing&&!state.PerformanceUpdating&&
        state.PerformanceRecovery?.Pending!=true&&state.FanAuthority is "Firmware" or "Custom";
    internal bool WithinBudget=>clock()-started is >=0 and <DurationMilliseconds;
    internal long ElapsedMilliseconds=>Math.Max(0,clock()-started);
    internal int RemainingSeconds=>(int)Math.Max(0,(DurationMilliseconds-Math.Max(0,clock()-started)+999)/1000);
    internal void Cancel()=>Interlocked.Exchange(ref cancelled,1);
    private void EnsureDispatchAllowed()
    {
        var state=runtime.State;
        var reason=Volatile.Read(ref cancelled)!=0?"cancelación solicitada":!WithinBudget?"plazo agotado":
            !ownerCurrent()?"propietario o ciclo de vida cambiado":!Eligible(state)?
            $"control no disponible (runtime={state.Runtime}, autoridad={state.FanAuthority}, manual={state.ManualAuthorized}, lifecycle={state.LifecycleBlocked}, automaticPreparing={state.AutomaticPreparing}, performanceUpdating={state.PerformanceUpdating}, recovery={state.PerformanceRecovery?.Pending==true})":
            source()!=PerformancePowerSourceKind.Ac?"AC no confirmado":null;
        if(reason is not null)throw new OperationCanceledException("Refrigeración de apagado cancelada: "+reason+".");
    }
    internal void Start()=>Preparation=PrepareAsync();
    private async Task PrepareAsync()
    {
        EnsureDispatchAllowed();
        // The AsyncLocal scope follows queued Manual work and is rechecked by the
        // production coordinator and at the HP native setter, including late work.
        using var admission=new FanDispatchAdmissionScope(EnsureDispatchAllowed);
        Attempted=true;
        await runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Manual,new ProductProfiles());
        EnsureDispatchAllowed();
        await runtime.ApplyManualAsync(Level);
        EnsureDispatchAllowed();
        if(!HasAcceptedTarget)throw new InvalidOperationException("El control no confirmó la solicitud nominal de 5000 RPM.");
    }
    private bool HasAcceptedTarget=>runtime.State is {FanMode:"Manual",FanAuthority:"Custom",ManualAppliedLevel:Level};
    internal bool Continue()
    {
        EnsureDispatchAllowed();
        if(Preparation.IsFaulted||Preparation.IsCanceled)return false;
        return !Preparation.IsCompleted||HasAcceptedTarget;
    }
}
