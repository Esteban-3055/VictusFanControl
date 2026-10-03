# HP 8C40/F.18 WMI fan telemetry

Software migration from baseline `5ad0821b0e7a4272abdc31f4a7d0d8fcb716332c`
(CI #1199 / run 37075117009 SUCCESS). P16 remains fail-closed; this work
performs no hardware execution and creates no physical authorization.

## Evidence and scope

The supplied ACPI analysis identifies HP command `20008h/2Dh`, through
`WMID.WMAA -> HWMC -> GM2D`, as reading RPM1..RPM4 and returning each
little-endian tachometer value divided by 100. The user reported 15 successful
read-only WMI calls (CPU 26..27, GPU 23..24). This is supporting target evidence,
not a new qualification of the complete application, control path, or suspend.
The ACPI ZIP and raw CSV are not included in this repository; their earlier
analysis/test report is the source of this semantic mapping.

`HardwareTelemetryReader` now selects `HpWmiFanTelemetryReader` only for the
exact resolved `HP-8C40-9D0R1LA-F18` profile. It does not construct or recover
`AcpiEcReader` for that profile's periodic RPM telemetry. There is no automatic
fallback to direct EC reads if WMI fails. The HP 88F8 legacy path remains EC.
Unsupported targets do not initialize a fan EC reader.

**Control qualification is separate:** HP fan writes (`2Eh`), release FF/FF and
LegacyDefault are unchanged. Ownership still uses stable EC 34h/35h and guards
still use EC. `Hp8C40FanHardware.ReadEcState`, command-response tach proofs,
read-only diagnostic probes and the installed M4 watchdog remain unchanged.
These paths can still perform direct EC tachometer reads; this migration does
not claim to eliminate all EC access or resolve the entire EC/ACPI risk.
WMI speed never substitutes for setpoint acknowledgement or ownership proof.

## Acquisition and freshness

- `ReadCached()` runs on the normal telemetry path but never waits on WMI.
  Connection, provider discovery and invocation run in a background task.
- Only the existing `BuildGetFanLevelRequest()` envelope is sent: command
  20008h, type 2Dh, four zero input bytes, 128 output bytes.
- New queries are admitted at least 1000 ms after the previous successful
  completion. Demand comes from snapshots; this is a maximum cadence, not a
  promise of exactly 1 Hz. Calls of about 350 ms with 1-second snapshots can
  yield one WMI call about every two seconds.
- One static admission slot covers all production fan-reader instances in this
  process, including disposal/recreation during telemetry recovery. No queued
  replacement queries can accumulate behind a stuck synchronous provider.
- A sample expires at age >=3000 ms. Age starts **before** WMI acquisition,
  including connection/discovery latency, and uses monotonic TickCount64.
  The wall-clock acquisition timestamp is preserved independently.
- A 5000-ms logical timeout clears cached health and rejects late results.
  It does not forcibly cancel WMI or release its slot before actual completion.
  A native call that never returns keeps this fan channel unavailable, while
  fast CPU/GPU telemetry and application disposal remain unblocked.
- Any rejected/short/implausible response or exception clears both cached fans.
  Retry backoff is 5000 ms after failure/completion; successful recovery is
  counted. A late call is checked at completion even without a foreground poll.
- Valid bytes are 0..100 (0..10000 nominal RPM); 0 is allowed in Firmware.
  Asymmetric measured speeds are allowed. Command restrictions 10..50 and
  equal CPU/GPU targets do not apply to measured speed bytes.

## Representation

A speed byte L represents the bin `[100L, 100L+99]` RPM. `CpuFanRpm/GpuFanRpm`
contain its lower nominal value for source compatibility. GUI, tray and console
prefix this with `~`; diagnostics state the 100-RPM resolution. Snapshot/CSV
carry source, resolution, query-start UTC timestamp, sample age, and both raw
speed bytes. Existing CSV columns retain their order; six metadata columns
are appended. Consumers needing precision should use these fields.

`TelemetrySnapshot.IsComplete` also requires fan acquisition metadata and an
age below 3000 ms for this WMI source. Initial/no-data, expired, failed and
timeout reads have null fan RPM and cannot contribute to healthy recovery.
The existing SafetyGate, thermal thresholds and controller fail-closed behavior
are unchanged.

## Lifecycle

Suspend synchronously pauses this reader, invalidates its cache and increments
its epoch. Completion from an older epoch cannot publish a sample. No further
polling starts on a paused/disposed reader. Existing TelemetryWorker recovery
creates a new reader after resume; the static admission slot covers any older
still-pending call. A post-construction lifecycle check also pauses a reader
created during a concurrent suspend without holding the lifecycle lock across
native backend initialization.

`WaitForHardwareReadQuiescenceAsync` now includes in-flight WMI work within the
same cancellation/timeout budget, rather than declaring quiescence based only
on synchronous MSR/NVML reads. Application shutdown does not indefinitely join
the native WMI call.

This telemetry epoch fence is **not** the pending P16 one-shot qualification
session interruption latch. That separate hardening requirement remains in
`p16-target-checkpoint.json`; no new P16 hardware gate may be opened here.

## Verification

`--fan-wmi-telemetry-self-test` uses fake transport, fake monotonic time and
explicit worker barriers. It covers the exact read-only envelope, quantization,
asymmetric speed, cadence, payload validation, zero/high/FF values, cache
invalidation, retry/recovery, age boundaries, completion latency, logical timeout,
single-flight across recreation, disposal, pause and cancellable quiescence.

`test-wmi-fan-telemetry-invariants.ps1` protects read-only scope, exact-profile
selection, no periodic EC fallback, freshness, suspend/quiescence integration
and the closed P16 gate. CI runs both plus all existing backend, safety,
watchdog, Windows PowerShell 5.1, build and packaging checks. P16 evidence
packages now include these telemetry/worker/snapshot sources.

The source transport retains the existing System.Management timeout setting.
Microsoft documents timeout semantics for WMI operations; application health
and concurrency are bounded independently rather than assuming cancellation
of a synchronous native provider call:
https://learn.microsoft.com/en-us/dotnet/api/system.management.invokemethodoptions

## Local verification record (Windows CI pending)

Implementation commit: `5b151f9` (full identity in the telemetry checkpoint).
C# compilation of core and GUI passed with warnings treated as errors using
.NET 8.0.419 Roslyn and official .NET/WindowsDesktop reference assemblies.
The container's normal dotnet CLI/MSBuild entry points cannot inspect process
metadata, so this is compiler verification, not a completed SDK build/publish.
The GUI compiler check used temporary equivalents of SDK-generated global
usings/ApplicationConfiguration; actual SDK generation remains a Windows CI check.

All seven new fake-transport cases passed. SafetyGate and both HP BIOS request
contracts passed locally. The existing backend self-test requires the Windows
active-time clock and is not executable in this Linux environment. The inherited
adaptive Manual retry test was intermittent in **both** a detached unmodified
baseline and this migration (one PASS/two FAIL in each three-run diagnostic
comparison); it remains an unresolved CI check, not a claimed regression fix.

Automatic approval review blocked the public GitHub push pending explicit user
publication authorization. The remote branch remains at `5ad0821`; no migration
CI run exists yet, implementationValidation remains null, and softwareClosure
is false. Full Windows build, PowerShell runtime/invariant tests, backend and
watchdog regressions, deterministic packaging, and a separate same-head software
closure still need to finish after an authorized push. No physical gate was opened.
