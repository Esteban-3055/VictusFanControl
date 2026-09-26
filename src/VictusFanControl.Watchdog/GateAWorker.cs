using Microsoft.Extensions.Hosting;

namespace VictusFanControl.Watchdog;

internal sealed class GateAWorker : BackgroundService
{
    private readonly WatchdogOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public GateAWorker(WatchdogOptions options, IHostApplicationLifetime lifetime)
    {
        _options = options;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var log = new WatchdogFileLog(_options.LogDirectory, "watchdog-gate-a");

        GateAProbeResult result;
        try
        {
            result = GateAProbe.Run(_options, log);
            AtomicJsonFile.Write(_options.ResultPath, result);
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

}
