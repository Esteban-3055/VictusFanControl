using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VictusFanControl.Watchdog;

internal static class Program
{
    public const string ServiceName = "VictusFanControlWatchdogGateA";
    public const string ServiceDisplayName = "VictusFanControl Watchdog Gate A";

    public static async Task<int> Main(string[] args)
    {
        GateAOptions options;
        try
        {
            options = GateAOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();

        builder.Services.AddWindowsService(serviceOptions =>
        {
            serviceOptions.ServiceName = ServiceDisplayName;
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService<GateAWorker>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return Environment.ExitCode;
    }
}
