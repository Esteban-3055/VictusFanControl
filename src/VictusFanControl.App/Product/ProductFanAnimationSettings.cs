using System.Text;
using System.Text.Json;

namespace VictusFanControl.App;

// Presentation-only file, separate from control profiles and the versioned installer payload.
// Reloaded by the existing one-second UI tick only while Home is visible.
internal sealed class ProductFanAnimationSettings
{
    internal const string Key = "segundosPorVueltaA3000Rpm";
    internal const double MinimumSeconds = 0.1, MaximumSeconds = 60;
    private const int MaximumBytes = 4096;
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VictusFanControl", "fan-animation.json");
    private readonly string _path;
    internal double SecondsPerRevolution { get; private set; } = ProductFanAnimation.ReferenceSecondsPerRevolution;
    internal static bool IsValid(double value) => double.IsFinite(value) && value is >=MinimumSeconds and <=MaximumSeconds;

    internal ProductFanAnimationSettings(string path)
    {
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(Encoding.UTF8.GetBytes("{\n  \"" + Key + "\": 1.0\n}\n"));
        }
        catch (IOException) { } // An existing file belongs to the user; never overwrite it.
        catch (UnauthorizedAccessException) { }
        Reload();
    }

    internal bool Reload()
    {
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[MaximumBytes + 1];
            int length = 0, read;
            while (length < buffer.Length && (read = file.Read(buffer, length, buffer.Length - length)) > 0) length += read;
            if (length > MaximumBytes) return false;
            // Windows editors may save a UTF-8 BOM; accept it without changing the user's file.
            var offset = length >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf ? 3 : 0;
            using var json = JsonDocument.Parse(buffer.AsMemory(offset, length - offset), new() { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            var properties = json.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != Key || properties[0].Value.ValueKind != JsonValueKind.Number ||
                !properties[0].Value.TryGetDouble(out var seconds) || !IsValid(seconds)) return false;
            var changed = seconds != SecondsPerRevolution;
            SecondsPerRevolution = seconds;
            return changed;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; } // Includes incomplete writes while the editor saves.
    }
}
