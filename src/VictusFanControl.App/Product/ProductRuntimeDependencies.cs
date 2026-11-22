using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

// Explicit dependencies let the actual product coordinator run against isolated
// controllers and IPC fixtures. Production startup supplies none and still
// constructs the qualified Windows/HP services. No qualification is replaced.
internal sealed record ProductRuntimeDependencies(HardwareIdentity Hardware, IFanControlBackend FanBackend,
    TelemetryWorker Telemetry, PerformanceGuardianClient Performance, IPerformancePowerSourceReader PowerSource,
    Func<PerformanceRecoveryPreview> RecoveryInventory,Func<PerformanceRetainedJournals>? RetainedJournals=null,ProductWmiLifetime? WmiLifetime=null);
