namespace VictusFanControl.App;

/// <summary>Read-only presentation cache. Fresh native admission still reads exact journals.</summary>
internal sealed class RecoveryPreviewCache : IAsyncDisposable
{
    private sealed record ScanResult(PerformanceRecoveryPreview Value,long CompletedAt);
    private static readonly object SharedSync=new();
    private static Task<ScanResult>? _sharedScan;
    private readonly object _sync=new();
    private readonly Func<PerformanceRecoveryPreview>? _fixtureRead;
    private readonly Func<long> _clock;
    private readonly long _maximumAge;
    private Task<ScanResult>? _scan,_observed;
    private PerformanceRecoveryPreview? _value;
    private long _completedAt;
    private bool _disposed;
    private static readonly PerformanceRecoveryPreview Unknown=new(true,"Comprobando registros de recuperación en segundo plano…"){Checking=true};
    internal RecoveryPreviewCache(Func<PerformanceRecoveryPreview>? read=null,Func<long>? clock=null,long maximumAge=5000)
    {_fixtureRead=read;_clock=clock??(()=>Environment.TickCount64);_maximumAge=maximumAge;}
    private Task<ScanResult> StartScan()
    {
        Task<ScanResult> Start(Func<PerformanceRecoveryPreview> read)=>Task.Factory.StartNew(()=>
        {
            PerformanceRecoveryPreview result;
            try{result=read();}catch(Exception ex){result=new(true,"No se pudo comprobar la recuperación: "+ex.Message){ReadStatus=RecoveryInventoryStatus.Unavailable};}
            return new ScanResult(result,_clock());
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        if(_fixtureRead is not null)return Start(_fixtureRead);
        lock(SharedSync)
        {
            if(_sharedScan is null||_sharedScan.IsCompleted)_sharedScan=Start(()=>PerformanceRecoveryPreview.Read());
            return _sharedScan;
        }
    }
    internal PerformanceRecoveryPreview Get()
    {
        lock(_sync)
        {
            if(_disposed)return Unknown;
            if(_scan is {IsCompletedSuccessfully:true} completed&&!ReferenceEquals(_observed,completed))
            {_observed=completed;_value=completed.Result.Value;_completedAt=completed.Result.CompletedAt;}
            var age=_clock()-_completedAt;
            if((_value is null||age>=_maximumAge/2)&&(_scan is null||_scan.IsCompleted))_scan=StartScan();
            return _value is not null&&age>=0&&age<_maximumAge?_value:Unknown;
        }
    }
    public ValueTask DisposeAsync()
    {
        // Detached read only: no callbacks and no authority. Production shares
        // one pending scan across runtimes; a slow disk cannot multiply readers.
        lock(_sync){_disposed=true;_value=null;_scan=null;_observed=null;}
        return ValueTask.CompletedTask;
    }
}
