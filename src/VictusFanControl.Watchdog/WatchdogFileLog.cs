namespace VictusFanControl.Watchdog;

internal sealed class WatchdogFileLog
{
    private readonly object _gate = new();
    private readonly string _logDirectory;
    private readonly string _filePrefix;

    public WatchdogFileLog(
        string logDirectory,
        string filePrefix)
    {
        _logDirectory = logDirectory;
        _filePrefix = filePrefix;
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_logDirectory);

            var path = Path.Combine(
                _logDirectory,
                $"{_filePrefix}-{DateTime.Now:yyyy-MM-dd}.log");

            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
        }
    }
}
