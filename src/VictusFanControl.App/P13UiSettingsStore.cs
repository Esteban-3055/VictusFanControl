using System.Text.Json;

namespace VictusFanControl.App;

internal sealed record P13UiSettingsDocument
{
    public int SchemaVersion { get; init; } = P13UiSettingsStore.CurrentSchemaVersion;
    public int ManualEqualLevel { get; init; } = P13UiSettingsStore.DefaultManualEqualLevel;
}

/// <summary>
/// Persists only non-authorizing P13 UI preferences.
///
/// No operating mode, hardware authorization, automatic-policy state or
/// ownership state is persisted. Every process start therefore begins in
/// Firmware mode regardless of this file.
/// </summary>
internal static class P13UiSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultManualEqualLevel = 30;

    private static readonly string SettingsDirectory =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl");

    public static readonly string SettingsPath =
        Path.Combine(SettingsDirectory, "p13-ui-settings.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        };

    public static P13UiSettingsDocument Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return Default();
            }

            var document =
                JsonSerializer.Deserialize<P13UiSettingsDocument>(
                    File.ReadAllText(SettingsPath),
                    JsonOptions);

            if (document is null ||
                document.SchemaVersion != CurrentSchemaVersion ||
                document.ManualEqualLevel is < 10 or > 50)
            {
                return Default();
            }

            return document;
        }
        catch
        {
            // A corrupt/unreadable preference file cannot affect control state.
            return Default();
        }
    }

    public static void SaveManualEqualLevel(int level)
    {
        if (level is < 10 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "P13 manual UI preference must remain inside 10..50.");
        }

        Directory.CreateDirectory(SettingsDirectory);

        var document =
            new P13UiSettingsDocument
            {
                ManualEqualLevel = level
            };

        var tempPath =
            SettingsPath + ".tmp";

        File.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(
                document,
                JsonOptions));

        File.Move(
            tempPath,
            SettingsPath,
            overwrite: true);
    }

    private static P13UiSettingsDocument Default() =>
        new()
        {
            ManualEqualLevel = DefaultManualEqualLevel
        };
}
