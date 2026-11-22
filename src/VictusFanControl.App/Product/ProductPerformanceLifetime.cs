namespace VictusFanControl.App;

// The detached CPU/GPU owner and its read-only polling outlive a fan runtime.
// Only global cleanup closes this service. It never starts a limit by itself.
internal sealed class ProductPerformanceLifetime(PerformanceGuardianClient client) : IAsyncDisposable
{
    private readonly object _sync=new();
    private readonly CancellationTokenSource _stop=new();
    private Task? _poll,_dispose;
    private bool _closing;
    internal PerformanceGuardianClient Client {get;}=client;
    internal bool Closing {get{lock(_sync)return _closing;}}
    internal event Action<Exception?>? Changed;
    internal void Start()
    {
        lock(_sync)
        {
            if(_closing)throw new ObjectDisposedException(nameof(ProductPerformanceLifetime));
            _poll??=Task.Run(PollAsync);
        }
    }
    private async Task PollAsync()
    {
        try
        {
            while(true)
            {
                await Task.Delay(2000,_stop.Token).ConfigureAwait(false);
                if(!Client.HasProcess)continue;
                Exception? failure=null;
                try{await Client.StatusAsync(_stop.Token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(_stop.IsCancellationRequested){return;}
                catch(Exception error){failure=error;}
                var observers=Changed;
                if(observers is not null)foreach(Action<Exception?> observer in observers.GetInvocationList())
                    try{observer(failure);}catch(Exception error){AppLog.Write("Performance presentation observer failed: "+error.GetType().Name);}
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    public ValueTask DisposeAsync()
    {
        lock(_sync)
        {
            if(_dispose is null){_closing=true;_stop.Cancel();_dispose=Task.Run(CloseAsync);}
            return new(_dispose);
        }
    }
    private async Task CloseAsync()
    {
        try
        {
            if(_poll is not null)await _poll.ConfigureAwait(false);
            await Client.CloseAsync().ConfigureAwait(false);
        }
        finally{_stop.Dispose();}
    }
}
