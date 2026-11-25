namespace VictusFanControl.Product;

public sealed record ProductFunctionRetryState(bool Pending,int Attempts,long? RetryAtTick,string? Failure,bool NeedsConfirmation,bool Running=false,long TotalAttempts=0);

/// <summary>One bounded retry lane per function. Eligibility and native completion are supplied by its owner.</summary>
public sealed class ProductFunctionRetry
{
    private readonly object _sync=new();
    private int _attempts;
    private bool _pending,_inFlight,_blocked;
    private long _retryAt;
    private string? _failure;
    private long _totalAttempts;
    private long? _healthySince;
    public ProductFunctionRetryState State{get{lock(_sync)return new(_pending,_attempts,_pending&&!_blocked?_retryAt:null,_failure,_blocked,_inFlight,_totalAttempts);}}
    public void Cancel(){lock(_sync){_attempts=0;_pending=false;_inFlight=false;_blocked=false;_failure=null;_healthySince=null;}}
    public void Failed(string failure,long now)
    {lock(_sync){_failure=failure;_inFlight=false;_pending=true;_healthySince=null;_retryAt=now+new[]{10000,30000,60000}[Math.Min(_attempts,2)];}}
    public bool TryBegin(long now,bool eligible)
    {
        lock(_sync)
        {
            if(!_pending||_inFlight||_blocked||!eligible||now<_retryAt||_attempts>=3)return false;
            _inFlight=true;_attempts++;_totalAttempts++;return true;
        }
    }
    public void WaitForConfirmation(string failure){lock(_sync){_failure=failure;_pending=true;_inFlight=false;_blocked=true;}}
    public void ConfirmedFailure(string failure,long now){lock(_sync){_blocked=false;Failed(failure,now);}}
    public void Succeeded(long now){lock(_sync){_pending=false;_inFlight=false;_blocked=false;_failure=null;_healthySince=now;}}
    public void ObserveHealthy(long now,bool healthy)
    {
        lock(_sync)
        {
            if(!healthy||_pending){_healthySince=null;return;}
            if(_healthySince is null||now<_healthySince){_healthySince=now;return;}
            if(now-_healthySince>=60000){_attempts=0;_healthySince=now;}
        }
    }
}
