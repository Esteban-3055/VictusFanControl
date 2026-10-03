# WMI broker / 5-sample work — Step 4 bounded telemetry history

Status: implemented; exact-head Windows CI required before software closure.
No hardware qualification or new physical test authorization.

Input: `5723eb2620aca990223884402e213d4fc94774b6`, tree
`621b8c821999d9a44f5c56f336fa1c9ba24f9c0c`; Windows CI #1229,
run `37109830712`, SUCCESS. Remote branch was checked before editing.
Branch: `feature/victus-8c40-wmi-broker-5sample`.

## Implementation and current boundary

`HpWmiFanSampleWindow` stores **at most five** accepted real acquisitions.
Startup/recovery uses one through five entries; it never fabricates samples or
waits for five before allowing a descriptive view. Each immutable entry keeps
the original sample object, publication identity and Periodic/Control purpose.
Identity is from publication ordering, not command-proof or diagnostic sequence.
Distinct queries with equal monotonic start ticks are allowed; replayed/older
identities, reversed acquisition order, future/stale samples and impossible
speed bytes are rejected.

Each periodic reader owns a window, synchronized by its existing lock. The two
existing accepted-publication paths append: periodic result and validated Control
publication. Appending occurs after existing response/freshness/epoch/ordering
checks. A cached read is never an acquisition. Multiple live recipients have
independent windows; newly created readers do not inherit earlier history.
Broker admission, publication registry and proof-reader behavior remain unchanged.
Moving publication/history ownership into the broker is the later Step 6, where
recipient epochs and lifetime isolation must still be maintained.

`ReadWindowCached()` is internal and descriptive. It schedules no I/O. Its copied,
read-only snapshot contains RawLatest, separate CPU/GPU medians, WindowCount,
LatestAgeMilliseconds and entries. Odd windows use the middle RPM; even windows
use the average of the middle two RPMs (50-RPM steps can occur despite 100-RPM
raw quantization). This is a median statistic, not a new physical measurement,
not a 50-RPM sensor-accuracy claim and not a command level.

RawLatest preserves the original object, acquisition-start monotonic timestamp
and UTC timestamp. LatestAge is computed from the caller's monotonic clock,
never from publication/snapshot time. Historical members can be older than
3000 ms while RawLatest is fresh: they are descriptive history, **not fresh
safety evidence**. Expiry of the newest sample at age >=3000 ms clears the
entire view; there is no fallback to another historical member. An unobserved
freshness gap also clears old history before appending a newly accepted sample.
Negative latest age is invalid. A held snapshot is an observation at its read
time, not a reusable freshness token; future exposure must recheck current age.

Periodic cache failure (including logical timeout/rejected/expired result), raw
cache expiry, Pause and Dispose clear history. Clear keeps the replay watermark.
Existing adapter epochs/order checks reject late lifecycle publications and old
successes after newer failures. A Control waiter cancellation does not invalidate
an otherwise fresh old periodic cache; its abandoned native result never enters
history. This matches the existing raw-cache semantics.

## Contracts kept and files audited

Production changes are restricted to the new window, descriptive accessor,
accepted append hooks, clear hooks and self-test dispatch. Broker, proof reader,
publication, HardwareTelemetryReader, TelemetrySnapshot, SafetyGate, coordinator,
HP backend, watchdog, writes/restores, thermal thresholds, 88F8 route and all
historical qualification checkpoints are unchanged.

ReadCached still returns raw RPM. No production consumer uses the median/view.
Cadence remains 1000 ms, native logical timeout 5000 ms, freshness 3000 ms,
Control total acquisition budget 3000 ms and ACK two real fresh post-command
queries. No scheduling priority/fairness change and no native cancellation claim.
Manual promotion stays as it was; Automatic is still closed.

Changed files:

- `Telemetry/HpWmiFanSampleWindow.cs`: bounded data and median implementation.
- `Telemetry/HpWmiFanSampleWindowSelfTest.cs`: ten new deterministic cases.
- `Telemetry/HpWmiFanTelemetryReader.cs`: one reader-owned window and hooks.
- `Telemetry/HpWmiFanTelemetryReaderSelfTest.cs`: history assertions for late
  completion and suspend rejection.
- `Telemetry/HpWmiFanProofReaderSelfTest.cs`: history assertions for abandoned
  native results, old/future outcomes, lane isolation and post-failure recovery.
- `Program.cs`: window suite in `--fan-wmi-telemetry-self-test`.
- `scripts/test-wmi-fan-telemetry-invariants.ps1`: history boundary and prohibition
  on filtered-history consumption in production proof/policy paths.
- This document: implementation, acceptance and remaining scope.

## Verification requirements

Ten new cases cover bounded rollover and retained timestamps; asymmetric
outlier medians; zero/even median; duplicate/delayed/future/stale/invalid rejection;
the exact 2999/3000-ms newest-age boundary; gaps and clock reversal; immutable
held views; combined real Periodic/Control acquisitions versus repeated cache
reads; failure/recovery; and pause/disposal/replacement isolation.

Strengthened existing cases cover late native completion after caller cancel
or timeout, old publications after newer outcomes/failure, separate native lanes,
periodic expired/timeout completion and actual in-flight suspension barriers.
All 39 WMI cases (window 10, broker 5, diagnostics 4, telemetry 8, proof 12)
must pass in the normal Windows executable self-test. Full strict Windows build,
other safety/backend/coordinator/watchdog/adaptive/profile/UI suites, WMI
invariants in PowerShell 7 and 5.1, and reproducible packaging remain required.
No local executable build is claimed if .NET/PowerShell are unavailable.

## Residual risks and next step

Median can lag a real ramp/stop and never proves correct fan response. Five
samples do not resolve an unavailable/blocked WMI provider. Single-flight native
slot retention and lack of fairness remain Step 3 risks. This change supplies
descriptive history only and does not claim to fix the observed Manual incident;
the physical root cause is still unproven.

Stop after Step 4 software audit. Next: Step 5 explicitly audit command-proof
separation from history with targeted acceptance checks, then Step 6 publication
migration. Exposing metadata to TelemetrySnapshot/GUI and future policy inputs
is Step 7; SafetyGate/freshness integration and later physical tests are separate.
