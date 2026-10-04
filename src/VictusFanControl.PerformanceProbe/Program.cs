using System.Text;
using System.Text.Json;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceProbe;

internal static class Program
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    [STAThread]
    public static int Main(
        string[] args)
    {
        try
        {
            if (args.Length == 1 &&
                args[0] == "--self-test")
            {
                return PerformancePowerSourceQuerySelfTest.Run(
                    Console.Out);
            }

            if (args.Length == 1 &&
                args[0] == "--notification-self-test")
            {
                return PowerSourceNotificationQualification.SelfTest(
                    Console.Out);
            }

            if (args.Length > 0 &&
                args[0] == "--watch-power-source")
            {
                return PowerSourceNotificationQualification.Run(
                    args);
            }

            if (!TryParse(
                    args,
                    out var label,
                    out var outputPath))
            {
                PrintHelp();
                return 2;
            }

            var reader =
                new WindowsPerformancePowerSourceReader();

            var observation =
                reader.Read();

            var report =
                new PowerSourceReport(
                    SchemaVersion: 1,
                    CapturedAtUtc: DateTimeOffset.UtcNow,
                    TargetProfileId,
                    Label: label,
                    Observation: observation,
                    HardwareWritesPerformed: false);

            var json =
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            Console.WriteLine(json);

            if (!string.IsNullOrWhiteSpace(
                    outputPath))
            {
                DurableWrite(
                    outputPath!,
                    json);
            }

            return observation.Succeeded
                ? 0
                : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Performance power-source probe failed: " +
                ex.Message);

            return 4;
        }
    }

    private static bool TryParse(
        string[] args,
        out string label,
        out string? outputPath)
    {
        label =
            "manual";

        outputPath =
            null;

        if (args.Length < 1 ||
            args[0] !=
                "--observe-power-source")
        {
            return false;
        }

        for (var index = 1;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--label"
                    when index + 1 <
                         args.Length:
                    label =
                        args[++index];
                    break;

                case "--output"
                    when index + 1 <
                         args.Length:
                    outputPath =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    return false;
            }
        }

        return !string.IsNullOrWhiteSpace(
            label);
    }

    private static void DurableWrite(
        string path,
        string content)
    {
        var directory =
            Path.GetDirectoryName(
                path);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        using var stream =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);

        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false),
                4096,
                leaveOpen: true);

        writer.Write(
            content);

        writer.Flush();

        stream.Flush(
            flushToDisk: true);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            "VictusFanControl.PerformanceProbe --observe-power-source [--label <name>] [--output <json-path>]");

        Console.WriteLine(
            "VictusFanControl.PerformanceProbe --self-test");

        Console.WriteLine(
            "VictusFanControl.PerformanceProbe --notification-self-test");

        Console.WriteLine(
            "VictusFanControl.PerformanceProbe --watch-power-source --expect <ac|battery> --timeout-seconds <5..120> --output <json-path>");

        Console.WriteLine(
            "Both probe modes are read-only. Notifications are triggers only; GetSystemPowerStatus confirms the source.");
    }

    private readonly record struct PowerSourceReport(
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        string TargetProfileId,
        string Label,
        PerformancePowerSourceObservation Observation,
        bool HardwareWritesPerformed);
}
