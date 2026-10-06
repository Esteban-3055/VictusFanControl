namespace VictusFanControl.Control.Adaptive;

/// <summary>One demand-to-level curve. Influences are independent gains, never averaging weights.</summary>
public sealed record UnifiedFanDemand
{
    public int SchemaVersion { get; init; } = 1;
    public int CpuTemperatureInfluence { get; init; } = 100;
    public int GpuTemperatureInfluence { get; init; } = 100;
    public int CpuPowerInfluence { get; init; } = 40;
    public int GpuPowerInfluence { get; init; } = 60;
    public int CpuLoadInfluence { get; init; } = 20;
    public int GpuLoadInfluence { get; init; } = 20;
    public IReadOnlyList<AdaptiveFanCurvePoint> Curve { get; init; } =
        [new(0,12),new(20,12),new(40,21),new(60,28),new(76,35),new(90,44),new(100,50)];
    public static UnifiedFanDemand Default(bool battery) => battery ? new()
    {
        CpuPowerInfluence=20,GpuPowerInfluence=35,CpuLoadInfluence=10,GpuLoadInfluence=10,
        Curve=[new(0,10),new(25,10),new(40,12),new(60,24),new(76,35),new(90,44),new(100,50)]
    } : new();
    public int Influence(int axis)=>axis switch
    {0=>CpuTemperatureInfluence,1=>GpuTemperatureInfluence,2=>CpuPowerInfluence,3=>GpuPowerInfluence,4=>CpuLoadInfluence,5=>GpuLoadInfluence,_=>throw new ArgumentOutOfRangeException(nameof(axis))};
    public UnifiedFanDemand WithInfluence(int axis,int value)=>axis switch
    {0=>this with{CpuTemperatureInfluence=value},1=>this with{GpuTemperatureInfluence=value},2=>this with{CpuPowerInfluence=value},3=>this with{GpuPowerInfluence=value},4=>this with{CpuLoadInfluence=value},5=>this with{GpuLoadInfluence=value},_=>throw new ArgumentOutOfRangeException(nameof(axis))};
    public void Validate()
    {
        if(SchemaVersion!=1)throw new InvalidDataException("Versión de demanda incompatible.");
        for(int i=0;i<6;i++)if(Influence(i)<(i<2?100:0)||Influence(i)>(i<2?150:100))
            throw new InvalidDataException("Influencia térmica 100–150 %; potencia/carga 0–100 %.");
        if(Curve is null||Curve.Count is <2 or >64||Curve.Any(p=>p is null)||Curve[0].Input!=0||Curve[^1].Input!=100||Curve[^1].Level!=50)
            throw new InvalidDataException("Curva de demanda: 2–64 puntos, extremos 0/100 % y nivel final 50.");
        double previous=-1,previousLevel=10;
        foreach(var p in Curve)
        {
            if(!double.IsFinite(p.Input)||!double.IsFinite(p.Level)||p.Input!=Math.Truncate(p.Input)||p.Level!=Math.Truncate(p.Level)||p.Input<=previous||p.Input>100||p.Level<previousLevel||p.Level>50)
                throw new InvalidDataException("Demanda creciente 0–100 %, niveles no decrecientes 10–50.");
            previous=p.Input;previousLevel=p.Level;
        }
    }
    public UnifiedDemandObservation Evaluate(AdaptiveFanPolicyInput input)
    {
        Validate();
        if(!AdaptiveFanPolicyEngine.ValidateInput(input,out var failure))throw new ArgumentException(failure,nameof(input));
        static double Scale(double value,double cold,double hot)=>Math.Clamp((value-cold)/(hot-cold)*100,0,100);
        // Absolute references: changing PL1/PL2 or clocks never remaps measured watts.
        double[] normalized=[Scale(input.CpuEffectiveTemperatureC,40,90),Scale(input.GpuTemperatureC,35,81),Scale(input.CpuPackagePowerW,0,60),Scale(input.GpuPowerW,0,75),input.CpuLoadPercent,input.GpuLoadPercent];
        var contributions=normalized.Select((v,i)=>Math.Clamp(v*Influence(i)/100,0,100)).ToArray();
        var rawCpu=input.CpuRawControlTemperatureC??input.CpuEffectiveTemperatureC;
        var cpuFloor=rawCpu>=90?50:rawCpu>=85?44:10;
        var gpuFloor=input.GpuTemperatureC>=81?50:input.GpuTemperatureC>=78?44:10;
        if(cpuFloor>10)contributions[0]=Math.Max(contributions[0],cpuFloor==50?100:90);
        if(gpuFloor>10)contributions[1]=Math.Max(contributions[1],gpuFloor==50?100:90);
        var dominant=Array.IndexOf(contributions,contributions.Max());var percent=contributions[dominant];
        var level=Math.Max(AdaptiveFanPolicyEngine.Interpolate(Curve,percent),Math.Max(cpuFloor,gpuFloor));
        return new(percent,level,dominant,Math.Max(cpuFloor,gpuFloor)>10,contributions[0],contributions[1],contributions[2],contributions[3],contributions[4],contributions[5]);
    }
}
public sealed record UnifiedDemandObservation(double Percent,double Level,int DominantVariable,bool ThermalProtection,
    double CpuTemperature,double GpuTemperature,double CpuPower,double GpuPower,double CpuLoad,double GpuLoad)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<double> Contributions=>[CpuTemperature,GpuTemperature,CpuPower,GpuPower,CpuLoad,GpuLoad];
}
