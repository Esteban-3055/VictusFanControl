using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

// Calls the production runtime with its own isolated test
// dependencies. No ProductForm, worker.Start(), physical reader or real guardian
// command is used. WMI children explicitly use --fan-wmi-fixture-session;
// performance children are intercepted and restricted to --gui-fixture-session.
static class R
{
    internal static readonly Assembly App=Assembly.Load("VictusFanControl.App");
    internal const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    internal static Type T(string name)=>App.GetType("VictusFanControl.App."+name,true)!;
    internal static object New(Type t,params object?[] args)=>Activator.CreateInstance(t,Flags,null,args,null)!;
    internal static object New(string name,params object?[] args)=>New(T(name),args);
    internal static object? Get(object obj,string key)=>obj.GetType().GetProperty(key,Flags)!.GetValue(obj);
    internal static void SetField(object obj,string key,object? value)=>obj.GetType().GetField(key,Flags)!.SetValue(obj,value);
    internal static object? Call(object obj,string method,params object?[] args)
    {
        var m=obj.GetType().GetMethods(Flags).Single(m=>m.Name==method&&m.GetParameters().Length==args.Length);
        try{return m.Invoke(obj,args);}catch(TargetInvocationException e){throw e.InnerException!;}
    }
    internal static Task Invoke(object obj,string method,params object?[] args)=>(Task)Call(obj,method,args)!;
    internal static object? Static(string type,string method)=>T(type).GetMethod(method,Flags)!.Invoke(null,null);
    internal static Delegate Inventory()
    {
        var t=T("PerformanceRecoveryPreview");var ctor=t.GetConstructors(Flags).Single(c=>c.GetParameters().Length==5);
        var p=ctor.GetParameters();
        var body=Expression.New(ctor,Expression.Constant(false),Expression.Constant("AUDIT ISOLATED INVENTORY"),Expression.Default(p[2].ParameterType),Expression.Default(p[3].ParameterType),Expression.Default(p[4].ParameterType));
        return Expression.Lambda(typeof(Func<>).MakeGenericType(t),body).Compile();
    }
}

sealed class Fixture : IAsyncDisposable
{
    internal readonly object Runtime,Worker,Client,Source,Backend;
    internal readonly object? Shared,Raw;
    internal TelemetrySnapshot Current;
    internal string? PerformanceReport;
    internal Fixture(ProductProfiles profiles,bool withWmi=false)
    {
        Current=Sample();
        Source=R.New(R.T("ProductRuntimePerformanceSelfTest").GetNestedType("Source",R.Flags)!);
        Backend=R.New(R.T("ProductRuntimePerformanceSelfTest").GetNestedType("Backend",R.Flags)!);
        Func<ProcessStartInfo,Process?> launch=start=>
        {
            if(start.ArgumentList[0]!="--gui-session")throw new InvalidOperationException("Unexpected process request");
            start.ArgumentList[0]="--gui-fixture-session";
            PerformanceReport=start.ArgumentList[start.ArgumentList.IndexOf("--report")+1];
            return Process.Start(start);
        };
        var inventory=R.Inventory();
        Client=R.New("PerformanceGuardianClient","fixture://post-release-audit",launch,inventory);
        Func<CancellationToken,ValueTask<TelemetrySnapshot>> reader=_=>ValueTask.FromResult(Current with{Timestamp=DateTimeOffset.UtcNow});
        Worker=R.New("TelemetryWorker","fixture://post-release-audit",reader);
        R.Call(R.Get(Worker,"StateMachine")!,"Transition",SystemState.Healthy,"Synthetic audit measurements");
        if(withWmi)
        {
            Raw=R.New("WmiFanGuiGuardianClient",true,Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N")),null,false,false);
            Shared=R.New("ProductWmiLifetime",Raw,Source);
        }
        var dependencies=R.New("ProductRuntimeDependencies",R.Static("ProductRuntimePerformanceSelfTest","Hardware"),Backend,Worker,Client,Source,inventory,null,Shared);
        Runtime=R.New("ProductRuntime","fixture://post-release-audit",profiles,null,null,null,dependencies,false,null);
        Publish(Current);
    }
    internal static TelemetrySnapshot Sample()=>(TelemetrySnapshot)R.Static("ProductRuntimePerformanceSelfTest","Sample")!;
    internal object State=>R.Get(Runtime,"State")!;
    internal void Publish(TelemetrySnapshot value){Current=value;R.Call(Worker,"PublishAcknowledgementSnapshot",value);}
    internal Task Auto(ProductProfiles p)=>R.Invoke(Runtime,"SelectFanModeAsync",AdaptiveFanProductionMode.Automatic,p);
    internal Task Domain(bool cpu,bool enabled,ProductProfiles p)=>R.Invoke(Runtime,"SetPerformanceDomainAsync",cpu,enabled,p);
    internal void Power(string source)=>R.SetField(Source,"Current",Enum.Parse(Source.GetType().GetField("Current",R.Flags)!.FieldType,source));
    internal async Task Pump(int count=4,int delay=100)
    {
        for(int i=0;i<count;i++)
        {await R.Invoke(Worker,"ProcessAndPublishSnapshotAsync",Sample(),CancellationToken.None);await Task.Delay(delay);}
    }
    internal object Summary()=>new{FanMode=R.Get(State,"FanMode"),Blocked=R.Get(State,"LifecycleBlocked"),Reason=R.Get(State,"LifecycleBlockReason"),Requests=R.Get(State,"FunctionRequests"),Applied=R.Get(Client,"AppliedConfiguration"),Ctgp=Raw is null?null:R.Get(Raw,"Ctgp"),CtgpWanted=Shared is null?null:R.Get(Shared,"CtgpWanted")};
    public async ValueTask DisposeAsync()
    {
        await ((IAsyncDisposable)Runtime).DisposeAsync();
        if(PerformanceReport is{} path&&File.Exists(path))
        {
            using var d=JsonDocument.Parse(File.ReadAllText(path));
            if(d.RootElement.GetProperty("CpuHardwareWriteAttempts").GetInt32()!=0||d.RootElement.GetProperty("GpuHardwareWriteAttempts").GetInt32()!=0)
                throw new InvalidOperationException("Hardware isolation failed");
        }
    }
}
