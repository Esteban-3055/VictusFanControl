using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

internal sealed record GateDServiceStatus(
    bool Ready,
    bool Blocked,
    DateTimeOffset Timestamp,
    int ProcessId,
    int SessionId,
    string AccountName,
    HardwareIdentity? Hardware,
    string? RecoveryDisposition,
    string Detail,
    string JournalPath,
    string PipeName);
