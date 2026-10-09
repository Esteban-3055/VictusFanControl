using System.Globalization;

namespace VictusFanControl.Telemetry;

public static class ConsoleTelemetryPrinter
{
    public static void Print(TelemetrySnapshot s)
    {
        Console.WriteLine(
            $"{s.Timestamp.ToLocalTime():HH:mm:ss}  " +
            $"CPU pkg {F(s.CpuTemperatureC),6} C  core-max {F(s.CpuCoreMaxTemperatureC),6} C  " +
            $"{F(s.CpuPackagePowerW),6} W  {F(s.CpuLoadPercent),6}%  |  " +
            $"GPU {F(s.GpuTemperatureC),6} C  {F(s.GpuPowerW),6} W  {F(s.GpuLoadPercent),6}%  |  " +
            $"FAN CPU {FanRpm(s, s.CpuFanRpm),5} RPM  GPU {FanRpm(s, s.GpuFanRpm),5} RPM");
    }

    private static string F(double? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a";

    private static string FanRpm(TelemetrySnapshot snapshot, double? value) =>
        (snapshot.FanRpmResolution == 100 && value.HasValue ? "~" : "") + Rpm(value);

    private static string Rpm(double? value) =>
        value?.ToString("0", CultureInfo.InvariantCulture) ?? "n/a";
}
