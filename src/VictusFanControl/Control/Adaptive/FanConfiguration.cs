using VictusFanControl.Telemetry;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VictusFanControl.Control.Adaptive;

public enum CpuDemandTemperatureSource { PackageOrHottestCore, CoreAverage, PerformanceCoreAverage, HottestPerformanceCoresAverage }

/// <summary>Demand only. Raw Package/core safety readings remain on the snapshot.</summary>
public static class CpuDemandTemperature
{
    public static double? Select(TelemetrySnapshot snapshot, CpuDemandTemperatureSource source, int hottestPerformanceCoreCount = 3)
    {
        if (source == CpuDemandTemperatureSource.PackageOrHottestCore)
            return snapshot.CpuControlTemperatureC;
        if (!Enum.IsDefined(source) || !snapshot.CpuCoreTelemetryComplete ||
            snapshot.CpuCoreTemperatures.Select(c => c.CoreIndex).Distinct().Count() != snapshot.CpuCoreTemperatures.Count ||
            snapshot.CpuCoreTemperatures.Any(c => !double.IsFinite(c.TemperatureC) || c.TemperatureC < 0 || c.TemperatureC > 125))
            return null;
        if (source == CpuDemandTemperatureSource.CoreAverage)
            return snapshot.CpuCoreAverageTemperatureC;
        // Do not infer core type from array order, temperature or SMT presence.
        if (snapshot.CpuCoreTemperatures.Any(c => c.CoreType is not ("Performance" or "Efficiency")))
            return null;
        var performance = snapshot.CpuCoreTemperatures.Where(c => c.CoreType == "Performance").ToArray();
        if (performance.Length == 0) return null;
        if (source == CpuDemandTemperatureSource.PerformanceCoreAverage)
            return performance.Average(c => c.TemperatureC);
        if (source != CpuDemandTemperatureSource.HottestPerformanceCoresAverage ||
            hottestPerformanceCoreCount < 1 || hottestPerformanceCoreCount > performance.Length)
            return null;
        return performance.OrderByDescending(c => c.TemperatureC)
            .Take(hottestPerformanceCoreCount).Average(c => c.TemperatureC);
    }
}

/// <summary>Operating preferences only. Safety, ownership and execution gates are not settings.</summary>
public sealed record AdaptiveFanTuning
{
    // Missing fields in archived v1 settings retain the original source.
    public CpuDemandTemperatureSource CpuTemperatureSource { get; init; } = CpuDemandTemperatureSource.PackageOrHottestCore;
    public int HottestPerformanceCoreCount { get; init; } = 3;
    public int MinimumLevel { get; init; } = 26;
    public int MaximumLevel { get; init; } = 50;
    public double RiseTimeConstantSeconds { get; init; } = 4;
    public double FallTimeConstantSeconds { get; init; } = 20;
    public double IncreaseConfirmationSeconds { get; init; } = 1;
    public double DecreaseConfirmationSeconds { get; init; } = 16;
    public int NormalMaximumUpStepLevels { get; init; } = 1;
    public int MaximumDownStepLevels { get; init; } = 1;
    public int ThermalMaximumUpStepLevels { get; init; } = 4;
    public double CpuThermalOverrideC { get; init; } = 85;
    public double GpuThermalOverrideC { get; init; } = 78;
    public bool RememberThermalDemand { get; init; } = false;
    public int NormalPollingDelayMilliseconds { get; init; } = 1000;

    public void Validate()
    {
        static void Range(double value, double lo, double hi, string name)
        {
            if (!double.IsFinite(value) || value < lo || value > hi)
                throw new InvalidDataException($"{name}: rango permitido {lo}–{hi}.");
        }
        if (!Enum.IsDefined(CpuTemperatureSource))
            throw new InvalidDataException("Fuente de temperatura CPU desconocida.");
        Range(HottestPerformanceCoreCount, 1, 64, "P-Cores más calientes (N)");
        Range(MinimumLevel, 10, 50, "Nivel mínimo");
        Range(MaximumLevel, MinimumLevel, 50, "Nivel máximo");
        Range(RiseTimeConstantSeconds, 0.5, 10, "Filtro de subida (s)");
        Range(FallTimeConstantSeconds, 1, 60, "Filtro de bajada (s)");
        Range(IncreaseConfirmationSeconds, 0, 5, "Confirmación de subida (s)");
        Range(DecreaseConfirmationSeconds, 2, 60, "Confirmación de bajada (s)");
        Range(NormalMaximumUpStepLevels, 1, 4, "Paso normal de subida");
        Range(MaximumDownStepLevels, 1, 2, "Paso de bajada");
        Range(ThermalMaximumUpStepLevels, 4, 4, "Paso térmico protegido");
        Range(CpuThermalOverrideC, 75, 85, "Respuesta térmica CPU (°C)");
        Range(GpuThermalOverrideC, 68, 78, "Respuesta térmica GPU (°C)");
        Range(NormalPollingDelayMilliseconds, 500, 1500, "Pausa normal (ms)");
    }
}

public sealed record FanConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public AdaptiveFanTuning Tuning { get; init; } = new() { CpuTemperatureSource = CpuDemandTemperatureSource.CoreAverage };
    public AdaptiveCurveProfile Profile { get; init; } = QuietProfile();

    public static AdaptiveCurveProfile QuietProfile()
    {
        // Firmware observations guide moderate demand; hard heat still reaches
        // the original 44/50 envelope. This is a candidate, not acoustic proof.
        var candidate = Hp8C40AdaptiveCandidateV1.Create() with
        {
            CpuTemperatureCurve = [new(40,26), new(50,26), new(60,26), new(70,28), new(78,34), new(85,44), new(90,50)],
            GpuTemperatureCurve = [new(35,26), new(45,26), new(55,27), new(65,29), new(72,34), new(78,44), new(84,50)]
        };
        return AdaptiveCurveProfiles.Create("d23b73412d654a13b7c1ab83f5468206", "Firmware suave", candidate);
    }

    public AdaptiveFanPolicyConfig BuildPolicy()
    {
        if (SchemaVersion != 1 || Tuning is null || Profile is null)
            throw new InvalidDataException("Configuración de ventiladores incompatible.");
        Tuning.Validate();
        var c = AdaptiveCurveProfiles.Validate(Profile);
        IReadOnlyList<AdaptiveFanCurvePoint> Clamp(IReadOnlyList<AdaptiveFanCurvePoint> points) =>
            points.Select(p => p with { Level = Math.Clamp(p.Level, Tuning.MinimumLevel, Tuning.MaximumLevel) }).ToArray();
        return c with
        {
            MinimumLevel = Tuning.MinimumLevel, MaximumLevel = Tuning.MaximumLevel,
            MaximumUpStepPerSample = Tuning.ThermalMaximumUpStepLevels,
            MaximumDownStepPerSample = Tuning.MaximumDownStepLevels,
            CpuTemperatureCurve = Clamp(c.CpuTemperatureCurve), GpuTemperatureCurve = Clamp(c.GpuTemperatureCurve),
            CpuPowerCurve = Clamp(c.CpuPowerCurve), GpuPowerCurve = Clamp(c.GpuPowerCurve),
            CpuLoadCurve = Clamp(c.CpuLoadCurve), GpuLoadCurve = Clamp(c.GpuLoadCurve)
        };
    }
}

/// <summary>Strict, atomic preference storage. Loading never selects a hardware mode.</summary>
public static class FanConfigurationStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "fan-configuration.json");
    public static string Serialize(FanConfiguration configuration)
    {
        _ = configuration.BuildPolicy();
        return JsonSerializer.Serialize(configuration, Json);
    }
    public static FanConfiguration Parse(string text)
    {
        var c = JsonSerializer.Deserialize<FanConfiguration>(text, Json) ??
            throw new InvalidDataException("Configuración vacía.");
        _ = c.BuildPolicy();
        return c;
    }
    public static FanConfiguration Copy(FanConfiguration c) => Parse(Serialize(c));
    public static FanConfiguration Load(string? path, out string? notice)
    {
        notice = null;
        path ??= DefaultPath;
        if (!File.Exists(path)) return new();
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            notice = "No se cargó el archivo de ajustes; se usa Firmware suave. " + ex.Message;
            return new();
        }
    }
    public static void Save(FanConfiguration c, string? path = null)
    {
        var text = Serialize(c);
        path = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
