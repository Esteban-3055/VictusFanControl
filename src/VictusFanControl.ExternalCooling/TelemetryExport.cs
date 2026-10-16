namespace VictusFanControl.ExternalCooling;

public enum ExternalCoolingMode { Disabled, MonitorOnly, MonitorAndRequest }
public enum ExternalCoolingConnectionState { Disabled, Disconnected, Connecting, Authenticating, Connected, Reconnecting, Error }

/// <summary>Nullable measurements are unavailable, invalid or expired, never fabricated zeroes.</summary>
public sealed record ExportTelemetry
{
    public int ValidForMs { get; init; } = ExternalCoolingProtocol.RequestValidForMilliseconds;
    public long? ReplyToDeviceSequence { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public DateTimeOffset? SampledAtUtc { get; init; }
    public DateTimeOffset? DecisionAtUtc { get; init; }
    public DateTimeOffset? CpuTempSampledAtUtc { get; init; }
    public DateTimeOffset? GpuTempSampledAtUtc { get; init; }
    public DateTimeOffset? RamSampledAtUtc { get; init; }
    public double? CpuUsagePct { get; init; }
    public double? GpuUsagePct { get; init; }
    public double? RamUsagePct { get; init; }
    public double? DemandaPct { get; init; }
    public int? Solicitud { get; init; }
    public bool SolicitudValida { get; init; }
    public int? CpuTargetLevel { get; init; }
    public int? GpuTargetLevel { get; init; }
    public string ModoSolicitado { get; init; } = "Firmware";
    public string ModoEfectivo { get; init; } = "Unavailable";
    public double? CpuTempC { get; init; }
    public double? GpuTempC { get; init; }
    public string PowerSource { get; init; } = "Unknown";
    public string RequestEvidence { get; init; } = "Unavailable";

    public ExportTelemetry WithoutRequest(string reason="MonitorOnly") => this with
    { Solicitud=null, SolicitudValida=false, CpuTargetLevel=null, GpuTargetLevel=null, RequestEvidence=reason };

    public static double? Range(double? value, double min, double max) =>
        value is {} v && double.IsFinite(v) && v >= min && v <= max ? v : null;
}

public interface ITelemetryExportSource { ExportTelemetry Capture(DateTimeOffset now); }

public sealed record ExternalCoolingSettings
{
    public int SchemaVersion { get; init; } = 1;
    public ExternalCoolingMode Mode { get; init; }
    public string Host { get; init; } = "victus-base.local";
    public int Port { get; init; } = 8765;
    public bool AutoReconnect { get; init; } = true;
    public bool UseSimulator { get; init; }
    public string EffectiveHost => UseSimulator ? "127.0.0.1" : Host;
    public void Validate()
    {
        if (SchemaVersion != 1 || !Enum.IsDefined(Mode) || Port is <1 or >65535 || Host is null || Host.Length>253 ||
            Mode!=ExternalCoolingMode.Disabled && !UseSimulator &&
            (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) ||
             Uri.CheckHostName(Host) == UriHostNameType.Unknown))
            throw new ArgumentException("Host, puerto o modo de base externa no válido.");
    }
}
