using System.Text.Json;
using System.Text.Json.Serialization;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;

namespace VictusFanControl.Product;

public enum ProductPowerProfile { Ac, Battery }

/// <summary>Configuration only. No session, authority, source observation or execution gate is persisted.</summary>
public sealed record ProductProfile
{
    public FanConfiguration Fan { get; init; } = new();
    public int CpuPl1Watts { get; init; }
    public int CpuPl2Watts { get; init; }
    public int GpuMaximumMHz { get; init; }
    public void Validate(ProductPowerProfile source)
    {
        if (Fan is null || !CpuPowerProductDefaults.IsConfigurable(CpuPl1Watts, CpuPl2Watts))
            throw new InvalidDataException("PL1/PL2 inválidos; PL2 debe ser mayor o igual a PL1.");
        if(Fan.Profile is null)throw new InvalidDataException("Falta el perfil de curva.");
        var config=Fan.Profile.Config;
        if(config is null||new[]{config.CpuTemperatureCurve,config.GpuTemperatureCurve,config.CpuPowerCurve,config.GpuPowerCurve,config.CpuLoadCurve,config.GpuLoadCurve}.Any(curve=>curve is null||curve.Any(point=>point is null)))
            throw new InvalidDataException("Cada perfil requiere las seis curvas y puntos válidos.");
        _ = Fan.BuildPolicy();
        if (Fan.Tuning.MinimumLevel is not (10 or 30) || Fan.Tuning.MaximumLevel != Hp8C40AutomaticPolicy.MaximumLevel)
            throw new InvalidDataException("El editor admite niveles 10–50; el control físico conserva su rango validado.");
        if (GpuMaximumMHz < GpuProductPreferences.MinimumMHz || GpuMaximumMHz > GpuProductPreferences.Maximum(source))
            throw new InvalidDataException("Límite GPU fuera del rango configurable 210–2500 MHz.");
        foreach (var axis in Enum.GetValues<AdaptiveCurveAxis>())
            if (AdaptiveCurveProfiles.Curve(AdaptiveCurveProfiles.Validate(Fan.Profile), axis).Any(p => p.Level < 10 || p.Level > 50))
                throw new InvalidDataException("Curva de ventiladores fuera de rango.");
    }
}

public static class GpuProductPreferences
{
    public const int MinimumMHz = 210;
    public const int QualifiedAcMaximumMHz = 1850;
    public const int QualifiedBatteryMaximumMHz = 1200;
    public const int ConfigurableMaximumMHz = 2500;
    // Configurable range is user authorized; historical qualification remains 1850/1200.
    // NVML rejection remains a failure; acceptance never claims independent range ownership.
    public const bool CustomClockExecutionAuthorized = true;
    public static int Maximum(ProductPowerProfile source) => source switch
    { ProductPowerProfile.Ac or ProductPowerProfile.Battery => ConfigurableMaximumMHz, _ => throw new ArgumentOutOfRangeException(nameof(source)) };
    public static int DefaultMaximum(ProductPowerProfile source) => source switch
    { ProductPowerProfile.Ac => QualifiedAcMaximumMHz, ProductPowerProfile.Battery => QualifiedBatteryMaximumMHz, _ => throw new ArgumentOutOfRangeException(nameof(source)) };
}

public sealed record ProductProfiles
{
    public int SchemaVersion { get; init; } = 1;
    public ProductProfile Ac { get; init; } = DefaultProfile(ProductPowerProfile.Ac);
    public ProductProfile Battery { get; init; } = DefaultProfile(ProductPowerProfile.Battery);
    public bool CpuEnabled { get; init; } = true;
    public bool GpuEnabled { get; init; } = true;
    public bool StartMinimized { get; init; }
    public ProductProfile Get(ProductPowerProfile source) => source switch
    { ProductPowerProfile.Ac => Ac, ProductPowerProfile.Battery => Battery, _ => throw new ArgumentOutOfRangeException(nameof(source)) };
    public ProductProfiles With(ProductPowerProfile source, ProductProfile profile) => source switch
    { ProductPowerProfile.Ac => this with { Ac = profile }, ProductPowerProfile.Battery => this with { Battery = profile }, _ => throw new ArgumentOutOfRangeException(nameof(source)) };
    public void Validate()
    {
        if (SchemaVersion != 1 || Ac is null || Battery is null) throw new InvalidDataException("Esquema de perfiles incompatible.");
        Ac.Validate(ProductPowerProfile.Ac); Battery.Validate(ProductPowerProfile.Battery);
    }
    public PerformanceGuiSessionConfiguration PerformanceConfiguration() => new()
    {
        CpuEnabled = CpuEnabled, GpuEnabled = GpuEnabled,
        AcPl1Watts = Ac.CpuPl1Watts, AcPl2Watts = Ac.CpuPl2Watts,
        BatteryPl1Watts = Battery.CpuPl1Watts, BatteryPl2Watts = Battery.CpuPl2Watts,
        AcGpuMaximumMHz = Ac.GpuMaximumMHz, BatteryGpuMaximumMHz = Battery.GpuMaximumMHz
    };
    public static ProductProfile DefaultProfile(ProductPowerProfile source, FanConfiguration? previous = null)
    {
        var fan = FanConfigurationStore.Copy(previous ?? QuietFanConfiguration(source));
        var c = AdaptiveCurveProfiles.Validate(fan.Profile);
        foreach (var axis in Enum.GetValues<AdaptiveCurveAxis>())
            fan = fan with { Profile = AdaptiveCurveProfiles.WithCurve(fan.Profile, axis,
                AdaptiveCurveProfiles.Curve(c, axis).Select(p => p with { Level = Math.Clamp(p.Level, 10, 50) }).ToArray()) };
        fan = fan with { Tuning = fan.Tuning with { MinimumLevel = 10, MaximumLevel = 50 } };
        return new() { Fan = fan,
            CpuPl1Watts = source == ProductPowerProfile.Ac ? CpuPowerProductDefaults.DefaultAcPl1Watts : CpuPowerProductDefaults.DefaultBatteryPl1Watts,
            CpuPl2Watts = source == ProductPowerProfile.Ac ? CpuPowerProductDefaults.DefaultAcPl2Watts : CpuPowerProductDefaults.DefaultBatteryPl2Watts,
            GpuMaximumMHz = GpuProductPreferences.DefaultMaximum(source) };
    }
    private static FanConfiguration QuietFanConfiguration(ProductPowerProfile source)
    {
        var ac=source==ProductPowerProfile.Ac;
        AdaptiveFanCurvePoint[] Points(params double[] pairs)=>Enumerable.Range(0,pairs.Length/2).Select(i=>new AdaptiveFanCurvePoint(pairs[i*2],pairs[i*2+1])).ToArray();
        var policy=Hp8C40AdaptiveCandidateV1.Create() with
        {
            CpuTemperatureCurve=ac?Points(40,12,50,16,60,21,70,28,78,35,85,44,90,50):Points(40,10,50,13,60,18,70,26,78,35,85,44,90,50),
            GpuTemperatureCurve=ac?Points(35,12,45,15,55,20,65,28,72,35,78,44,81,50):Points(35,10,45,12,55,17,65,26,72,35,78,44,81,50),
            CpuPowerCurve=ac?Points(0,10,15,10,30,16,45,23,65,32,90,43,115,50):Points(0,10,10,10,15,12,25,17,40,24,60,32,90,43,115,50),
            GpuPowerCurve=ac?Points(0,10,20,10,40,16,70,24,95,34,115,42,140,50):Points(0,10,10,10,20,12,40,17,70,26,95,36,115,44,140,50),
            CpuLoadCurve=Points(0,10,25,10,50,12,75,18,100,24),
            GpuLoadCurve=Points(0,10,25,10,50,12,75,18,100,24)
        };
        return new()
        {
            Profile=AdaptiveCurveProfiles.Create(ac?"5629a2f243674123ae9e243bdd8743cc":"911be76e54814f12a56bdb8eb193cf90",ac?"Silencioso AC":"Silencioso Batería",policy),
            Tuning=AdaptiveFanTuning.WithSmoothAdaptiveResponse(new AdaptiveFanTuning()) with
            {MinimumLevel=10,MaximumLevel=50,CpuTemperatureSource=CpuDemandTemperatureSource.HottestPerformanceCoresAverage,HottestPerformanceCoreCount=3}
        };
    }

}

public static class ProductProfilesStore
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "product-profiles.json");
    public static string Serialize(ProductProfiles profiles) { profiles.Validate(); return JsonSerializer.Serialize(profiles, Json); }
    public static ProductProfiles Parse(string text)
    {
        using var document=JsonDocument.Parse(text);
        static void CheckKeys(JsonElement element)
        {
            if(element.ValueKind==JsonValueKind.Object)
            {
                var keys=new HashSet<string>(StringComparer.Ordinal);
                foreach(var field in element.EnumerateObject()){if(!keys.Add(field.Name))throw new InvalidDataException("Campo duplicado: "+field.Name);CheckKeys(field.Value);}
            }
            else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())CheckKeys(child);
        }
        CheckKeys(document.RootElement);
        if(document.RootElement.ValueKind!=JsonValueKind.Object||!document.RootElement.TryGetProperty("schemaVersion",out _))throw new InvalidDataException("Falta la versión del esquema.");
        var profiles = JsonSerializer.Deserialize<ProductProfiles>(text, Json) ?? throw new InvalidDataException("Perfiles vacíos.");
        profiles.Validate();
        // Older GUI files used the prepared Automatic minimum for editing too.
        // Preserve every stored curve point; expand only the offline editor envelope.
        ProductProfile Expand(ProductProfile p) => p with { Fan = p.Fan with { Tuning = p.Fan.Tuning with { MinimumLevel = 10 } } };
        return profiles with { Ac = Expand(profiles.Ac), Battery = Expand(profiles.Battery) };
    }
    public static ProductProfiles Copy(ProductProfiles profiles) => Parse(Serialize(profiles));
    public static ProductProfiles Load(string? path, out string? notice, Func<ProductProfiles>? migrate = null)
    {
        path ??= DefaultPath; notice = null;
        try
        {
            if (File.Exists(path)) return Parse(File.ReadAllText(path));
            var defaults = migrate?.Invoke() ?? new ProductProfiles(); defaults.Validate();
            notice = migrate is null ? null : "Se importaron las preferencias anteriores; AC y Batería conservan copias independientes.";
            return Copy(defaults);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { notice = "Configuración no válida; se cargaron valores seguros. El archivo original se conserva. " + ex.Message; return new(); }
    }
    public static void Save(ProductProfiles profiles, string? path = null)
    {
        var text = Serialize(profiles); path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {var bytes=System.Text.Encoding.UTF8.GetBytes(text);stream.Write(bytes);stream.Flush(flushToDisk:true);}
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
