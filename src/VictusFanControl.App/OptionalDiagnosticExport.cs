namespace VictusFanControl.App;

internal static class OptionalDiagnosticExport
{
    private static readonly object Sync=new();
    private static Task? _active;
    internal static bool Pending {get{lock(Sync)return _active is {IsCompleted:false};}}
    // Only detached immutable diagnostic inputs may be captured. No runtime, form or control callbacks.
    internal static Task<bool> TryStart(Action export)
    {
        lock(Sync)
        {
            if(_active is {IsCompleted:false})return Task.FromResult(false);
            var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _active=done.Task;
            new Thread(()=>
            {
                try{export();done.TrySetResult(true);}
                catch(Exception error){AppLog.Write("Optional diagnostic export unavailable: "+error.GetType().Name);done.TrySetResult(false);}
            }){IsBackground=true,Name="Optional diagnostic export"}.Start();
            return done.Task;
        }
    }
}
