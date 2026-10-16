using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.App;

internal static class NvmlTelemetryProtocol
{
    internal const int Version = 1;
    internal const int MaximumLineCharacters = 4096;
    internal sealed record Request(int Version, string Session, long Sequence, string Operation,
        string? PreferredDeviceName = null, bool RequirePreferredDevice = false);
    internal sealed record Reply(int Version, string Session, long Sequence, string DeviceName,
        DateTimeOffset? SampledAtUtc = null, NvmlClient.GpuSample? Sample = null, bool Failed = false);

    internal static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken cancellation)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellation).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == MaximumLineCharacters) throw new InvalidDataException("NVML helper response exceeds its size limit.");
            line.Append(character[0]);
        }
        if (line.Length != 0) throw new EndOfStreamException("Incomplete NVML helper response.");
        return null;
    }

    internal static T Parse<T>(string? line) where T : class => line is null
        ? throw new EndOfStreamException("NVML helper disconnected.")
        : JsonSerializer.Deserialize<T>(line) ?? throw new InvalidDataException("Empty NVML helper message.");

    internal static Task WriteAsync<T>(TextWriter writer, T value, CancellationToken cancellation) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellation);
}
