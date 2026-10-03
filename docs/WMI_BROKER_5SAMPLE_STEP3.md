# WMI broker / 5-sample work — Step 3 native acquisition ownership

Status: implemented, validation pending. No hardware qualification claimed.

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
are unchanged. Both scripts must pass in PowerShell 7 and Windows PowerShell
5.1; full Windows build, self-tests, deterministic packaging and CI are required.

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
