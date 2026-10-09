# WMI broker / 5-sample work — Step 3 native acquisition ownership

Status: implementation software-validated and audited. No hardware qualification claimed.

Baseline: `05fa77c1fee05650246d68bd529bfb7149b36bad`.
Step 2 input: `0ddf8af069028e6d7fcc32f28cf31e3172bc4f30`, tree
`6494b60f7f144e54417f0603fad2c3de404c799e`, Windows CI #1227
(`37107780024`) SUCCESS.
Branch: `feature/victus-8c40-wmi-broker-5sample`.

## Implemented boundary

`HpWmiFanSampleBroker` is the single owner of native read admission and worker
release. A process-wide production instance survives telemetry/proof reader
recreation. Fake readers share a broker through their original semaphore key;
the same key still identifies diagnostics and publication recipients.

Periodic admission uses `Wait(0)` and skips a busy lane. Control admission awaits
the same lane within the existing total acquisition budget. The broker's lease
transitions atomically from reserved to native-owned to released. Cancellation
before worker transfer can release only a reservation. Once transferred, the
uncanceled native task releases in its own finally, even if the awaiting reader
has timed out, been canceled, paused, disposed or replaced. Failed task
scheduling releases the transferred reservation before rethrowing.

Both readers are adapters: their existing transport, original timestamps,
response validation, backoff, diagnostic outcomes and sample/proof semantics
remain in place. Their admission, native worker scheduling and quiescence now
delegate to the broker. They cannot release native admission themselves.

The broker currently coordinates operations; it deliberately stores no samples.
It does not yet merge clocks, sequences, transports or adapter validation.

## Contracts preserved

- Periodic cadence 1000 ms; fan acquisition expiry 3000 ms; logical timeout
  5000 ms; Control queue plus native await maximum 3000 ms.
- Command ACK remains two real fresh post-command acquisitions with distinct
  query identities and the existing guaranteed directional delta checks.
- `HpWmiFanSamplePublication` remains unchanged: original acquisition metadata,
  recipient snapshots before native dispatch, epoch fences and outcome ordering.
- Diagnostics retain their separate sequence, counters and bounded notices.
- SafetyGate, ownership/EC guards, write/restore/watchdog paths, Manual promotion,
  Automatic gate, historical checkpoints and the 88F8 path are unchanged.
- No EC RPM fallback, OmenMon, native cancellation claim or hardware execution.

## Verification matrix

The existing `--fan-wmi-telemetry-self-test` runs all broker, diagnostics,
telemetry and proof cases. Five new broker cases cover:

1. Shared identity across reader recreation; quiescence cancellation cannot
   release a reservation belonging to another operation.
2. Wrong-broker/reused lease rejection; cancel-before-native is idempotent and
   cannot release a queued/running worker.
3. Native exception releases exactly once and permits a new acquisition.
4. Periodic nonblocking admission, Control waiting behind a real blocked
   periodic query, admission timeout/cancellation, native peak concurrency one
   and a fresh Control query rather than cache reuse.
5. Seven consecutive blocked Control acquisitions with a fake monotonic clock:
   six genuine fresh publications retain identity/original timestamps; the
   seventh holds the lane until real RPM expiry, then its expired result is
   rejected. Periodic contention and expiry remain observable in diagnostics.

Existing telemetry/proof cases continue to cover logical/native timeout and
late completion, waiter cancellation, pause/dispose/recreation publication
fences, invalid responses, freshness boundaries, ordering after failures,
isolated lanes and recovery. Backend/coordinator suites continue to verify
real post-command identity and restoration while a timed-out native read owns
the lane. SafetyGate tests retain the held-snapshot fan expiry boundary.

The two WMI source invariant scripts now follow the relocated ownership:
broker single-lane admission, atomic lease states and worker-finally release.
They additionally forbid direct semaphore admission/release or Task.Run in
the adapters and require the broker tests to run. Historical checkpoint checks
are unchanged. CI explicitly runs both scripts in PowerShell 7 and Windows
PowerShell 5.1; full Windows build, self-tests, deterministic packaging and CI
are required.

## Implementation audit evidence

Implementation commit: `7f83bc7a7b294ae2168e3b3b36ccbfaf51d8060d`.
Implementation tree: `893b68a9f1ce4c94a5ecbdaa0356570fa30476b2`.
Full Windows CI #1228, run `37109531983`: **SUCCESS**, 106 completed steps.
Run: https://github.com/Esteban-3055/VictusFanControl/actions/runs/37109531983

The exact implementation build reported zero warnings and zero errors. Its
WMI executable self-test passed 29 named cases: broker 5, diagnostics 4,
telemetry 8 and proof 12. Safety, coordinator/authority and HP backend suites
also passed, including restoration with a still-running native RPM read.
The WMI source invariants passed in Windows PowerShell 5.1. PowerShell 7
syntax validation passed; explicit PowerShell 7 execution of both WMI invariant
scripts is added by this audit follow-up and requires its own same-head CI.
The follow-up changes only this document and the CI execution entry; production
code and executable tests remain identical to the validated implementation.

The implementation's independently built RC ZIPs matched SHA-256
`e73866c1eeb8d327f3de44346a09f67b49624085f54069c1ef95bfd3e2124a14`.
This identifies the implementation package only, not a later documentation
commit's package. The full run retains its RC and final audit artifacts.

All eight implementation files were reviewed: broker, broker self-test, both
reader adapters, CLI test dispatch, both invariant scripts and this document.
The accumulated changes from `05fa77c` also include Step 1 baseline and Step 2
diagnostics; those diagnostics/publication and all historical qualification
checkpoints are unchanged by Step 3. No local Windows build is claimed: the
evidence above comes from the exact-head Windows Actions job.

## Remaining risks and next boundary

Semaphore scheduling is unchanged and gives no fairness/priority guarantee.
The pressure case demonstrates that Control can prevent periodic native polls;
fresh accepted Control results may sustain telemetry, but stale/invalid/in-flight
results cannot. This step does not claim starvation prevention. A future
Control-over-Periodic scheduler must reserve bounded periodic opportunities and
test sustained demand without weakening the 3000 ms freshness contract.

A stuck synchronous provider can still own the lane indefinitely. Logical
timeout is not physical cancellation. Restore remains independent of RPM
acquisition. Step 3 is a software structural change, not a demonstrated fix for
the observed Manual handoff/timeout; incident root cause remains unproven until
correlated runtime evidence is available.

Stop after Step 3 software audit. Step 4 rolling five-sample history, SafetyGate
integration changes and EC-only unexpected-guard rechecks are separate work.
