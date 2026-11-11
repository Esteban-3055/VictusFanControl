namespace VictusFanControl.App;

internal sealed class LatestUiMailbox<T>(Action<Action> schedule,Action<T> deliver,Func<T,long>? sequence=null) : IDisposable where T:class
{
    private readonly object _sync=new();
    private T? _latest;
    private bool _pending,_closed;
    private long _accepted;
    internal int PendingCallbacks {get{lock(_sync)return _pending?1:0;}}
    internal void DiscardBefore(long minimum)
    {lock(_sync){_latest=null;_accepted=Math.Max(_accepted,minimum-1);}}
    internal void Post(T value)
    {
        lock(_sync)
        {
            if(_closed)return;
            var order=sequence?.Invoke(value)??0;
            if(order>0&&order<=_accepted)return;
            if(order>0)_accepted=order;
            _latest=value;if(_pending)return;_pending=true;
            try{schedule(Drain);}catch(InvalidOperationException){_pending=false;_latest=null;}
        }
    }
    private void Drain()
    {
        T? next;
        lock(_sync){_pending=false;if(_closed)return;next=_latest;_latest=null;}
        if(next is not null)deliver(next);
    }
    public void Dispose(){lock(_sync){_closed=true;_latest=null;}}
}
