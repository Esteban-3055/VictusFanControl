using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VictusFanControl.App;

// A presentation-only producer. At most one UI callback is pending, and stale
// callbacks are invalidated on pause/handle recreation. No spin or global timer-resolution change.
internal sealed class ProductAnimationFramePump : IDisposable
{
    internal const int RequestedFramesPerSecond = 120;
    private readonly System.Windows.Forms.Control _owner;
    private readonly Action _frame;
    private readonly ManualResetEvent _shutdown = new(false);
    private readonly AutoResetEvent _changed = new(false);
    private Thread? _thread;
    private int _running, _disposed, _pending, _generation;
    internal bool IsRunning => Volatile.Read(ref _running) != 0;
    internal string Scheduler { get; private set; } = "not-started";
    internal long CoalescedRequests;
    internal ProductAnimationFramePump(System.Windows.Forms.Control owner, Action frame) { _owner=owner;_frame=frame; }
    internal void Start()
    {
        if (Volatile.Read(ref _disposed)!=0 || Interlocked.Exchange(ref _running,1)!=0) return;
        Interlocked.Increment(ref _generation);Volatile.Write(ref _pending,0);
        if (_thread is null) { _thread=new(Run){IsBackground=true,Name="Victus Home animation"};_thread.Start(); }
        _changed.Set();
    }
    internal void Stop()
    {
        if (Volatile.Read(ref _disposed)!=0 || Interlocked.Exchange(ref _running,0)==0) return;
        Interlocked.Increment(ref _generation);Volatile.Write(ref _pending,0);_changed.Set();
    }
    private void Run()
    {
        NativeTimer? timer=null;
        try
        {
            var handle=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x00100002);
            if(handle.IsInvalid){handle.Dispose();handle=CreateWaitableTimerEx(IntPtr.Zero,null,0,0x00100002);Scheduler="waitable-fallback";}
            else Scheduler="waitable-high-resolution";
            if(!handle.IsInvalid)timer=new(handle);else{handle.Dispose();Scheduler="wait-timeout-fallback";}
            var period=Stopwatch.Frequency/(double)RequestedFramesPerSecond;
            double next=Stopwatch.GetTimestamp()+period;
            while(!_shutdown.WaitOne(0))
            {
                if(!IsRunning)
                {
                    if(WaitHandle.WaitAny([_shutdown,_changed])==0)break;
                    next=Stopwatch.GetTimestamp()+period;continue;
                }
                int wake;
                var remaining=Math.Max(1,next-Stopwatch.GetTimestamp());
                if(timer is not null)
                {
                    var due=-(long)Math.Max(1,Math.Ceiling(remaining*10_000_000/Stopwatch.Frequency));
                    if(!SetWaitableTimer(timer.SafeWaitHandle,ref due,0,IntPtr.Zero,IntPtr.Zero,false))
                    {timer.Dispose();timer=null;Scheduler="wait-timeout-fallback";continue;}
                    wake=WaitHandle.WaitAny([_shutdown,_changed,timer]);
                }
                else wake=WaitHandle.WaitAny([_shutdown,_changed],Math.Max(1,(int)Math.Ceiling(remaining*1000/Stopwatch.Frequency)));
                if(wake==0)break;
                if(wake==1){next=Stopwatch.GetTimestamp()+period;continue;}
                if(!IsRunning)continue;
                PostFrame();next+=period;
                if(next<Stopwatch.GetTimestamp()-period)next=Stopwatch.GetTimestamp()+period;
            }
        }
        catch(Exception ex){Scheduler="unavailable-"+ex.GetType().Name;Volatile.Write(ref _running,0);}
        finally {timer?.Dispose();}
    }
    private void PostFrame()
    {
        if(!IsRunning)return;
        var generation=Volatile.Read(ref _generation);
        if(Interlocked.CompareExchange(ref _pending,generation,0)!=0){Interlocked.Increment(ref CoalescedRequests);return;}
        try
        {
            _owner.BeginInvoke((Action)(()=>
            {
                try {if(Volatile.Read(ref _disposed)==0&&IsRunning&&generation==Volatile.Read(ref _generation))_frame();}
                finally {Interlocked.CompareExchange(ref _pending,0,generation);}
            }));
        }
        catch(InvalidOperationException){Interlocked.CompareExchange(ref _pending,0,generation);}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        Volatile.Write(ref _running,0);Interlocked.Increment(ref _generation);_shutdown.Set();_changed.Set();
        if(_thread is null||_thread.Join(1000)){_shutdown.Dispose();_changed.Dispose();}
    }
    private sealed class NativeTimer : WaitHandle {internal NativeTimer(SafeWaitHandle handle)=>SafeWaitHandle=handle;}
    [DllImport("kernel32.dll",EntryPoint="CreateWaitableTimerExW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long due,int period,IntPtr callback,IntPtr arg,[MarshalAs(UnmanagedType.Bool)]bool resume);
}
