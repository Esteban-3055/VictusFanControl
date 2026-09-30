using System.Text.Json;
using System.Text.Json.Serialization;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Control.Adaptive;

public sealed record AdaptiveFanPolicyShadowConfigDocument
{
    public int SchemaVersion { get; init; }
    public string? TargetProfileId { get; init; }
    public string? Purpose { get; init; }
    public bool? AuthorizedForProduction { get; init; }
    public int MinimumLevel { get; init; }
    public int MaximumLevel { get; init; }
    public int MaximumUpStepPerSample { get; init; }
    public int MaximumDownStepPerSample { get; init; }
    public int DecreaseConfirmationSamples { get; init; }
    public double DecreaseDeadbandLevels { get; init; }
    public double MaximumSampleGapSeconds { get; init; }
    public List<AdaptiveFanCurvePoint>? CpuTemperatureCurve { get; init; }
    public List<AdaptiveFanCurvePoint>? GpuTemperatureCurve { get; init; }
    public List<AdaptiveFanCurvePoint>? CpuPowerCurve { get; init; }
    public List<AdaptiveFanCurvePoint>? GpuPowerCurve { get; init; }
    public List<AdaptiveFanCurvePoint>? CpuLoadCurve { get; init; }
    public List<AdaptiveFanCurvePoint>? GpuLoadCurve { get; init; }
    public List<AdaptiveFanCurvePoint>? CpuTemperatureTrendCurve { get; init; }
}

/// <summary>
/// Strict loader for adaptive-policy shadow/replay configurations.
///
/// A document must explicitly identify the exact HP 8C40 target, declare
/// purpose=shadow-only, and explicitly declare authorizedForProduction=false.
/// Loading a document never creates hardware/control objects.
/// </summary>
public static class AdaptiveFanPolicyShadowConfig
{
    public const int CurrentSchemaVersion = 1;
    public const string RequiredPurpose = "shadow-only";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    public static AdaptiveFanPolicyConfig Load(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Adaptive shadow config path is required.",
                nameof(path));
        }

        return Parse(
            File.ReadAllText(
                Path.GetFullPath(path)));
    }

    public static AdaptiveFanPolicyConfig Parse(
        string json)
    {
        AdaptiveFanPolicyShadowConfigDocument document;

        try
        {
            document =
                JsonSerializer.Deserialize<AdaptiveFanPolicyShadowConfigDocument>(
                    json,
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "Adaptive shadow config deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Adaptive shadow config is not valid schema-v1 JSON.",
                ex);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Adaptive shadow config schemaVersion must be {CurrentSchemaVersion}.");
        }

        if (!string.Equals(
                document.TargetProfileId,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Adaptive shadow config targetProfileId must be " +
                $"{Hp8C40TargetProfile.Instance.Id}.");
        }

        if (!string.Equals(
                document.Purpose,
                RequiredPurpose,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Adaptive shadow config purpose must be '{RequiredPurpose}'.");
        }

        if (document.AuthorizedForProduction is not false)
        {
            throw new InvalidDataException(
                "Adaptive shadow config must explicitly set authorizedForProduction=false.");
        }

        if (document.MinimumLevel <
                Hp8C40TargetProfile.MinimumValidatedFanLevel ||
            document.MaximumLevel >
                Hp8C40TargetProfile.MaximumValidatedFanLevel)
        {
            throw new InvalidDataException(
                "Adaptive shadow config fan envelope must remain inside the " +
                $"physically validated equal-only " +
                $"{Hp8C40TargetProfile.MinimumValidatedFanLevel}.." +
                $"{Hp8C40TargetProfile.MaximumValidatedFanLevel} range.");
        }

        if (!double.IsFinite(document.MaximumSampleGapSeconds) ||
            document.MaximumSampleGapSeconds <= 0)
        {
            throw new InvalidDataException(
                "Adaptive shadow config maximumSampleGapSeconds must be finite and positive.");
        }

        if (document.CpuTemperatureCurve is null ||
            document.GpuTemperatureCurve is null ||
            document.CpuPowerCurve is null ||
            document.GpuPowerCurve is null ||
            document.CpuLoadCurve is null ||
            document.GpuLoadCurve is null)
        {
            throw new InvalidDataException(
                "Adaptive shadow config must define all six demand curves.");
        }

        var config =
            new AdaptiveFanPolicyConfig(
                MinimumLevel: document.MinimumLevel,
                MaximumLevel: document.MaximumLevel,
                MaximumUpStepPerSample: document.MaximumUpStepPerSample,
                MaximumDownStepPerSample: document.MaximumDownStepPerSample,
                DecreaseConfirmationSamples: document.DecreaseConfirmationSamples,
                DecreaseDeadbandLevels: document.DecreaseDeadbandLevels,
                MaximumSampleGap:
                    TimeSpan.FromSeconds(
                        document.MaximumSampleGapSeconds),
                CpuTemperatureCurve: document.CpuTemperatureCurve,
                GpuTemperatureCurve: document.GpuTemperatureCurve,
                CpuPowerCurve: document.CpuPowerCurve,
                GpuPowerCurve: document.GpuPowerCurve,
                CpuLoadCurve: document.CpuLoadCurve,
                GpuLoadCurve: document.GpuLoadCurve,
                CpuTemperatureTrendCurve:
                    document.CpuTemperatureTrendCurve);

        // Constructor validation is deliberately reused so shadow config and
        // the pure policy engine cannot drift apart.
        _ = new AdaptiveFanPolicyEngine(config);

        return config;
    }
}
