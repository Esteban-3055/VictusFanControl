using System.Diagnostics;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.Esp32Simulator;

public sealed record SimulatorOperatingState(string Mode,string Communication,double PwmPct,string? Revision=null,int? Duty=null,int? Request=null);

/// <summary>Simulated actuator only; no hardware writes. A timer expires the remote lease independently of reads.</summary>
public sealed class SimulatorDeviceState
{
    private readonly object _sync=new();
    private SimulatorOperatingState _state=new("Local","Ready",25);
    private long _lastRequest;
    public SimulatorOperatingState Snapshot{get{lock(_sync)return _state;}}
    public void Apply(bool remote,int? level,ExternalCurveConfiguration? config=null,double? demand=null,bool manual=false)
    {
        lock(_sync)
        {
            int? output=null;
            if(config is not null)
            {
                config.Validate();
                if(remote)
                {
                    var x=config.Input==1?100.0*(level!.Value-config.RequestMinimum)/(config.RequestMaximum-config.RequestMinimum):demand;
                    if(config.Input==1&&(level<config.RequestMinimum||level>config.RequestMaximum)||config.Input==0&&!manual&&x is null)remote=false;
                    if(remote)
                    {
                        output=config.ManualOutput;
                        if(config.Input==1||!manual)
                        {
                            for(var i=1;i<config.Points.Length;i++)if(x<=config.Points[i].Input)
                            {
                                var a=config.Points[i-1];var b=config.Points[i];
                                output=(int)Math.Round(a.Output+(b.Output-a.Output)*(x!.Value-a.Input)/(b.Input-a.Input));break;
                            }
                        }
                    }
                }
            }
            _state=new(remote?"Remote":"Local","Ready",output is{} duty?duty*100.0/255:remote?Math.Clamp(level!.Value,0,100):25,config?.Revision,output,output);
            if(remote)_lastRequest=Stopwatch.GetTimestamp();
        }
    }
    public void Disconnect()
    {lock(_sync)_state=new("Local","Timeout",25);}
    public async Task WatchAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                lock(_sync)
                    if(_state.Mode=="Remote"&&Stopwatch.GetElapsedTime(_lastRequest).TotalMilliseconds>=ExternalCoolingProtocol.RequestValidForMilliseconds)
                        _state=new("Local","Timeout",25);
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
    }
}
