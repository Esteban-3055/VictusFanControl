namespace VictusFanControl.Watchdog;

internal sealed class GateAFileLog
{
    private readonly object _gate = new();
    private readonly string _logDirectory;

    public GateAFileLog(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(
                _logDirectory,
                $"watchdog-gate-a-{DateTime.Now:yyyy-MM-dd}.log");

            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
        }
    }
}
