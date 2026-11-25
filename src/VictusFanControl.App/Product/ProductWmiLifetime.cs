using VictusFanControl.Hardware.Hp;
using VictusFanControl.Performance;
using VictusFanControl.Runtime;
using VictusFanControl.Product;

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
    private long _ctgpRequestGeneration;
    private bool _ctgpDispatchAttempted;
    private readonly ProductFunctionRetry _retry=new();
    private FanWmiReleaseEvidence? _observedRelease;
    internal Func<bool>? CanRestartAfterFailure {get;set;}
    internal ProductFunctionRetryState RecoveryState=>_retry.State;
    internal Func<long> RecoveryClock {get;set;}=()=>Environment.TickCount64;
    private bool NoPendingNativeEvidence=>!Client.HasChild&&!_ctgpDispatchAttempted&&
        !new[]{"native-uncertain.signal","native-inflight.json","write-intent.json"}.Any(name=>File.Exists(Path.Combine(Client.SessionDirectory,name)));
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
    internal Task SetCtgpAsync(bool wanted,bool eligible,CancellationToken token)
    {
        return ApplyCtgpAsync(wanted,eligible,Interlocked.Increment(ref _ctgpRequestGeneration),changeIntent:true,token);
    }
    private async Task ApplyCtgpAsync(bool wanted,bool eligible,long generation,bool changeIntent,CancellationToken token)
    {
        Interlocked.Increment(ref _ctgpPendingCommands);var entered=false;
        try
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);entered=true;
            // A queued retry must not become a new user request. An explicit
            // selection invalidates earlier work before waiting for this queue.
            if(generation!=Volatile.Read(ref _ctgpRequestGeneration)){DeferStaleRetry();return;}
            if(changeIntent&&_retry.State.Pending&&wanted&&eligible)
            {_ctgpWanted=wanted;_ctgpEligible=eligible;return;}
            if(!changeIntent&&(!_ctgpWanted||!_ctgpEligible||_ctgpFaulted||_systemBlocked)){DeferStaleRetry();return;}
            eligible&=!_systemBlocked;
            if(changeIntent){_ctgpWanted=wanted;_ctgpEligible=eligible;}
            if(_ctgpFaulted&&wanted&&eligible)throw new IOException(Failure??"cTGP requiere recuperación antes de reactivarse.");
            if(Client.HasCompletedRelease&&wanted&&eligible&&CanRestartAfterFailure?.Invoke()==false)
            {_retry.Failed("Esperando la retirada confirmada del control de ventiladores.",RecoveryClock());return;}
            if(!wanted){_retry.Cancel();if(Client.HasCompletedRelease)_ctgpFaulted=false;}
            if(!Client.HasChild&&(!wanted||!eligible))return;
            await StartAsync(token).ConfigureAwait(false);
            // Startup may have awaited while the UI received a power fence.
            // Never let a queued pre-suspend request acquire policy afterwards.
            if(generation!=Volatile.Read(ref _ctgpRequestGeneration)){DeferStaleRetry();return;}
            eligible&=!_systemBlocked;_ctgpEligible=eligible;
            _ctgpDispatchAttempted=true;
            await Client.SetCtgpAsync(wanted&&eligible,token).ConfigureAwait(false);
            Failure=null;if(!wanted){_ctgpFaulted=false;_ctgpDispatchAttempted=false;}
            if(wanted&&eligible)_retry.Succeeded(RecoveryClock());
        }
        catch(CtgpActivationDeferredException){Failure=null;if(_retry.State.Pending)_retry.Failed("WMI ocupado; activación aplazada.",RecoveryClock());}
        catch(OperationCanceledException) when(entered&&NoPendingNativeEvidence)
        { _ctgpEligible=false;Failure=null;throw; }
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
                    await ReconcileAsync().ConfigureAwait(false);
                }
                catch(OperationCanceledException)when(_stop.IsCancellationRequested){return;}
                catch(Exception error)
                {
                    Failure=string.IsNullOrWhiteSpace(error.Message)?error.GetType().Name:error.Message;
                    _ctgpFaulted|=Client.Ctgp?.RequiresManualRecovery==true;
                    if(_ctgpWanted&&Client.ChildExited&&!Client.HasCompletedRelease)_retry.WaitForConfirmation(Failure);
                }
                Notify();
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    private void DeferStaleRetry(){if(_retry.State.Running)_retry.Failed("Solicitud cambió; reintento aplazado.",RecoveryClock());}
    internal async Task ReconcileAsync()
    {
        if(_closing)return;
        if(Client.ChildExited||Client.CompletedRelease is{} completed&&!ReferenceEquals(completed,_observedRelease))
        {await ObserveCompletedExitAsync().ConfigureAwait(false);Notify();return;}
        if(_retry.State.Pending&&!_ctgpFaulted&&_ctgpWanted&&_ctgpEligible&&!_systemBlocked&&source.Read().Source==PerformancePowerSourceKind.Ac&&
            _retry.TryBegin(RecoveryClock(),CanRestartAfterFailure?.Invoke()==true))
        {
            var requestGeneration=Volatile.Read(ref _ctgpRequestGeneration);
            try{await ApplyCtgpAsync(true,true,requestGeneration,changeIntent:false,_stop.Token).ConfigureAwait(false);}
            catch(Exception retryError)when(retryError is not OperationCanceledException)
            {_retry.Failed(retryError.Message,RecoveryClock());Failure=retryError.Message;}
        }
        if(!Client.HasChild){Notify();return;}
        Client.EnsureAlive();Client.Heartbeat();
        var status=Client.Ctgp;
        _retry.ObserveHealthy(RecoveryClock(),_ctgpWanted&&status is{Active:true,Managed:true,Failure:null}&&status.FreshAt(DateTimeOffset.UtcNow));
        if(status?.RequiresManualRecovery==true){_ctgpFaulted=true;Failure=status.Failure;}
        var generation=Volatile.Read(ref _ctgpRequestGeneration);
        if(!_retry.State.Pending&&!_ctgpFaulted&&_ctgpWanted&&_ctgpEligible&&status is{State:"AcOnly" or "Waiting"}&&source.Read().Source==PerformancePowerSourceKind.Ac)
            await ApplyCtgpAsync(true,true,generation,changeIntent:false,_stop.Token).ConfigureAwait(false);
    }
    private async Task ObserveCompletedExitAsync()
    {
        await _commands.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            if(!Client.ChildExited&&(Client.CompletedRelease is not{} completed||ReferenceEquals(completed,_observedRelease)))return;
            var failed=Client.Ctgp;
            var evidence=await Client.ConfirmCompletedReleaseAsync(_stop.Token).ConfigureAwait(false);
            _observedRelease=evidence;
            failed??=evidence.SupervisionCtgp;
            _ctgpDispatchAttempted=false;_ctgpEligible=false;
            _ctgpFaulted=failed?.RequiresManualRecovery==true;
            Failure=failed?.Failure;
            if(_ctgpWanted&&failed?.AllowsAutomaticRetry==true)
                _retry.ConfirmedFailure(failed.Failure??"cTGP espera una nueva observación.",RecoveryClock());
            else _retry.Cancel();
        }
        finally{_commands.Release();}
    }
    private void Notify()
    {var observers=Changed;if(observers is not null)foreach(Action observer in observers.GetInvocationList())try{observer();}catch(Exception e){AppLog.Write("WMI presentation observer failed: "+e.GetType().Name);}}
    internal void EnsureTransferAllowed()
    {
        lock(_sync)if(_closing)throw new IOException("El supervisor WMI ya está cerrándose.");
        if(File.Exists(Path.Combine(Client.SessionDirectory,"native-uncertain.signal"))||File.Exists(Path.Combine(Client.SessionDirectory,"write-intent.json")))
            throw new IOException("La sesión WMI conserva una operación incierta o ventiladores pendientes.");
        if(Client.HasChild)Client.EnsureAlive();
        if(_ctgpFaulted&&!Client.HasCompletedRelease&&Client.Ctgp is not{Managed:false,Active:false})throw new IOException(Failure??"cTGP requiere recuperación.");
        if(!_ctgpFaulted&&!_retry.State.Pending&&_ctgpWanted&&_ctgpEligible&&source.Read().Source==PerformancePowerSourceKind.Ac&&
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
            if(policyFailure is not null&&!evidence.GuardianLeaseRetired&&Client.Ctgp is not{Managed:false,Active:false})throw policyFailure;
            return evidence;
        }
        public Task<FanWmiReleaseEvidence> ConfirmCompletedReleaseAsync(CancellationToken token)=>Client.ConfirmCompletedReleaseAsync(token);
        public Task SetCtgpAsync(bool enabled,CancellationToken token)=>lifetime.SetCtgpAsync(enabled,true,token);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
