# Telemetry robustness

Fan control must not be enabled until telemetry failures are observable, bounded and recoverable.

## Rules

1. A sensor failure is never converted silently into a plausible numeric value.
2. Low-level transient reads use bounded retries.
3. CPU/GPU fan tachometers are acquired as one mutex-scoped EC snapshot.
4. Persistent PawnIO/NVML failures trigger backend reinitialization.
5. All values receive basic plausibility validation.
6. A complete telemetry snapshot is required before the future controller may consume it.
7. Long soak tests use a strict **zero missing samples** acceptance criterion.
8. Future fan-control safety logic must treat stale/missing critical telemetry as a reason to return control to HP firmware, not as permission to reuse an old value indefinitely.

## Current retry/recovery behavior

- Intel MSR: 3 bounded I/O attempts.
- ACPI EC: 5 attempts for the complete fan-tachometer snapshot while holding `Global\Access_EC`.
- NVIDIA NVML: 3 attempts per metric; if reads remain invalid, NVML is reinitialized once and retried.
- HardwareTelemetryReader: if a backend still throws, its client is reconstructed and the read is attempted again.
- Windows CPU load: native `GetSystemTimes` failures are surfaced as errors.

## Health soak

Run from an elevated PowerShell:

```powershell
.\scripts\test-telemetry-health.ps1 -Minutes 30
```

The test warms the differential CPU power/load counters, then checks every sample for:

- CPU temperature
- CPU package power
- CPU load
- GPU temperature
- GPU power
- GPU load
- CPU fan RPM
- GPU fan RPM

The test exits successfully only when every post-warm sample contains all eight values.

Before enabling actual fan writes, repeat the soak under idle, browser/video, CPU load, gaming, and after sleep/resume.
