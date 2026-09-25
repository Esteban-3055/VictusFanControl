using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

internal sealed record GateBServiceResult(
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
    string? BeforeState,
    string? AfterState,
    int? BeforeCpuSetpoint,
    int? BeforeGpuSetpoint,
    int? AfterCpuSetpoint,
    int? AfterGpuSetpoint,
    bool RestoreCallSucceeded,
    bool VerifiedFfFf,
    int? BiosCpuCurrentLevelAfter,
    int? BiosGpuCurrentLevelAfter,
    double ElapsedMilliseconds,
    string? Failure);
