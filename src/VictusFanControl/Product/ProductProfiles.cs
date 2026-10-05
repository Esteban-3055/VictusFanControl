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
        _ = Fan.BuildPolicy();
        if (Fan.Tuning.MinimumLevel != Hp8C40AutomaticPolicy.MinimumLevel || Fan.Tuning.MaximumLevel != Hp8C40AutomaticPolicy.MaximumLevel)
            throw new InvalidDataException("La GUI conserva el rango de ventiladores 30–50 del backend WMI.");
        if (GpuMaximumMHz < GpuProductPreferences.MinimumMHz || GpuMaximumMHz > GpuProductPreferences.Maximum(source))
            throw new InvalidDataException("Límite GPU fuera del rango conservador del perfil.");
        foreach (var axis in Enum.GetValues<AdaptiveCurveAxis>())
            if (AdaptiveCurveProfiles.Curve(AdaptiveCurveProfiles.Validate(Fan.Profile), axis).Any(p => p.Level < 30 || p.Level > 50))
                throw new InvalidDataException("Curva de ventiladores fuera de rango.");
    }
}

public static class GpuProductPreferences
{
    public const int MinimumMHz = 210;
    public const int QualifiedAcMaximumMHz = 1850;
    public const int QualifiedBatteryMaximumMHz = 1200;
    // The GUI can edit/persist conservative targets, but new ranges require physical qualification.
    public const bool CustomClockExecutionAuthorized = false;
    public static int Maximum(ProductPowerProfile source) => source switch
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
        var fan = FanConfigurationStore.Copy(previous ?? new FanConfiguration());
        var c = fan.BuildPolicy();
        foreach (var axis in Enum.GetValues<AdaptiveCurveAxis>())
            fan = fan with { Profile = AdaptiveCurveProfiles.WithCurve(fan.Profile, axis,
                AdaptiveCurveProfiles.Curve(c, axis).Select(p => p with { Level = Math.Clamp(p.Level, 30, 50) }).ToArray()) };
        fan = fan with { Tuning = fan.Tuning with { MinimumLevel = 30, MaximumLevel = 50 } };
        return new() { Fan = fan,
            CpuPl1Watts = source == ProductPowerProfile.Ac ? CpuPowerProductDefaults.DefaultAcPl1Watts : CpuPowerProductDefaults.DefaultBatteryPl1Watts,
            CpuPl2Watts = source == ProductPowerProfile.Ac ? CpuPowerProductDefaults.DefaultAcPl2Watts : CpuPowerProductDefaults.DefaultBatteryPl2Watts,
            GpuMaximumMHz = GpuProductPreferences.Maximum(source) };
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
        var profiles = JsonSerializer.Deserialize<ProductProfiles>(text, Json) ?? throw new InvalidDataException("Perfiles vacíos.");
        profiles.Validate(); return profiles;
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
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
