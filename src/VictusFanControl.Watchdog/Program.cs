using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VictusFanControl.Watchdog;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WatchdogOptions options;
        try
        {
            options = WatchdogOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options.Mode == WatchdogRunMode.GateBSelfTest)
        {
            return await GateBRestoreSelfTest.RunAsync(Console.Out)
                .ConfigureAwait(false);
        }

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(serviceOptions =>
        {
            serviceOptions.ServiceName = options.ServiceName;
        });

        // Gate A/B write their own ProgramData diagnostics. Keep the service
        // independent of Event Log source creation and related side effects.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(options);

        if (options.Mode == WatchdogRunMode.GateBRestoreTest)
        {
            builder.Services.AddHostedService<GateBRestoreWorker>();
        }
        else
        {
            builder.Services.AddHostedService<GateAWorker>();
        }

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return Environment.ExitCode;
    }
}
