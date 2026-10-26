namespace VictusFanControl.ExternalCooling;

/// <summary>Authenticated display-only preferences, never actuator authority.</summary>
public sealed record HyperxDisplayConfiguration
{
    public bool LowEnabled {get;init;}=true;
    public bool CriticalEnabled {get;init;}=true;
    public int LowPercent {get;init;}=20;
    public int CriticalPercent {get;init;}=10;
    public int LowDurationMs {get;init;}=6000;
    public int CriticalDurationMs {get;init;}=10000;
    public int LowBlinkCycles {get;init;}=3;
    public int CriticalBlinkCycles {get;init;}=5;
    public void Validate()
    {
        if(CriticalPercent is <10 or >98||LowPercent<=CriticalPercent||LowPercent>99)
            throw new ArgumentException("Umbrales LCD HyperX inválidos.");
        foreach(var (duration,cycles) in new[]{(LowDurationMs,LowBlinkCycles),(CriticalDurationMs,CriticalBlinkCycles)})
            if(duration is <1000 or >120000||cycles is <1 or >20||duration<cycles*1000)
                throw new ArgumentException("LCD HyperX: duración 1–120 s, 1–20 ciclos y al menos 1 s por ciclo.");
    }
}
