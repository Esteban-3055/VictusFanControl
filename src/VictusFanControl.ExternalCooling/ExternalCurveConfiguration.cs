namespace VictusFanControl.ExternalCooling;

public sealed record ExternalCurvePoint(int Input,int Output);
/// <summary>External device configuration. Windows never maps the notebook request to PWM.</summary>
public sealed record ExternalCurveConfiguration
{
    public int SchemaVersion {get;init;}=1;
    public string Revision {get;init;}="00000000000000000000000000000001";
    public ExternalCurvePoint[] Points {get;init;}=[new(0,30),new(25,70),new(50,130),new(75,195),new(100,255)];
    // 0: notebook demand; 1: accepted notebook request normalized by explicit calibration.
    public int Input {get;init;}
    public int RequestMinimum {get;init;}=0;
    public int RequestMaximum {get;init;}=54;
    public int ManualOutput {get;init;}=130;
    public int Rise {get;init;}=12;
    public int Fall {get;init;}=6;
    public int FilterMs {get;init;}=1500;
    public int Hysteresis {get;init;}=3;
    public int DownHoldMs {get;init;}=3000;
    public int StartMs {get;init;}=800;
    public int StartOutput {get;init;}=255;
    // Explicit opt-in: preserve the curve, input/calibration and Manual output.
    // These are the slowest supported ramps and longest supported EMA/hold.
    public ExternalCurveConfiguration WithMaximumSmoothing() => this with
    {
        Rise=1,Fall=1,FilterMs=10000,Hysteresis=3,DownHoldMs=30000,StartMs=0,StartOutput=0
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RequiresFullOutputRange => ManualOutput<30 || StartOutput<30 || Points.Any(p=>p.Output<30);
    public bool CanSendTo(DeviceStatus? device) => device?.ExternalCurveVersion==1 &&
        (!RequiresFullOutputRange || device.ExternalMinimumOutput==0);
    // UI preview only. The ESP32 remains the actuator/controller.
    public double Evaluate(double input) {
        input=Math.Clamp(input,0,100);
        for(int i=1;i<Points.Length;i++) if(input<=Points[i].Input) {
            var a=Points[i-1];var b=Points[i];
            return a.Output+(b.Output-a.Output)*(input-a.Input)/(b.Input-a.Input);
        }
        return Points[^1].Output;
    }
    public bool Equals(ExternalCurveConfiguration? other)=>other is not null&&SchemaVersion==other.SchemaVersion&&Revision==other.Revision&&
        Input==other.Input&&RequestMinimum==other.RequestMinimum&&RequestMaximum==other.RequestMaximum&&ManualOutput==other.ManualOutput&&
        Rise==other.Rise&&Fall==other.Fall&&FilterMs==other.FilterMs&&Hysteresis==other.Hysteresis&&DownHoldMs==other.DownHoldMs&&
        StartMs==other.StartMs&&StartOutput==other.StartOutput&&Points is not null&&other.Points is not null&&Points.SequenceEqual(other.Points);
    public override int GetHashCode()=>Revision?.GetHashCode(StringComparison.Ordinal)??0;
    public void Validate()
    {
        if(SchemaVersion!=1||Revision is null||Revision.Length!=32||Revision.Any(c=>!char.IsAsciiDigit(c)&&c is not(>='a' and <='f'))||
           Points is null||Points.Length is <2 or >16||Input is <0 or >1||RequestMinimum<0||RequestMaximum>65535||RequestMinimum>=RequestMaximum||
           ManualOutput is <0 or >255||Rise is <1 or >100||Fall is <1 or >100||FilterMs is <0 or >10000||Hysteresis is <0 or >30||
           DownHoldMs is <0 or >30000||StartMs is <0 or >3000||StartOutput is <0 or >255)
            throw new ArgumentException("Configuración de curva externa no válida.");
        for(var i=0;i<Points.Length;i++)
            if(Points[i] is not {} p||p.Input is <0 or >100||p.Output is <0 or >255||
               i>0&&(p.Input<=Points[i-1].Input||p.Output<Points[i-1].Output))
                throw new ArgumentException("Los puntos deben ascender: entrada 0–100 y salida 0–255.");
        if(Points[0].Input!=0||Points[^1].Input!=100)throw new ArgumentException("Conserva los extremos 0 y 100.");
    }
}
