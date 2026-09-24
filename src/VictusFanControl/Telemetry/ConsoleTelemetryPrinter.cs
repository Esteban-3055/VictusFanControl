using System.Globalization;

namespace VictusFanControl.Telemetry;

public static class ConsoleTelemetryPrinter
{
    public static void Print(TelemetrySnapshot s)
    {
        Console.WriteLine(
            $"{s.Timestamp.ToLocalTime():HH:mm:ss}  " +
            $"CPU {F(s.CpuTemperatureC),6} C  {F(s.CpuPackagePowerW),6} W  {F(s.CpuLoadPercent),6}%  |  " +
            $"GPU {F(s.GpuTemperatureC),6} C  {F(s.GpuPowerW),6} W  {F(s.GpuLoadPercent),6}%  |  " +
            $"FAN CPU {Rpm(s.CpuFanRpm),5} RPM  GPU {Rpm(s.GpuFanRpm),5} RPM");
    }

    private static string F(double? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Rpm(double? value) =>
        value?.ToString("0", CultureInfo.InvariantCulture) ?? "n/a";
}
