using VictusFanControl.Hardware.Hp;
using VictusFanControl.Performance;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

// One physical boundary, one detached guardian, separate fan and cTGP intent.
// The heartbeat is independent from fan telemetry and presentation.
internal sealed class ProductWmiLifetime(WmiFanGuiGuardianClient client,IPerformancePowerSourceReader source) : IAsyncDisposable
{
    private readonly SemaphoreSlim _commands=new(1,1);
    private readonly SemaphoreSlim _startup=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly object _sync=new();
    private Task? _poll,_dispose;
    private bool _closing,_ctgpWanted,_ctgpEligible,_ctgpFaulted;
    private volatile bool _systemBlocked;
    private int _ctgpPendingCommands;
    internal WmiFanGuiGuardianClient Client {get;}=client;
    internal bool CtgpWanted=>Volatile.Read(ref _ctgpWanted);
    internal bool CtgpEligible=>Volatile.Read(ref _ctgpEligible);
    internal bool CtgpChanging=>Volatile.Read(ref _ctgpPendingCommands)>0;
    internal bool CtgpFaulted=>Volatile.Read(ref _ctgpFaulted);
    internal bool KeepCtgpAfterFanRelease {get;set;}
    internal string? Failure {get;private set;}
    internal event Action? Changed;
    internal IWmiFanGuiGuardian FanLease=>new FanDomainLease(this);
    internal void FenceForLifecycle()=>_systemBlocked=true;
    internal void ResumeAfterLifecycle()=>_systemBlocked=false;
    internal async Task StartAsync(CancellationToken token)
    {
        await _startup.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock(_sync)if(_closing)throw new ObjectDisposedException(nameof(ProductWmiLifetime));
            await Client.StartAsync(token).ConfigureAwait(false);
            lock(_sync)_poll??=Task.Run(PollAsync);
        }
        finally{_startup.Release();}
    }
    internal async Task SetCtgpAsync(bool wanted,bool eligible,CancellationToken token)
    {
        Interlocked.Increment(ref _ctgpPendingCommands);var entered=false;
        try
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);entered=true;
            eligible&=!_systemBlocked;
            _ctgpWanted=wanted;_ctgpEligible=eligible;
            if(_ctgpFaulted&&wanted&&eligible)throw new IOException(Failure??"cTGP requiere recuperación antes de reactivarse.");
            if(!Client.HasChild&&(!wanted||!eligible))return;
            await StartAsync(token).ConfigureAwait(false);
            // Startup may have awaited while the UI received a power fence.
            // Never let a queued pre-suspend request acquire policy afterwards.
            eligible&=!_systemBlocked;_ctgpEligible=eligible;
            await Client.SetCtgpAsync(wanted&&eligible,token).ConfigureAwait(false);
            Failure=null;if(!wanted)_ctgpFaulted=false;
        }
        catch(CtgpActivationDeferredException){Failure=null;}
        catch(Exception error)
        {
            if(entered){_ctgpFaulted=true;Failure=error.Message;}throw;
        }
        finally{if(entered)_commands.Release();Interlocked.Decrement(ref _ctgpPendingCommands);Notify();}
    }
    private async Task PollAsync()
    {
        try
        {
            while(true)
            {
                await Task.Delay(1000,_stop.Token).ConfigureAwait(false);
                try
                {
                    Client.EnsureAlive();Client.Heartbeat();
                    var status=Client.Ctgp;
                    if(status?.RequiresManualRecovery==true){_ctgpFaulted=true;Failure=status.Failure;}
                    if(!_ctgpFaulted&&_ctgpWanted&&_ctgpEligible&&status is{State:"AcOnly" or "Waiting"}&&source.Read().Source==PerformancePowerSourceKind.Ac)
                        await SetCtgpAsync(true,true,_stop.Token).ConfigureAwait(false);
                }
                catch(OperationCanceledException)when(_stop.IsCancellationRequested){return;}
                catch(Exception error){Failure=error.Message;if(_ctgpWanted)_ctgpFaulted=true;}
                Notify();
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    private void Notify()
    {var observers=Changed;if(observers is not null)foreach(Action observer in observers.GetInvocationList())try{observer();}catch(Exception e){AppLog.Write("WMI presentation observer failed: "+e.GetType().Name);}}
    internal void EnsureTransferAllowed()
    {
        lock(_sync)if(_closing)throw new IOException("El supervisor WMI ya está cerrándose.");
        if(File.Exists(Path.Combine(Client.SessionDirectory,"native-uncertain.signal"))||File.Exists(Path.Combine(Client.SessionDirectory,"write-intent.json")))
            throw new IOException("La sesión WMI conserva una operación incierta o ventiladores pendientes.");
        if(Client.HasChild)Client.EnsureAlive();
        if(_ctgpFaulted&&Client.Ctgp is not{Managed:false,Active:false})throw new IOException(Failure??"cTGP requiere recuperación.");
        if(!_ctgpFaulted&&_ctgpWanted&&_ctgpEligible&&source.Read().Source==PerformancePowerSourceKind.Ac&&
            Client.Ctgp is not{Active:true,Managed:true,Failure:null})throw new IOException("cTGP no tiene confirmación suficiente para conservar su sesión.");
    }
    public ValueTask DisposeAsync()
    {
        lock(_sync)
        {
            if(_dispose is null){_closing=true;_stop.Cancel();_dispose=Task.Run(CloseAsync);}
            return new(_dispose);
        }
    }
    internal async Task DrainFanReadsAsync()
    {
        if(Client.Fixture)return;
        WmiFanExperimentBoundary.BeginFanRelease();
        async Task Confirm()
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await VictusFanControl.Telemetry.HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(timeout.Token).ConfigureAwait(false);
            await Task.Run(()=>{using var slot=WmiFanExperimentBoundary.EnterRecoverySlot(Client.SessionDirectory,TimeSpan.FromSeconds(10),waitForConfirmedCleanup:true);}).ConfigureAwait(false);
            WmiFanExperimentBoundary.CompleteFanRelease();
        }
        try{await Confirm().ConfigureAwait(false);}
        catch(OperationCanceledException error){throw new WmiFanReadDrainPendingException(error,Confirm);}
    }
    private async Task CloseAsync()
    {
        try
        {
            if(_poll is not null)await _poll.ConfigureAwait(false);
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                await _startup.WaitAsync().ConfigureAwait(false);
                try
                {
                    try{await Client.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);}
                    finally{await Client.DisposeAsync().ConfigureAwait(false);}
                }
                finally{_startup.Release();}
            }
            finally{_commands.Release();}
        }
        finally{_stop.Dispose();}
    }
    private sealed class FanDomainLease(ProductWmiLifetime lifetime) : IWmiFanGuiGuardian
    {
        private WmiFanGuiGuardianClient Client=>lifetime.Client;
        public string SessionDirectory=>Client.SessionDirectory;
        public string ReportPath=>Client.ReportPath;
        public Task StartAsync(CancellationToken token)=>lifetime.StartAsync(token);
        public void BeginFanSession()=>Client.BeginFanSession();
        public void EnsureAlive()=>Client.EnsureAlive();
        public void PersistIntent(int level)=>Client.PersistIntent(level);
        public void Heartbeat()=>Client.Heartbeat();
        public async Task<FanWmiReleaseEvidence> ReleaseAsync(CancellationToken token)
        {
            Exception? policyFailure=null;
            if(!lifetime.KeepCtgpAfterFanRelease&&lifetime.CtgpWanted)
                try{await lifetime.SetCtgpAsync(lifetime.CtgpWanted,false,token).ConfigureAwait(false);}catch(Exception error){policyFailure=error;}
            var evidence=await Client.ReleaseFanDomainAsync(token).ConfigureAwait(false);
            if(policyFailure is not null&&Client.Ctgp is not{Managed:false,Active:false})throw policyFailure;
            return evidence;
        }
        public Task<FanWmiReleaseEvidence> ConfirmCompletedReleaseAsync(CancellationToken token)=>Client.ConfirmCompletedReleaseAsync(token);
        public Task SetCtgpAsync(bool enabled,CancellationToken token)=>lifetime.SetCtgpAsync(enabled,true,token);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
