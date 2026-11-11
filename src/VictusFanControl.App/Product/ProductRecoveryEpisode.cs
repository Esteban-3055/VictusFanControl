namespace VictusFanControl.App;

/// <summary>Shared budget across native recovery, runtime replacement and Automatic preparation.</summary>
internal sealed class ProductRecoveryEpisode
{
    internal const int StableMilliseconds=30000,ResetMilliseconds=600000,CooldownMilliseconds=300000;
    private int _attempts;
    private bool _reserved,_uncertain,_preparationCredit;
    private long _cooldownUntil;
    private DateTimeOffset? _firstFresh,_lastFresh,_automaticSince;
    private string? _source;
    internal int Attempts=>_attempts;
    internal bool Reserved=>_reserved;
    internal bool Uncertain=>_uncertain;
    internal long Generation {get;private set;}
    internal int DelayMilliseconds=>new[]{10000,30000,60000}[Math.Min(_attempts,2)];
    internal bool Stable=>_firstFresh.HasValue&&_lastFresh-_firstFresh>=TimeSpan.FromMilliseconds(StableMilliseconds);
    internal bool Observe(ProductRuntimeState state,DateTimeOffset now)
    {
        var sample=state.Snapshot;
        var valid=state.Runtime=="Healthy"&&sample is {IsComplete:true,RetainedTelemetry:null}&&
            sample.HasFreshControlSensorsAt(now)&&sample.IsFanTelemetryFreshAt(now,3000)&&
            now>=sample.Timestamp&&now-sample.Timestamp<TimeSpan.FromSeconds(3)&&state.Source is "Ac" or "Battery"&&
            ProductAutomaticAdmission.TelemetryIssues(sample,now,startup:true).Count==0;
        if(!valid){_firstFresh=null;_lastFresh=null;_automaticSince=null;return false;}
        if(_lastFresh==sample!.Timestamp)return false; // A UI timer never renews stability.
        if(_source!=state.Source||_lastFresh is {} previous&&(sample!.Timestamp<previous||sample.Timestamp-previous>TimeSpan.FromSeconds(3)))
        {_firstFresh=null;_automaticSince=null;}
        _source=state.Source;_firstFresh??=sample!.Timestamp;_lastFresh=sample!.Timestamp;
        if(state.FanMode=="Automatic"&&state.FanAuthority=="Custom"&&state.AutomaticAuthorized&&!state.LifecycleBlocked)
        {
            _automaticSince??=sample.Timestamp;
            if(!_uncertain&&sample.Timestamp-_automaticSince>=TimeSpan.FromMilliseconds(ResetMilliseconds))
            {_attempts=0;_reserved=false;_cooldownUntil=0;Generation++;_automaticSince=sample.Timestamp;return true;}
        }
        else _automaticSince=null;
        return false;
    }
    internal bool TryReserve(long now)
    {
        if(_reserved||_uncertain)return false;
        if(_attempts>=3)
        {
            if(now<_cooldownUntil||!Stable)return false;
            _attempts=2; // Only one new attempt after cooldown and observed stability.
        }
        _reserved=true;_attempts++;
        _preparationCredit=false;
        if(_attempts>=3)_cooldownUntil=now+CooldownMilliseconds;
        return true;
    }
    internal void Finish(bool ownerConfirmed=true)
    {_reserved=false;if(!ownerConfirmed)_uncertain=true;}
    internal void ConfirmOwnerReleased(){_uncertain=false;}
    internal void CompleteUserActivation()
    {
        if(_uncertain)return;
        _reserved=false;_preparationCredit=false;_attempts=0;_cooldownUntil=0;Generation++;
    }
    internal void CarryPreparation(){_reserved=false;_preparationCredit=true;}
    internal bool TryPrepare(long now)
    {
        if(_uncertain)return false;
        if(_reserved)return true;
        if(_preparationCredit){_preparationCredit=false;_reserved=true;return true;}
        return TryReserve(now);
    }
    internal void DeferRead(){if(_reserved){_reserved=false;_attempts=Math.Max(0,_attempts-1);}}
    internal object Diagnostic()=>new{Generation,attempts=_attempts,reserved=_reserved,nativeReleaseUncertain=_uncertain,Stable,
        cooldownUntilTick=_cooldownUntil,firstFresh=_firstFresh,lastFresh=_lastFresh,automaticSince=_automaticSince};
}
