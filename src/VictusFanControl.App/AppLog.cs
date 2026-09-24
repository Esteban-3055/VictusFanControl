namespace VictusFanControl.App;

internal static class AppLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VictusFanControl",
        "logs");

    public static string CurrentLogPath =>
        Path.Combine(LogDirectory, $"events-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Initialize()
    {
        Directory.CreateDirectory(LogDirectory);
        Write("VictusFanControl application log opened.");
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(CurrentLogPath);

                File.AppendAllText(
                    CurrentLogPath,
                    $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Persistent diagnostics must never crash the monitoring process.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes)
        {
            return;
        }

        var rotated = path + ".1";
        if (File.Exists(rotated))
        {
            File.Delete(rotated);
        }

        File.Move(path, rotated);
    }
}
