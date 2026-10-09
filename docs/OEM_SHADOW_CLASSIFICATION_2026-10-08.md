# OEM shadow: observed classification correction, 2026-10-08

The one-hour 8C40/F.18 read-only capture completed with 3542 frames, 1769 distinct
fresh fan acquisitions, no terminal failures, no clock discontinuities and no
frame gaps above five seconds. Required thermal sources were missing only on the
first frame. Original source revision: `4c8049c1e74837a23896f300c885e1872fc85457`.
The original archive hash and preserved input hashes are in the live fixture
manifest. No machine paths or host identifiers are included in the fixture.

## Problem and corrected behavior

Previously, any valid pair outside the A-D seed ranges was labeled Transition.
The live session included roughly five and a half minutes near 46-47/40-41 fan
levels (nominal 4600-4700/4000-4100 RPM). The old label implied a ramp without
evidence of movement. The range table is a hypothesis, not an OEM specification.

The classifier now reports **Unmapped** for these pairs. Transition refers to
the debounce between recognized ranges, or the predictor's declared ramp.
A separate causal tracker reports an **UnmappedStableCandidate** after at least
20 seconds and six distinct fresh acquisitions within a full envelope spanning
at most two levels on each fan. This envelope is anchored to the first acquisition;
it does not slide along a slow ramp. Exceeding either span, missing/stale/future
fan data, regressing source epochs or a gap above five seconds resets evidence.
Cached frames never advance it. Qualification is not backdated in the duration
metrics. Candidate records retain the first acquisition epoch and final envelope.

Candidates are descriptive observations, not newly qualified OEM states. The
known-range tolerance remains plus/minus one level. A real regime that overlaps
a tolerance boundary can alternate between mapped and unmapped labels; this
iteration does not claim that the full firmware state machine has been recovered.

## Accounting and transition semantics

Summary schema 2 adds Unmapped as the last enum/matrix entry, explicit A-D coverage,
regime frame/acquisition counts and durations, and exact comparisons of both raw
fan levels against prediction ranges. CSV appends regime evidence columns; JSONL
adds `actual.Regime`. Consumers must read the named matrix axes, not assume 6x6.

Unmapped breaks direct A-D transition scoring: an unknown intermediate path cannot
be scored as a demonstrated direct transition. Known endpoint changes across
Unmapped observations are retained separately in
`transitionMetrics.endpointChangesAcrossUnmapped`, rather than being erased.
Gaps and Unknown break those endpoint chains as well. Consequently new transition
counts cannot be compared directly with old counts that crossed unmodeled levels.

No thermal thresholds, persistence rules, hysteresis rules, fan write paths, GUI,
Performance Guardian or production policy were changed.

## Replayed evidence

| Capture | Frames | Actual A-D coverage | A-D accuracy | Unmapped frames | Exact raw pairs above either predicted upper bound |
| --- | ---: | ---: | ---: | ---: | ---: |
| v10 | 6049 | 86.01% | 24.45% | 516 | 1849 / 3284 (56.30%) |
| v11 | 1976 | 98.28% | 86.77% | 0 | 247 / 1929 (12.80%) |
| live 2026-10-08 | 3542 | 69.51% | 81.64% | 879 | 1543 / 3527 (43.75%) |

The live A-D accuracy increase from 78.44% is **not a predictor improvement**:
classification/debounce and its denominator changed. Every thermal prediction
in all 11567 frames is unchanged from the original source revision. Reviewed
baseline hashes enforce this in the independent checker and CI.

The live trace produces six stable candidate segments, with envelopes including
31-32/26-27, 35-36/32-33, 46-47/40-41 and 35-36/31-33. These are observations
to investigate jointly with v10/v11, not an expanded control table. The high
candidate contains 164 distinct acquisitions. Total time classified Unmapped
is 894.33 seconds; time already confirmed as a stable candidate is 385.21 seconds.
CPU load remains absent in this trace; no substitute or fabricated load is used.

The seed's CPU up condition held continuously for at most about 18 seconds against
a 45-second requirement. GPU >=75 C and DTT3 >=67 C never coincided despite higher
actual fan levels. Sensor conditions and temporal assumptions need investigation
after the observed-state definitions are reviewed; shortening timers alone is
not established as a solution.

## Verification and next development step

Local Roslyn compilation against .NET 8 reference assemblies passed with warnings
as errors. The 408 self-test checks cover cached epochs, drift, gaps, invalid timing,
return debounce, raw-range misses outside A-D and retained endpoint changes.
All three replays pass an independent Python reconciliation of hashes, input
epochs, freshness, matrix support, coverage, intervals, raw-range comparisons and
transition chains. Local SDK CLI startup is restricted by process-information
access in the executor; the Windows/Ubuntu workflow supplies the SDK build check.

CI replays all three fixtures and Windows also builds/publishes the read-only
capturer and runs its fake hardware/launcher tests. CI status belongs to the
actual Actions run for the published commit; this document is not a claim that
an as-yet-unexecuted run passed.

**NO-GO for fan control remains.** Next: investigate range-boundary overlap and
candidate regimes across independent sessions, then compare causal thermal
hypotheses on held-out sessions using both coverage and raw upper-bound misses.
Avoid tuning to v11 alone. No additional physical capture is needed to reproduce
the problems already represented by these fixtures.
