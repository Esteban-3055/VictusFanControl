using System.Text.Json;

namespace VictusFanControl.Watchdog;

internal static class AtomicJsonFile
{
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                "JSON result path has no parent directory.");

        Directory.CreateDirectory(directory);

        var tempPath =
            path +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
