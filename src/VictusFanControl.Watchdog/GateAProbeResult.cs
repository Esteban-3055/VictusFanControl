using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

internal sealed record GateAProbeResult(
    bool Success,
    DateTimeOffset Timestamp,
    Guid RunId,
    int ProcessId,
    int SessionId,
    string AccountName,
    string ModulesDirectory,
    HardwareIdentity? Hardware,
    bool TargetMatched,
    string? TargetReason,
    string? EcState,
    int? CpuSetpoint,
    int? GpuSetpoint,
    int? CpuRpm,
    int? GpuRpm,
    int? BiosCpuCurrentLevel,
    int? BiosGpuCurrentLevel,
    string? Failure);
