using System.Globalization;
using System.Text;

namespace VictusFanControl.Telemetry;

public sealed class CsvTelemetryLogger : IAsyncDisposable
{
    private readonly StreamWriter _writer;

    public CsvTelemetryLogger(string path)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public async Task WriteHeaderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _writer.WriteLineAsync(
            "timestamp_utc,cpu_name,cpu_temp_c,cpu_package_power_w,cpu_load_pct,gpu_name,gpu_temp_c,gpu_power_w,gpu_load_pct,cpu_fan_rpm,gpu_fan_rpm");
    }

    public Task WriteAsync(TelemetrySnapshot s, CancellationToken cancellationToken)
    {
        var line = string.Join(',', new[]
        {
            Escape(s.Timestamp.ToString("O")),
            Escape(s.CpuName),
            Number(s.CpuTemperatureC),
            Number(s.CpuPackagePowerW),
            Number(s.CpuLoadPercent),
            Escape(s.GpuName),
            Number(s.GpuTemperatureC),
            Number(s.GpuPowerW),
            Number(s.GpuLoadPercent),
            Number(s.CpuFanRpm),
            Number(s.GpuFanRpm)
        });

        cancellationToken.ThrowIfCancellationRequested();
        return _writer.WriteLineAsync(line);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _writer.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync();
        _writer.Dispose();
    }

    private static string Number(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var escaped = value.Replace("\"", "\"\"");
        return escaped.IndexOfAny(new[] { ',', '\"', '\r', '\n' }) >= 0 ? $"\"{escaped}\"" : escaped;
    }
}
