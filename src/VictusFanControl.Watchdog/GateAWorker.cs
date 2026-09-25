using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace VictusFanControl.Watchdog;

internal sealed class GateAWorker : BackgroundService
{
    private readonly GateAOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public GateAWorker(GateAOptions options, IHostApplicationLifetime lifetime)
    {
        _options = options;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var log = new GateAFileLog(_options.LogDirectory);

        GateAProbeResult result;
        try
        {
            result = GateAProbe.Run(_options, log);
            WriteResultAtomically(_options.ResultPath, result);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 71;
            try
            {
                log.Write(
                    $"GATE A INFRASTRUCTURE FAIL: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }

            _lifetime.StopApplication();
            return;
        }

        if (!result.Success)
        {
            Environment.ExitCode = 70;
            _lifetime.StopApplication();
            return;
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.Write(
                $"GATE A STOP run={result.RunId}; no hardware write cleanup required.");
        }
    }

    private static void WriteResultAtomically(
        string resultPath,
        GateAProbeResult result)
    {
        var directory = Path.GetDirectoryName(resultPath) ??
            throw new InvalidOperationException(
                "Gate A result path has no parent directory.");

        Directory.CreateDirectory(directory);
        var tempPath = resultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, resultPath, overwrite: true);
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
