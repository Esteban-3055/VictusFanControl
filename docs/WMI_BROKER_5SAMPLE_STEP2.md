# WMI broker / 5-sample work — Step 2 acquisition instrumentation

Status: **implemented on the dedicated work branch; control/safety policy intentionally unchanged**

Baseline: `docs/WMI_BROKER_5SAMPLE_BASELINE.md`

## Purpose

Before introducing a shared broker or a five-sample window, the existing periodic and control WMI readers are instrumented so a future failure can distinguish:

- periodic polling that could not acquire the shared native slot;
- control time spent waiting for that slot;
- native WMI duration;
- total request duration;
- logical periodic timeout;
- control waiter timeout/cancellation;
- invalid/expired responses;
- a native call that finishes only after its caller stopped waiting;
- recovery by a later accepted acquisition.

This step is observational. It does not relax freshness, command proof, ownership or restore behavior.

## Correlation model

Instrumentation state is keyed by the existing shared `SemaphoreSlim`. Production periodic and control readers therefore report into the same diagnostic stream without changing who owns or releases the native slot.

Each request attempt receives a diagnostics-only monotonically increasing sequence. This sequence is separate from:

- `HpWmiFanProofSample.Sequence`, which remains the command-proof identity used by control;
- `HpWmiFanSamplePublication` ordering, which remains the periodic-cache publication ordering.

No control or safety decision consumes the diagnostics-only sequence.

## Metrics

The backend diagnostics surface reports the latest acquisition with:

- purpose: `Periodic` or `Control`;
- outcome;
- admission queue wait;
- native duration;
- total duration;
- aggregate request/native-start counters;
- periodic admission-busy count;
- admission/logical/waiter timeout counters;
- cancellation count;
- native failures;
- response rejects;
- expired results;
- acquisitions whose native call lasted at least 1000 ms.

Normal successful acquisitions are visible in the diagnostics panel but are not appended to the persistent event log.

The event log receives bounded notices only for slow acquisitions, timeouts/failures, relevant cancellation state, and recovery after an observed acquisition fault. The diagnostics queue is capped at 32 entries and cannot grow without bound.

## Preserved invariants

Step 2 intentionally preserves the frozen baseline:

- periodic poll interval remains 1000 ms;
- WMI fan freshness remains 3000 ms;
- periodic logical timeout remains 5000 ms;
- control proof maximum wait remains 3000 ms;
- command-response confirmation remains two fresh post-command samples;
- native WMI access remains single-flight;
- a logical timeout/cancellation never releases a still-running synchronous native call;
- successful control publication keeps the original acquisition timestamp;
- no periodic EC fallback is introduced;
- SafetyGate, ownership, watchdog and firmware restore policy are unchanged.

## Software verification

The existing `--fan-wmi-telemetry-self-test` now also runs deterministic acquisition-instrumentation cases. They verify:

1. periodic native timing and slow-read reporting;
2. periodic admission contention without native overlap;
3. control queue wait versus native duration correlation;
4. control waiter timeout remaining distinct from a native call that completes later.

All tests use fake transport/semaphores/clocks and perform no hardware access.
