using System.Text.Json;

namespace VictusFanControl.App;

internal sealed record HyperXSnapshot
{
    internal const int PollSeconds=60, FreshSeconds=180;
    internal string Status {get;init;}="Preparando lector";
    internal bool ReceiverPresent {get;init;}
    internal int? Millivolts {get;init;}
    internal int? Percent {get;init;}
    internal bool? Charging {get;init;}
    internal DateTimeOffset? SampledAt {get;init;}
    internal bool? Muted {get;init;}
    internal long MuteEventVersion {get;init;}
    internal DateTimeOffset? MuteEventAt {get;init;}
    internal bool? Powered {get;init;}
    internal bool Fresh(DateTimeOffset now)=>ReceiverPresent&&SampledAt is{} at&&now>=at&&(now-at).TotalSeconds<FreshSeconds;
    internal static (int Millivolts,int? Percent,bool Charging)? Decode(ReadOnlySpan<byte> b)
    {
        if(b.Length is not(15 or 20)||b[0]!=0x21||b[1]!=0xff||b[2]!=5)return null;
        int mv=(b[3]<<8)|b[4];if(mv is <3300 or >4500)return null;
        bool charging=mv>0x100b;
        // srn/hyperx-cloud-flight-wireless (MIT): coarse voltage-to-level map.
        (int Low,int High,int Pct)[] ranges=[(4097,4107,100),(3970,4095,100),(3960,3969,95),(3940,3959,90),(3910,3939,85),(3890,3909,80),(3860,3889,75),(3840,3859,70),(3825,3833,65),(3804,3823,60),(3792,3803,55),(3784,3791,50),(3774,3783,45),(3764,3773,40),(3753,3762,35),(3743,3752,30),(3732,3742,25),(3703,3731,20),(3674,3702,15),(3584,3673,10)];
        int? pct=null;if(!charging)foreach(var r in ranges)if(mv>=r.Low&&mv<=r.High){pct=r.Pct;break;}
        // Extend the original map below 3584 mV only as critical/unknown, never invent 0%.
        return (mv,pct,charging);
    }
}

internal sealed record HyperXSettings
{
    public int SchemaVersion {get;init;}=1;
    public bool LowEnabled {get;init;}=true;
    public bool CriticalEnabled {get;init;}=true;
    public bool FullEnabled {get;init;}=true;
    public bool MuteEnabled {get;init;}=true;
    public int LowPercent {get;init;}=20;
    public int CriticalPercent {get;init;}=10;
    public int FullPercent {get;init;}=100;
    public int LcdLowSeconds {get;init;}=6;
    public int LcdCriticalSeconds {get;init;}=10;
    public int LcdLowBlinkCycles {get;init;}=3;
    public int LcdCriticalBlinkCycles {get;init;}=5;
    internal VictusFanControl.ExternalCooling.HyperxDisplayConfiguration DisplayConfiguration()=>new()
    {
        LowEnabled=LowEnabled,CriticalEnabled=CriticalEnabled,LowPercent=LowPercent,CriticalPercent=CriticalPercent,
        LowDurationMs=LcdLowSeconds*1000,CriticalDurationMs=LcdCriticalSeconds*1000,
        LowBlinkCycles=LcdLowBlinkCycles,CriticalBlinkCycles=LcdCriticalBlinkCycles
    };
    internal void Validate()
    {
        try{DisplayConfiguration().Validate();}catch(ArgumentException e){throw new InvalidDataException(e.Message,e);}
        if(LcdLowSeconds is <1 or >120||LcdCriticalSeconds is <1 or >120)throw new InvalidDataException("Duración LCD: 1–120 segundos.");
        if(SchemaVersion!=1||CriticalPercent is <10 or >98||LowPercent<=CriticalPercent||LowPercent>=FullPercent||FullPercent>100)
            throw new InvalidDataException("Usa 10 ≤ crítica < baja < carga completa ≤ 100. La estimación tiene un mínimo de 10 %.");
    }
    internal static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VictusFanControl","hyperx-battery.json");
    internal static HyperXSettings Load(string path,out string? notice)
    {
        notice=null;if(!File.Exists(path))return new();
        try{var s=JsonSerializer.Deserialize<HyperXSettings>(File.ReadAllText(path))??throw new InvalidDataException();s.Validate();return s;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){notice="No se pudieron leer los ajustes HyperX. Se usan valores predeterminados; el archivo se conserva.";return new();}
    }
    internal void Save(string path)
    {
        Validate();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}

internal sealed record HyperXNotice(string Title,string Text);
// UI consumes immutable snapshots. Threshold latches survive missing data/reconnects;
// five percentage points of recovery re-arm them. No repeated alarm per TCP frame.
internal sealed class HyperXNotificationPolicy
{
    private bool low,critical,full;
    private bool? muted;
    private long muteVersion;
    private HyperXSettings? previousSettings;
    internal IReadOnlyList<HyperXNotice> Evaluate(HyperXSnapshot s,HyperXSettings cfg,DateTimeOffset now)
    {
        cfg.Validate();var output=new List<HyperXNotice>();
        if(previousSettings!=cfg){low=critical=full=false;previousSettings=cfg;}
        if(s.Fresh(now)&&s.Percent is{} p)
        {
            if(p>=cfg.LowPercent+5)low=false;if(p>=cfg.CriticalPercent+5)critical=false;
            if(p<cfg.FullPercent-5)full=false;
            if(p<=cfg.CriticalPercent)
            {
                if(!critical&&cfg.CriticalEnabled)output.Add(new("HyperX: batería crítica",$"Carga estimada: {p} %. Conecta el cable de carga."));
                critical=true;low=true;
            }
            else if(p<=cfg.LowPercent)
            {
                if(!low&&cfg.LowEnabled)output.Add(new("HyperX: batería baja",$"Carga estimada: {p} %."));low=true;
            }
            else if(p>=cfg.FullPercent)
            {
                if(!full&&cfg.FullEnabled)output.Add(new("HyperX: nivel de carga alcanzado",$"Carga estimada: {p} % (umbral {cfg.FullPercent} %). La estimación no confirma la terminación eléctrica de carga."));full=true;
            }
        }
        // Unknown initial state/reconnect never invents a mute transition.
        if(s.Muted is{} m&&s.MuteEventVersion>muteVersion&&muted!=m&&cfg.MuteEnabled&&s.MuteEventAt is{} eventAt&&now>=eventAt&&(now-eventAt).TotalSeconds<5)
            output.Add(new(m?"HyperX: micrófono silenciado":"HyperX: micrófono activado",m?"Se silenció el micrófono de los audífonos.":"Se quitó el silencio del micrófono."));
        muteVersion=Math.Max(muteVersion,s.MuteEventVersion);muted=s.Muted;return output;
    }
}
