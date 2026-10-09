using Microsoft.Extensions.Hosting;

namespace VictusFanControl.Watchdog;

/// <summary>
/// One-shot read-only HP 8C40 M2 probe hosted as a real LocalSystem service.
/// After publishing its result it remains alive until the test harness stops
/// it, proving the process is actually hosted by SCM in Session 0.
/// </summary>
internal sealed class M2Hp8C40ReadOnlyWorker :
    BackgroundService
{
    private readonly WatchdogOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public M2Hp8C40ReadOnlyWorker(
        WatchdogOptions options,
        IHostApplicationLifetime lifetime)
    {
        _options = options;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var log =
            new WatchdogFileLog(
                _options.LogDirectory,
                "watchdog-m2-8c40");

        M2Hp8C40ReadOnlyResult result;

        try
        {
            result =
                M2Hp8C40ReadOnlyProbe.Run(
                    _options,
                    log);

            AtomicJsonFile.Write(
                _options.ResultPath,
                result);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 111;

            try
            {
                log.Write(
                    $"M2 8C40 INFRASTRUCTURE FAIL: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }

            _lifetime.StopApplication();
            return;
        }

        if (!result.Success)
        {
            Environment.ExitCode = 110;
            _lifetime.StopApplication();
            return;
        }

        try
        {
            await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            log.Write(
                $"M2 8C40 STOP run={result.RunId}; read-only gate requires no hardware cleanup.");
        }
    }
}
