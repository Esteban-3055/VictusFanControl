namespace VictusFanControl.Hardware.Nvidia;

/// <summary>Read-only GPU acquisition. It grants no clock or power control authority.</summary>
internal interface INvmlTelemetryReader : IDisposable
{
    string DeviceName { get; }
    NvmlClient.GpuSample ReadSample();
}

/// <summary>A failed isolated acquisition must not trigger an immediate in-process fallback.</summary>
internal sealed class NvmlTelemetryUnavailableException(string message) : IOException(message);
