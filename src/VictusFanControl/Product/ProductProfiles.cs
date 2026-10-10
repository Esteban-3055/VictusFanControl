using System.Text.Json;
using System.Text.Json.Serialization;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Product;

public enum ProductPowerProfile { Ac, Battery }

/// <summary>Software handoff preferences; never alters firmware throttling or sensor/ownership admission.</summary>
public sealed record ProductProtectionSettings
{
    public bool CpuThermalHandoff { get; init; } = true;
    public bool GpuThermalHandoff { get; init; } = true;
    public bool PowerEnvelopeHandoff { get; init; } = true;
    public bool ResumeAutomatic { get; init; } = true;

    public SafetyGateResult ApplyThermalPolicy(TelemetrySnapshot? snapshot, SafetyGateResult raw)
    {
        if (snapshot is null || !raw.ThermalEmergency) return raw;
        var thermal = CpuThermalHandoff && snapshot.CpuControlTemperatureC >= SafetyGate.CpuEmergencyC ||
            GpuThermalHandoff && snapshot.GpuTemperatureC >= SafetyGate.GpuEmergencyC;
        if (thermal) return raw;
        var ready = raw.BoardAllowed && raw.RuntimeHealthy && raw.SnapshotComplete && raw.SnapshotFresh &&
            raw.TelemetryDeviceIdentityValid && raw.SensorsPlausible && snapshot.CpuCoreTelemetryComplete;
        return raw with { ThermalEmergency = false, PreconditionsReady = ready,
            CustomControlPermitted = ready && raw.FanWritePathPresent,
            Reasons = raw.Reasons.Where(r => !r.StartsWith("Thermal handoff threshold reached", StringComparison.Ordinal)).ToArray() };
    }
}

/// <summary>Configuration only. No session, authority, source observation or execution gate is persisted.</summary>
public sealed record ProductProfile
{
    public FanConfiguration Fan { get; init; } = new();
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public FanConfiguration? LegacyFan { get; init; }
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
        if(LegacyFan is not null)
        {
            var legacyConfig=LegacyFan.Profile?.Config;
            if(legacyConfig is null||new[]{legacyConfig.CpuTemperatureCurve,legacyConfig.GpuTemperatureCurve,legacyConfig.CpuPowerCurve,legacyConfig.GpuPowerCurve,legacyConfig.CpuLoadCurve,legacyConfig.GpuLoadCurve}.Any(curve=>curve is null||curve.Any(point=>point is null)))
                throw new InvalidDataException("Respaldo de curvas anteriores incompleto.");
            _ = LegacyFan.BuildPolicy();
        }
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
    public int SchemaVersion { get; init; } = 2;
    public ProductProfile Ac { get; init; } = DefaultProfile(ProductPowerProfile.Ac);
    public ProductProfile Battery { get; init; } = DefaultProfile(ProductPowerProfile.Battery);
    public bool CpuEnabled { get; init; } = true;
    public bool GpuEnabled { get; init; } = true;
    public bool StartMinimized { get; init; }
    public bool ActivateAutomaticOnStart { get; init; }
    public ProductProtectionSettings Protections { get; init; } = new();
    public int DefaultCurveRevision { get; init; } = 2;
    // Preferences only: never persists source qualification, fan authority or a running session.
    public bool ExperimentalPlatformRetention { get; init; }
    public ProductProfile Get(ProductPowerProfile source) => source switch
    { ProductPowerProfile.Ac => Ac, ProductPowerProfile.Battery => Battery, _ => throw new ArgumentOutOfRangeException(nameof(source)) };
    public ProductProfiles With(ProductPowerProfile source, ProductProfile profile) => source switch
    { ProductPowerProfile.Ac => this with { Ac = profile }, ProductPowerProfile.Battery => this with { Battery = profile }, _ => throw new ArgumentOutOfRangeException(nameof(source)) };
    public void Validate()
    {
        if (DefaultCurveRevision is not (1 or 2) || SchemaVersion is not(1 or 2) || Ac is null || Battery is null || Protections is null) throw new InvalidDataException("Esquema de perfiles incompatible.");
        Ac.Validate(ProductPowerProfile.Ac); Battery.Validate(ProductPowerProfile.Battery);
        if(SchemaVersion==2&&(Ac.Fan.UnifiedDemand is null||Battery.Fan.UnifiedDemand is null))throw new InvalidDataException("Cada fuente requiere su curva única de demanda.");
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
        var profile=LegacyDefaultProfile(source,previous);
        return source==ProductPowerProfile.Ac && previous is null
            ? profile with{Fan=ProductQuietCandidate.Apply(profile.Fan)} : profile;
    }
    // Explicit historical baseline: archived evidence must not follow a later default.
    public static ProductProfile LegacyDefaultProfile(ProductPowerProfile source, FanConfiguration? previous = null)
    {
        var fan = FanConfigurationStore.Copy(previous ?? QuietFanConfiguration(source));
        var c = AdaptiveCurveProfiles.Validate(fan.Profile);
        foreach (var axis in Enum.GetValues<AdaptiveCurveAxis>())
            fan = fan with { Profile = AdaptiveCurveProfiles.WithCurve(fan.Profile, axis,
                AdaptiveCurveProfiles.Curve(c, axis).Select(p => p with { Level = Math.Clamp(p.Level, 10, 50) }).ToArray()) };
        fan = fan with { Tuning = fan.Tuning with { MinimumLevel = 10, MaximumLevel = 50 }, UnifiedDemand=fan.UnifiedDemand??UnifiedFanDemand.Default(source==ProductPowerProfile.Battery) };
        return new() { Fan = fan, LegacyFan=previous?.UnifiedDemand is null&&previous is not null?FanConfigurationStore.Copy(previous):null,
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
            CpuTemperatureCurve=ac?Points(40,12,50,16,60,21,70,28,78,35,85,44,90,50):Points(40,10,50,10,60,12,70,24,78,35,85,44,90,50),
            GpuTemperatureCurve=ac?Points(35,12,45,15,55,20,65,28,72,35,78,44,81,50):Points(35,10,45,10,55,12,65,24,72,35,78,44,81,50),
            // CPU watts stay absolute: changing PL1/PL2 must not remap the same heat input.
            CpuPowerCurve=ac?Points(0,10,15,10,30,16,45,23,65,32,90,43,115,50):Points(0,10,10,10,18,10,25,14,40,24,60,32,90,43,115,50),
            // 70 W nominal, with headroom to the existing 75 W review envelope.
            // Sensor values are never clipped to the nominal rating.
            GpuPowerCurve=ac?Points(0,10,10,10,20,12,30,16,40,21,50,27,60,34,70,42,75,50):Points(0,10,10,10,20,10,30,14,40,20,50,27,60,34,70,42,75,50),
            CpuLoadCurve=ac?Points(0,10,25,10,50,12,75,18,100,24):Points(0,10,25,10,50,10,75,12,100,16),
            GpuLoadCurve=ac?Points(0,10,25,10,50,12,75,18,100,24):Points(0,10,25,10,50,10,75,12,100,16)
        };
        return new()
        {
            Profile=AdaptiveCurveProfiles.Create(ac?"5629a2f243674123ae9e243bdd8743cc":"911be76e54814f12a56bdb8eb193cf90",ac?"Silencioso AC":"Silencioso Batería",policy),
            Tuning=AdaptiveFanTuning.WithStableQuietResponse(new AdaptiveFanTuning()) with
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
        if(document.RootElement.ValueKind!=JsonValueKind.Object||!document.RootElement.TryGetProperty("schemaVersion",out _)||!document.RootElement.TryGetProperty("ac",out _)||!document.RootElement.TryGetProperty("battery",out _))throw new InvalidDataException("Falta la versión o uno de los perfiles AC/Batería.");
        var profiles = JsonSerializer.Deserialize<ProductProfiles>(text, Json) ?? throw new InvalidDataException("Perfiles vacíos.");
        profiles.Validate();
        // Older GUI files used the prepared Automatic minimum for editing too.
        // Preserve every stored curve point; expand only the offline editor envelope.
        ProductProfile Expand(ProductProfile p) => p with { Fan = p.Fan with { Tuning = p.Fan.Tuning with { MinimumLevel = 10 } } };
        if(profiles.SchemaVersion==1)
        {
            ProductProfile Upgrade(ProductProfile p,bool battery)=>Expand(p) with
            {LegacyFan=p.LegacyFan??FanConfigurationStore.Copy(p.Fan),Fan=Expand(p).Fan with{UnifiedDemand=UnifiedFanDemand.Default(battery)}};
            return profiles with{SchemaVersion=2,Ac=Upgrade(profiles.Ac,false),Battery=Upgrade(profiles.Battery,true)};
        }
        var ac=Expand(profiles.Ac);
        if(!document.RootElement.TryGetProperty("defaultCurveRevision",out _) || profiles.DefaultCurveRevision==1)
        {
            // Only the untouched prior fan preset migrates. Preserve custom curves, caps and startup choices.
            if(FanConfigurationStore.Serialize(ac.Fan)==FanConfigurationStore.Serialize(ProductProfiles.LegacyDefaultProfile(ProductPowerProfile.Ac).Fan))
                ac=ac with{Fan=ProductProfiles.DefaultProfile(ProductPowerProfile.Ac).Fan};
        }
        return profiles with { DefaultCurveRevision=2, Ac = ac, Battery = Expand(profiles.Battery) };
    }
    public static ProductProfiles Copy(ProductProfiles profiles) => Parse(Serialize(profiles));
    public static ProductProfiles Load(string? path, out string? notice, Func<ProductProfiles>? migrate = null)
    {
        path ??= DefaultPath; notice = null;
        try
        {
            if (File.Exists(path))
            {
                var text=File.ReadAllText(path);var loaded=Parse(text);
                using var document=JsonDocument.Parse(text);
                if(document.RootElement.GetProperty("schemaVersion").GetInt32()==1)notice="Motor nuevo en edición; curvas anteriores conservadas como respaldo. Guardar crea una copia exacta del archivo v1.";
                else if(!document.RootElement.TryGetProperty("defaultCurveRevision",out _))notice="v1.0: curva AC predeterminada actualizada si no estaba personalizada; límites y curva Batería conservados. Guardar crea un respaldo del archivo anterior.";
                return loaded;
            }
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
            if(File.Exists(path))
            {
                var bytes=File.ReadAllBytes(path);
                // Inspect the same bytes that will be backed up, with the BOM handling used by Load.
                // PowerShell 5.1 can write UTF-8 BOM or UTF-16; JsonDocument's byte parser accepts neither.
                try
                {
                    using var original=new MemoryStream(bytes,writable:false);
                    using var reader=new StreamReader(original,System.Text.Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
                    using var document=JsonDocument.Parse(reader.ReadToEnd());
                    if(document.RootElement.ValueKind==JsonValueKind.Object&&document.RootElement.TryGetProperty("schemaVersion",out var schema)&&
                        schema.ValueKind==JsonValueKind.Number&&schema.TryGetInt32(out var version)&&(version==1||!document.RootElement.TryGetProperty("defaultCurveRevision",out _)))
                    {
                        var backup=path+(version==1?".v1-backup-":".pre-v1-backup-")+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant()+".json";
                        if(File.Exists(backup)){if(!File.ReadAllBytes(backup).SequenceEqual(bytes))throw new IOException("El respaldo v1 existente no coincide; no se reemplazó el original.");}
                        else {using var stream=new FileStream(backup,FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(bytes);stream.Flush(flushToDisk:true);}
                    }
                }
                catch(JsonException){/* A corrupt original is not a legacy profile to migrate. */}
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
