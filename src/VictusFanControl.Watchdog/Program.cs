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

        builder.Services.AddWindowsService(serviceOptions =>
        {
            // Must match the SCM service name used by the installer.
            serviceOptions.ServiceName = ServiceName;
        });

        // AddWindowsService enables Event Log integration by default. Gate A
        // uses only its explicit ProgramData log/result files, so remove all
        // logging providers after the Windows-service lifetime is registered.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService<GateAWorker>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return Environment.ExitCode;
    }
}
