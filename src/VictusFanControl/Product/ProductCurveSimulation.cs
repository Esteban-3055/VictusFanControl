using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.Product;

/// <summary>Synthetic demand inputs. CPU temperature is the configured demand signal, not raw safety telemetry.</summary>
public sealed record ProductSimulationInputs(int CpuTemperature=40,int GpuTemperature=35,int CpuPower=10,int GpuPower=10,int CpuLoad=10,int GpuLoad=10)
{
    public int Value(int axis)=>axis switch{0=>CpuTemperature,1=>GpuTemperature,2=>CpuPower,3=>GpuPower,4=>CpuLoad,5=>GpuLoad,_=>throw new ArgumentOutOfRangeException(nameof(axis))};
    public ProductSimulationInputs With(int axis,int value)
    {
        value=Math.Clamp(value,0,(int)AdaptiveCurveProfiles.MaximumInput((AdaptiveCurveAxis)axis));
        return axis switch{0=>this with{CpuTemperature=value},1=>this with{GpuTemperature=value},2=>this with{CpuPower=value},3=>this with{GpuPower=value},4=>this with{CpuLoad=value},5=>this with{GpuLoad=value},_=>throw new ArgumentOutOfRangeException(nameof(axis))};
    }
}
public sealed record ProductSimulationPoint(int Seconds,AdaptiveFanInertiaDecision Decision);

/// <summary>Pure prepared-policy simulation. No runtime, hardware identity, sensors, Guardian or authority.</summary>
public sealed class ProductCurveSimulation
{
    private readonly AdaptiveFanInertiaPolicy _engine;
    private readonly List<ProductSimulationPoint> _history=[];
    public const int MaximumSeconds=86400;
    public int ElapsedSeconds {get;private set;}
    public AdaptiveFanInertiaDecision? Current {get;private set;}
    public IReadOnlyList<ProductSimulationPoint> History=>_history.AsReadOnly();
    public ProductCurveSimulation(FanConfiguration configuration)
    {
        var copy=FanConfigurationStore.Copy(configuration);
        _engine=new(Hp8C40AutomaticPolicy.Create(copy.BuildPolicy()),copy.Tuning);
    }
    public void Advance(ProductSimulationInputs inputs,int seconds)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        for(int axis=0;axis<6;axis++)if(inputs.Value(axis)<0||inputs.Value(axis)>AdaptiveCurveProfiles.MaximumInput((AdaptiveCurveAxis)axis))throw new ArgumentOutOfRangeException(nameof(inputs));
        if(seconds<1||seconds>3600||ElapsedSeconds>MaximumSeconds-seconds)throw new ArgumentOutOfRangeException(nameof(seconds),"Reinicia la simulación tras 24 horas virtuales.");
        for(int i=0;i<seconds;i++)
        {
            var time=ElapsedSeconds+1;
            var decision=_engine.Evaluate(new(DateTimeOffset.UnixEpoch.AddSeconds(time),inputs.CpuTemperature,inputs.CpuPower,inputs.CpuLoad,inputs.GpuTemperature,inputs.GpuPower,inputs.GpuLoad));
            if(!decision.Accepted)throw new InvalidOperationException(decision.Detail);
            Current=decision;ElapsedSeconds=time;_history.Add(new(time,decision));
        }
        if(_history.Count>600)_history.RemoveRange(0,_history.Count-600);
    }
}
