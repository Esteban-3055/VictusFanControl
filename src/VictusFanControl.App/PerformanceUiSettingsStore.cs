using System.Text.Json;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal sealed record PerformanceUiSettingsDocument
{
    public int SchemaVersion { get; init; } =
        PerformanceUiSettingsStore.CurrentSchemaVersion;

    public int AcPl1Watts { get; init; } =
        CpuPowerProductDefaults.DefaultAcPl1Watts;

    public int AcPl2Watts { get; init; } =
        CpuPowerProductDefaults.DefaultAcPl2Watts;

    public int BatteryPl1Watts { get; init; } =
        CpuPowerProductDefaults.DefaultBatteryPl1Watts;

    public int BatteryPl2Watts { get; init; } =
        CpuPowerProductDefaults.DefaultBatteryPl2Watts;
}

/// <summary>
/// Persists only CPU power-limit preferences.
///
/// The file grants no Guardian session, write authority, startup persistence or
/// automatic-profile authority. Loading or saving this document never touches
/// RAPL hardware.
/// </summary>
internal static class PerformanceUiSettingsStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly string SettingsDirectory =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl");

    public static readonly string SettingsPath =
        Path.Combine(
            SettingsDirectory,
            "performance-ui-settings.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        };

    public static PerformanceUiSettingsDocument Load() =>
        Load(SettingsPath);

    internal static PerformanceUiSettingsDocument Load(
        string path)
    {
        try
        {
            if (!File.Exists(path))
                return Default();

            var document =
                JsonSerializer.Deserialize<PerformanceUiSettingsDocument>(
                    File.ReadAllText(path),
                    JsonOptions);

            return document is not null &&
                   Valid(document)
                ? document
                : Default();
        }
        catch
        {
            // Corrupt/unreadable preferences cannot create hardware authority.
            return Default();
        }
    }

    public static void Save(
        PerformanceUiSettingsDocument document) =>
        Save(
            document,
            SettingsPath);

    internal static void Save(
        PerformanceUiSettingsDocument document,
        string path)
    {
        ArgumentNullException.ThrowIfNull(
            document);

        if (!Valid(document))
        {
            throw new ArgumentOutOfRangeException(
                nameof(document),
                "CPU performance UI settings are outside the target-specific product envelope.");
        }

        var directory =
            Path.GetDirectoryName(
                path);

        if (string.IsNullOrWhiteSpace(
                directory))
        {
            throw new ArgumentException(
                "Performance UI settings path must have a parent directory.",
                nameof(path));
        }

        Directory.CreateDirectory(
            directory);

        var tempPath =
            path + ".tmp";

        File.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(
                document,
                JsonOptions));

        File.Move(
            tempPath,
            path,
            overwrite: true);
    }

    public static PerformanceUiSettingsDocument Default() =>
        new();

    internal static bool Valid(
        PerformanceUiSettingsDocument document) =>
        document.SchemaVersion ==
            CurrentSchemaVersion &&
        CpuPowerProductDefaults.IsConfigurable(
            document.AcPl1Watts,
            document.AcPl2Watts) &&
        CpuPowerProductDefaults.IsConfigurable(
            document.BatteryPl1Watts,
            document.BatteryPl2Watts);
}
