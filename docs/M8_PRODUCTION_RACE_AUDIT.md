# HP 8C40 M8 production-race audit

Target: `HP-8C40-9D0R1LA-F18`.

Status: **CODE/CI PASS. M8B REPRESENTATIVE-LOAD AND M8C THERMAL-PREEMPTION PHYSICAL CONTEXTS ARE CLOSED; PRODUCTION PROMOTION REMAINS A SEPARATE GATE.**

This audit does not add fan authority and does not replace physical M8 evidence. Its purpose is to
prevent the project from repeatedly re-running destructive historical gates when the corresponding
race is already deterministically covered by production code plus synthetic/invariant tests.

## Coverage matrix

| Race boundary | Deterministic code/test evidence | Physical status |
| --- | --- | --- |
| stale SafetyGate result vs newer accepted result | `TestStaleSafetyEvaluationCannotTearDownNewerSessionAsync`, `TestStaleCommandSafetyCannotTearDownNewerSessionAsync` | Code/CI closes this ordering; M8B/M8C now provide the physically closed real under-load context |
| safety preemption vs in-flight `ApplyAsync` | coordinator `TestSafetyPreemptsInFlightCommandAsync` plus M8C `TestThermalPreemptionCancelsInFlightApplyAsync` | M8C PHYSICAL PASS closes the thermal-preemption/restore physical context |
| lifecycle/admission fence vs command dispatch | `TestLifecycleFenceClosesBeforeCoordinatorGateAsync`, `TestCancellationAtPreDispatchPreventsWriteAsync` | M6/M7 already physically closed; M8 does not repeat them solely for history |
| watchdog liveness vs concurrent EC/telemetry failure | `TestControlDependencyFailurePreemptsUnsafeSafetyAsync`, `TestWatchdogLossWinsConcurrentEcFailureAsync` | Historical watchdog failure domains are qualified; M8B representative-load soak is PHYSICAL PASS |
| external override before first write | `TestOwnershipConflictDoesNotClearExternalOverrideAsync`, `TestFirstCommandExternalOverrideIsNoWriteAsync` | no new destructive test required unless implementation changes |
| external override after durable WRITE_INTENT | `TestWatchdogPostIntentExternalRaceAsync`, `TestWatchdogCancellationAfterIntentAbortsAsync` | M5D/M5E establish physical WRITE_ARMED boundaries; M8B load context is PHYSICAL PASS |
| transient guard / torn-read confirmation | `TestStatusToleratesSingleGuardTransientAsync`, `TestStatusRejectsRepeatedGuardConflictAsync` | real M8B/M8C supervision physically observed the same production path |
| restore / journal cleanup ordering | `TestWatchdogCommitFailureRestoresAsync`, `TestWatchdogRestoreIpcFailureDoesNotBlockLocalRestoreAsync`, M5D/M5E invariants | local/independent physical closure was satisfied by the M8B/M8C PASS criteria |

## Audit rule

A race is considered **code/CI covered** only when:

1. the production source still contains the corresponding ordering/fail-closed behavior;
2. a deterministic self-test or invariant exercises that boundary;
3. the test is executed by GitHub Actions;
4. no qualification-only bypass is exposed through the production GUI/runtime.

Code/CI coverage is not a physical PASS. M8B and M8C have now closed the representative-load and thermal-preemption hardware evidence.
This does not itself promote the production watchdog or automatic/adaptive policy.

The complete audit is **CODE/CI PASS** at commit
`ccb11f285a92c6f789929eee6911b5213fd95ddd`, GitHub Actions **#773**
(run `36669038036`). The workflow executed the mapped coordinator/backend/M5D/M5E/M8C
coverage plus PowerShell 7 / Windows PowerShell 5.1 invariants and the warnings-as-errors build.
No additional physical race was discovered by that review, and no hardware execution occurred.

## No-repeat rule

M5D/M5E, M6 and M7 are not repeated only to reproduce historical evidence. A new physical
sub-gate is created only if implementation review or CI exposes a new race whose outcome depends
on real WMI/EC/tach/watchdog timing and cannot be resolved deterministically in synthetic code.

Automatic/adaptive policy remains OFF and `WatchdogRecoveryValidated` remains false even after M8
physical closure. Promotion requires a separate explicit post-M8 gate and same-HEAD CI/evidence review.
