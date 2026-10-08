using System.Management;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.OemShadow;

namespace VictusFanControl.OemShadowCapture;

// One native call per source group. Timeout never frees a still-running admission slot.
internal sealed class ReadSlot<T> where T:class
{
    private Task<(T? Value,string? Error,int Epoch)>? _pending;
    private long _next;
    private int _epoch;
    private sealed record NativeProgress(long QueuedAt, long? StartedAt = null, long? CompletedAt = null);
    private NativeProgress? _progress;
    internal sealed record ReadProgress(bool InFlight, bool NativeRunning, long? QueueWaitMilliseconds, long? NativeElapsedMilliseconds);
    public ReadProgress CaptureProgress()
    {
        var p=Volatile.Read(ref _progress);var now=Environment.TickCount64;
        return new(InFlight,p?.StartedAt is not null&&p.CompletedAt is null,
            p is null?null:Math.Max(0,(p.StartedAt??now)-p.QueuedAt),
            p?.StartedAt is not {} start?null:Math.Max(0,(p.CompletedAt??now)-start));
    }
    public T? Latest { get; private set; }
    public string? Error { get; private set; }
    public bool InFlight=>_pending is {IsCompleted:false};
    public void Invalidate(){_epoch++;Latest=null;Error="CLOCK_EPOCH_INVALIDATED";}
    public void Poll(long milliseconds,Func<T> read)
    {
        if(_pending is {IsCompleted:true})
        {
            var result=_pending.GetAwaiter().GetResult();_pending=null;
            if(result.Epoch==_epoch){Latest=result.Value;Error=result.Error;}
        }
        if(_pending is not null||milliseconds<_next)return;
        int epoch=_epoch;_next=milliseconds+1000;
        var progress=new NativeProgress(Environment.TickCount64);Volatile.Write(ref _progress,progress);
        _pending=Task.Run(()=>
        {
            progress=progress with{StartedAt=Environment.TickCount64};Volatile.Write(ref _progress,progress);
            try{return ((T?)read(),(string?)null,epoch);}
            catch(Exception ex){return ((T?)null,ex.GetType().Name+": "+ex.Message,epoch);}
            finally{Volatile.Write(ref _progress,progress with{CompletedAt=Environment.TickCount64});}
        });
    }
}

internal sealed record FastSample(Source Package,Source Core,Source Gpu,double? CpuPower,double? GpuPower,double? GpuLoad,string? Error);
internal sealed class FastSources(string module):IDisposable
{
    private IntelMsrReader? _cpu;
    private NvmlClient? _gpu;
    public FastSample Read()
    {
        Source package=new(),core=new(),gpu=new();double? cpuPower=null,gpuPower=null,gpuLoad=null;string? error=null;
        try
        {
            _cpu??=new(module);
            if(_cpu.PhysicalCoreCount!=14)throw new InvalidOperationException("Expected 14 physical cores.");
            var at=DateTimeOffset.UtcNow;package=new(_cpu.ReadPackageTemperatureC(),at);
            at=DateTimeOffset.UtcNow;var cores=_cpu.ReadCoreTemperaturesC();core=new(cores.Count>0?cores.Max(c=>c.TemperatureC):null,at);
            cpuPower=_cpu.ReadPackagePowerW();
        }
        catch(Exception ex){error="CPU: "+ex.Message;_cpu?.Dispose();_cpu=null;package=core=new();}
        try
        {
            _gpu??=new(Hp8C40TargetProfile.ExpectedGpuName,requirePreferredDevice:true);
            var at=DateTimeOffset.UtcNow;var value=_gpu.ReadSample();gpu=new(value.TemperatureC,at);gpuPower=value.PowerW;gpuLoad=value.LoadPercent;
        }
        catch(Exception ex){error=(error is null?"":error+"; ")+"GPU: "+ex.Message;_gpu?.Dispose();_gpu=null;}
        return new(package,core,gpu,cpuPower,gpuPower,gpuLoad,error);
    }
    public void Dispose(){_cpu?.Dispose();_gpu?.Dispose();}
}

internal sealed record DttSample(Source Dtt1,Source Dtt2,Source Dtt3);
internal static class OemSources
{
    internal const string TzInstance=@"ACPI\ThermalZone\TZ01_0";
    internal const string DttPrefix=@"PCI\VEN_8086&DEV_A71D&SUBSYS_8C40103C";
    internal static bool IsDtt(string instance,int suffix)=>instance.Length>DttPrefix.Length && instance[DttPrefix.Length] is '&' or '\\' &&
        instance.StartsWith(DttPrefix,StringComparison.OrdinalIgnoreCase)&&
        instance.EndsWith("_"+suffix,StringComparison.OrdinalIgnoreCase);
    internal static double DeciKelvinToC(double raw)=>raw/10-273.15;
    private static ManagementObjectSearcher Query(string query)=>new(new ManagementScope(@"\\.\root\wmi"),new ObjectQuery(query),
        new System.Management.EnumerationOptions{Timeout=TimeSpan.FromSeconds(2),ReturnImmediately=false});
    public static Source ReadTz()
    {
        var at=DateTimeOffset.UtcNow;var values=new List<double>();
        using var query=Query("SELECT InstanceName,Active,CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        using var result=query.Get();
        foreach(ManagementObject row in result)
        {
            using(row)
                if(row["Active"] is true&&string.Equals(row["InstanceName"]?.ToString(),TzInstance,StringComparison.OrdinalIgnoreCase)&&row["CurrentTemperature"] is not null)
                    values.Add(DeciKelvinToC(Convert.ToDouble(row["CurrentTemperature"])));
        }
        return new(values.Count==1?values[0]:null,at);
    }
    public static DttSample ReadDtt()
    {
        var at=DateTimeOffset.UtcNow;var values=new[]{new List<double>(),new List<double>(),new List<double>()};
        using var query=Query("SELECT InstanceName,Active,Temperature FROM EsifDeviceInformation");
        using var result=query.Get();
        foreach(ManagementObject row in result)
        {
            using(row)
                if(row["Active"] is true&&row["InstanceName"] is { } instance&&row["Temperature"] is not null)
                    for(int i=0;i<3;i++)if(IsDtt(instance.ToString()!,i+1))values[i].Add(Convert.ToDouble(row["Temperature"]));
        }
        Source S(int i)=>new(values[i].Count==1?values[i][0]:null,at);
        return new(S(0),S(1),S(2));
    }
}
