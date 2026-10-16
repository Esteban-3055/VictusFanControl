using System.Diagnostics;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.Esp32Simulator;

public sealed record SimulatorOperatingState(string Mode,string Communication,double PwmPct);

/// <summary>Simulated actuator only; no hardware writes. A timer expires the remote lease independently of reads.</summary>
public sealed class SimulatorDeviceState
{
    private readonly object _sync=new();
    private SimulatorOperatingState _state=new("Local","Ready",25);
    private long _lastRequest;
    public SimulatorOperatingState Snapshot{get{lock(_sync)return _state;}}
    public void Apply(bool remote,int? level)
    {
        lock(_sync)
        {
            _state=new(remote?"Remote":"Local","Ready",remote?Math.Clamp(level!.Value,0,100):25);
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
