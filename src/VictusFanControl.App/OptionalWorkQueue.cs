namespace VictusFanControl.App;

internal sealed record OptionalWriterStatus(int Entries,int ReservedBytes,long Dropped,long Failures,bool Running);

/// <summary>One optional IO worker. Reservations bound retained payload; never used for journals or control.</summary>
internal sealed class OptionalWorkQueue : IDisposable
{
    private sealed record Entry(Action Write,int Bytes);
    private readonly Queue<Entry> _pending = new();
    private readonly object _sync = new();
    private readonly int _maximumEntries, _maximumBytes;
    private bool _closed, _running;
    private Thread? _thread;
    private int _bytes;
    private long _dropped, _failures;
    internal OptionalWorkQueue(int maximumEntries=256,int maximumBytes=4*1024*1024)
    { _maximumEntries=maximumEntries;_maximumBytes=maximumBytes; }
    internal OptionalWriterStatus Status
    { get { lock(_sync)return new(_pending.Count,_bytes,_dropped,_failures,_running); } }
    internal void RecordDrop(){lock(_sync)_dropped++;}
    internal bool TryEnqueue(Action write,int reservedBytes)
    {
        lock(_sync)
        {
            if(_closed||reservedBytes<=0||reservedBytes>_maximumBytes){_dropped++;return false;}
            while(_pending.Count>=_maximumEntries||_bytes+reservedBytes>_maximumBytes)
            {var old=_pending.Dequeue();_bytes-=old.Bytes;_dropped++;}
            _pending.Enqueue(new(write,reservedBytes));_bytes+=reservedBytes;
            if(_thread is null){_thread=new Thread(Run){IsBackground=true,Name="Optional diagnostic writer"};_thread.Start();}
            Monitor.PulseAll(_sync);return true;
        }
    }
    private void Run()
    {
        while(true)
        {
            Entry next;
            lock(_sync)
            {
                while(!_closed&&_pending.Count==0)Monitor.Wait(_sync);
                if(_closed)return;
                next=_pending.Dequeue();_bytes-=next.Bytes;_running=true;
            }
            try{next.Write();}catch{lock(_sync)_failures++;}
            finally{lock(_sync){_running=false;Monitor.PulseAll(_sync);}}
        }
    }
    internal async Task<bool> FlushAsync(TimeSpan budget)
    {
        var until=Environment.TickCount64+(long)budget.TotalMilliseconds;
        while(true)
        {
            lock(_sync)if(_pending.Count==0&&!_running)return true;
            if(Environment.TickCount64>=until)return false;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
    public void Dispose(){lock(_sync){_closed=true;_pending.Clear();_bytes=0;Monitor.PulseAll(_sync);}}
}

/// <summary>At most one queued latest value and one running presentation callback; no control ownership.</summary>
internal sealed class LatestValuePump<T> : IDisposable where T:class
{
    private readonly OptionalWorkQueue _worker=new(1,1);
    private readonly object _sync=new();
    private readonly Action<T> _deliver;
    private T? _latest;
    private bool _scheduled,_closed;
    private readonly Func<T,long>? _sequence;
    private long _accepted;
    internal LatestValuePump(Action<T> deliver,Func<T,long>? sequence=null){_deliver=deliver;_sequence=sequence;}
    internal void Post(T value)
    {
        lock(_sync)
        {
            if(_closed)return;
            var order=_sequence?.Invoke(value)??0;
            if(order>0&&order<=_accepted)return;
            if(order>0)_accepted=order;
            _latest=value;
            if(_scheduled)return;
            _scheduled=true;_worker.TryEnqueue(Deliver,1);
        }
    }
    private void Deliver()
    {
        T? value;
        lock(_sync){if(_closed)return;value=_latest;_latest=null;}
        try{if(value is not null)_deliver(value);}
        finally
        {
            lock(_sync)
            {
                if(!_closed&&_latest is not null)_worker.TryEnqueue(Deliver,1);
                else _scheduled=false;
            }
        }
    }
    public void Dispose(){lock(_sync){_closed=true;_latest=null;_worker.Dispose();}}
}
