# WMI broker / 5-sample work — Step 5 command-proof separation

Status: audit and executable regression additions implemented; exact-head
Windows CI pending. No production runtime behavior change or hardware claim.

Input HEAD: `57b0a82f34dba62f01c187d18e19930b6f6f5ebb`, tree
`a532ca422101d32fa8060b3f078d0296b244a5b3`. Input CI #1231, run
`37110713634`, SUCCESS. Remote ref and CI checked before modifying code.
Branch: `feature/victus-8c40-wmi-broker-5sample`.

## Audited data flow

Production `Hp8C40FanHardware.ReadEcStateAsync` awaits
`HpWmiFanProofReader.ReadFreshAsync`: a newly admitted synchronous native query
under the single-flight broker. The proof sample carries its own query sequence
and original acquisition start. Raw speeds are then combined with narrow EC
setpoint/guard observations; no EC mutex is held while awaiting WMI.

The production pre-write admission and current-speed reads may reuse the fresh
initial baseline (`_lastFanSample`). This reuse is restricted to admission and
does not count as a command response. Every tachometer-ACK iteration uses the
fresh async state path. `WaitForTachometerResponseAsync` rejects pre-command,
repeated and expired identity, checks control state and the target setpoint,
and requires directional/continuity evidence for both fans in **two consecutive
accepted samples**. A nonresponding, repeated or failed snapshot resets the
confirmation count according to existing rules. ACK active-time budget remains
8 s, polling 250 ms; reader queue/native budget and freshness remain 3000 ms.

Only after accepting the new Control query is its raw sample published to live
telemetry recipients; that publication can append descriptive history. The
reverse path does not exist. A five-entry window, median or repeated cache read
cannot supply the new query or its identity. Existing exact-EC diagnostic/fake
1-RPM semantics remain untouched; production 8C40 uses 100-RPM WMI bins.

## New executable tests

Two new proof-reader cases (the second runs five independent outcomes):

1. Fill a five-entry history from real fake-transport acquisitions, then request
   another proof. A sixth native invocation and a new identity must occur.
   New raw RPM must be returned even when the median still shows old speeds.
2. Fill a fresh five-entry window; native IOException, invalid response,
   acquisition expiry, admission timeout and pre-admission cancellation must
   all fail without historical proof fallback. Abandoned admission starts no
   native query; expired history cannot be exposed as fresh.

Three new backend cases connect the real proof reader, broker and telemetry
window to fake EC hardware and the real backend Apply/ACK implementation:

- A favorable five-entry median at dispatch cannot hide a failed new native
  query. Apply fails, emits no ACK event and independent restore reaches FF/FF.
- One real post-command response followed by native failure is not two ACKs;
  the historical median cannot complete it. Restore reaches FF/FF.
- Exactly two real post-command responses can acknowledge even while the
  five-entry median lags. ACK records `samples=2` and raw speeds, not median RPM.

Existing suites retain repeated/pre-command identities, coarse directional
delta guarantees, one-sample spike rejection, EC/ownership loss, suspension,
cancel/timeout native-slot retention, bounded active-time ACK and coordinator
restoration while a native RPM query is still running.

## Files and verification boundary

Changed files:

- `Telemetry/HpWmiFanProofReaderSelfTest.cs`: two new groups above.
- `Hardware/Hp/Hp8C40FanControlBackendSelfTest.cs`: three integrated cases.
- `scripts/test-wmi-fan-control-invariants.ps1`: forbid cache/window/median
  dependencies in production proof reader/backend, require both new test paths
  and preserve exact ACK/polling constants.
- This document: audited flow and evidence boundary.

All production runtime blobs, broker/window/publication, telemetry, SafetyGate,
controller, watchdog, writes/restores, workflow and release gates are unchanged.
No new test harness or hardware authorization. Existing CLI/CI dispatch runs
the extended suites: **41 WMI cases** (window 10, broker 5, diagnostics 4,
telemetry 8, proof 14), plus three extra HP backend cases. Full strict Windows
build, invariants in PowerShell 7/5.1, other regressions and reproducible RC
packaging must pass. Local source review is not a substitute for executable CI.

## Risks and next boundary

No incident root cause is established by these software tests. A blocked native
provider may still retain admission, scheduling has no fairness guarantee and
median history may lag a ramp. No cached data can mask failed command proof.
The pre-write admission cache remains age-limited and requires its separate
existing tests; it is not a rolling-window consumer.

Stop after Step 5 exact-head CI and audit. Step 6 will gradually migrate
`HpWmiFanSamplePublication` into the broker while preserving recipient epochs,
outcome ordering and new-reader isolation. No Step 6 change is included here.
