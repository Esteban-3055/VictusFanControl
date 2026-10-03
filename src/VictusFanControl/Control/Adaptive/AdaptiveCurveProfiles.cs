using System.Text.Json;
using System.Text.Json.Serialization;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Control.Adaptive;

public enum AdaptiveCurveAxis { CpuTemperature, GpuTemperature, CpuPower, GpuPower, CpuLoad, GpuLoad }

public sealed record AdaptiveCurveProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public AdaptiveFanPolicyShadowConfigDocument Config { get; init; } = new();
    [JsonIgnore] public bool IsBuiltIn => Id is "silencio" or "equilibrado" or "performance";
    public override string ToString() => Name;
}

/// <summary>Editor profiles are data for a read-only preview, never control authorization.</summary>
public static class AdaptiveCurveProfiles
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<AdaptiveCurveProfile> Presets()
    {
        var balanced = Hp8C40AdaptiveCandidateV1.Create();
        AdaptiveFanCurvePoint[] Points(params double[] pairs) => Enumerable.Range(0, pairs.Length / 2)
            .Select(i => new AdaptiveFanCurvePoint(pairs[i * 2], pairs[i * 2 + 1])).ToArray();
        var silentLoad = Points(0,10,25,10,50,12,75,18,100,24);
        var performanceLoad = Points(0,10,25,12,50,20,75,26,100,32);
        // Recorded GPU-power/load proposals. Missing historical tables retain Candidate V1.
        return [
            Create("silencio", "Silencio", balanced with
            {
                GpuPowerCurve = Points(20,10,40,12,70,20,95,28,115,38,140,50),
                CpuLoadCurve = silentLoad, GpuLoadCurve = silentLoad
            }),
            Create("equilibrado", "Equilibrado", balanced),
            Create("performance", "Performance", balanced with
            {
                GpuPowerCurve = Points(0,10,20,12,40,20,70,30,95,40,115,46,140,50),
                CpuLoadCurve = performanceLoad, GpuLoadCurve = performanceLoad
            })
        ];
    }

    public static AdaptiveCurveProfile Create(string id, string name, AdaptiveFanPolicyConfig c) => new()
    {
        Id = id, Name = name,
        Config = new()
        {
            SchemaVersion = 1, TargetProfileId = Hp8C40TargetProfile.Instance.Id,
            Purpose = "shadow-only", AuthorizedForProduction = false,
            MinimumLevel = c.MinimumLevel, MaximumLevel = c.MaximumLevel,
            MaximumUpStepPerSample = c.MaximumUpStepPerSample, MaximumDownStepPerSample = c.MaximumDownStepPerSample,
            DecreaseConfirmationSamples = c.DecreaseConfirmationSamples, DecreaseDeadbandLevels = c.DecreaseDeadbandLevels,
            MaximumSampleGapSeconds = c.MaximumSampleGap.TotalSeconds,
            CpuTemperatureCurve = c.CpuTemperatureCurve.ToList(), GpuTemperatureCurve = c.GpuTemperatureCurve.ToList(),
            CpuPowerCurve = c.CpuPowerCurve.ToList(), GpuPowerCurve = c.GpuPowerCurve.ToList(),
            CpuLoadCurve = c.CpuLoadCurve.ToList(), GpuLoadCurve = c.GpuLoadCurve.ToList()
        }
    };

    public static AdaptiveFanPolicyConfig Validate(AdaptiveCurveProfile profile)
    {
        if (profile.SchemaVersion != 1 || string.IsNullOrWhiteSpace(profile.Name) ||
            profile.Name.Length > 60 || profile.Name.Any(char.IsControl) ||
            (!profile.IsBuiltIn && !Guid.TryParseExact(profile.Id, "N", out _)))
            throw new InvalidDataException("El nombre debe tener 1–60 caracteres y el ID debe ser válido.");
        if (profile.Config is null || profile.Config.MaximumSampleGapSeconds != 3)
            throw new InvalidDataException("El intervalo máximo debe conservarse en 3 segundos.");
        var c = AdaptiveFanPolicyShadowConfig.Parse(JsonSerializer.Serialize(profile.Config, JsonOptions));
        if (c.MinimumLevel != 10 || c.MaximumLevel != 50 || c.MaximumUpStepPerSample != 4 ||
            c.MaximumDownStepPerSample != 1 || c.DecreaseConfirmationSamples != 5 ||
            c.DecreaseDeadbandLevels != 1 || c.MaximumSampleGap != TimeSpan.FromSeconds(3))
            throw new InvalidDataException("Los perfiles conservan los límites y suavizado de Candidate V1.");
        foreach (var axis in Enum.GetValues<AdaptiveCurveAxis>())
        {
            var curve = Curve(c, axis);
            if (curve.Count > 64 || curve.Any(p => p.Input < 0 || p.Input > MaximumInput(axis) || p.Input != Math.Truncate(p.Input) || p.Level != Math.Truncate(p.Level)))
                throw new InvalidDataException("Una curva excede sus límites de entrada o 64 puntos.");
        }
        return c;
    }

    public static double MaximumInput(AdaptiveCurveAxis axis) => axis switch
    {
        AdaptiveCurveAxis.CpuTemperature => 110, AdaptiveCurveAxis.GpuTemperature => 100,
        AdaptiveCurveAxis.CpuPower => 150, AdaptiveCurveAxis.GpuPower => 200, _ => 100
    };

    public static IReadOnlyList<AdaptiveFanCurvePoint> Curve(AdaptiveFanPolicyConfig c, AdaptiveCurveAxis axis) => axis switch
    {
        AdaptiveCurveAxis.CpuTemperature => c.CpuTemperatureCurve, AdaptiveCurveAxis.GpuTemperature => c.GpuTemperatureCurve,
        AdaptiveCurveAxis.CpuPower => c.CpuPowerCurve, AdaptiveCurveAxis.GpuPower => c.GpuPowerCurve,
        AdaptiveCurveAxis.CpuLoad => c.CpuLoadCurve, AdaptiveCurveAxis.GpuLoad => c.GpuLoadCurve,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    public static AdaptiveCurveProfile WithCurve(AdaptiveCurveProfile profile, AdaptiveCurveAxis axis,
        IReadOnlyList<AdaptiveFanCurvePoint> points)
    {
        var c = Validate(profile);
        var copy = points.ToArray();
        c = axis switch
        {
            AdaptiveCurveAxis.CpuTemperature => c with { CpuTemperatureCurve = copy },
            AdaptiveCurveAxis.GpuTemperature => c with { GpuTemperatureCurve = copy },
            AdaptiveCurveAxis.CpuPower => c with { CpuPowerCurve = copy },
            AdaptiveCurveAxis.GpuPower => c with { GpuPowerCurve = copy },
            AdaptiveCurveAxis.CpuLoad => c with { CpuLoadCurve = copy },
            AdaptiveCurveAxis.GpuLoad => c with { GpuLoadCurve = copy },
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
        var result = Create(profile.Id, profile.Name, c);
        _ = Validate(result);
        return result;
    }

    public static double Interpolate(IReadOnlyList<AdaptiveFanCurvePoint> curve, double value) =>
        AdaptiveFanPolicyEngine.Interpolate(curve, value);
    public static string Serialize(AdaptiveCurveProfile p) { _ = Validate(p); return JsonSerializer.Serialize(p, JsonOptions); }
    public static AdaptiveCurveProfile Parse(string json)
    {
        var p = JsonSerializer.Deserialize<AdaptiveCurveProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Perfil vacío.");
        _ = Validate(p);
        return p;
    }
    public static AdaptiveCurveProfile Copy(AdaptiveCurveProfile p) => Parse(Serialize(p));
}

public sealed class AdaptiveCurveProfileStore
{
    private readonly string _directory;
    public AdaptiveCurveProfileStore(string directory) => _directory = Path.GetFullPath(directory);
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Solo se guardan perfiles propios con ID válido.");
        return Path.Combine(_directory, id + ".json");
    }
    public IReadOnlyList<AdaptiveCurveProfile> LoadCustom(out int rejected)
    {
        rejected = 0;
        var profiles = new List<AdaptiveCurveProfile>();
        if (!Directory.Exists(_directory)) return profiles;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").Take(64))
        {
            try
            {
                if (new FileInfo(file).Length > 262144) throw new InvalidDataException("Perfil demasiado grande.");
                var p = AdaptiveCurveProfiles.Parse(File.ReadAllText(file));
                if (p.IsBuiltIn || Path.GetFileName(file) != p.Id + ".json") throw new InvalidDataException("ID de archivo inválido.");
                profiles.Add(p);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            { rejected++; }
        }
        return profiles;
    }
    public void Save(AdaptiveCurveProfile profile)
    {
        var path = PathFor(profile.Id);
        var json = AdaptiveCurveProfiles.Serialize(profile);
        Directory.CreateDirectory(_directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete(string id) => File.Delete(PathFor(id));
}
