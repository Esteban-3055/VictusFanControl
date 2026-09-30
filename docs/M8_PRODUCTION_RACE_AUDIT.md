# HP 8C40 M8 production-race audit

Target: `HP-8C40-9D0R1LA-F18`.

Status: **CODE/SYNTHETIC COVERAGE MAPPED; CI GATE PREPARED. PHYSICAL UNDER-LOAD CLOSURE REMAINS DEPENDENT ON M8B/M8C.**

This audit does not add fan authority and does not replace physical M8 evidence. Its purpose is to
prevent the project from repeatedly re-running destructive historical gates when the corresponding
race is already deterministically covered by production code plus synthetic/invariant tests.

## Coverage matrix

| Race boundary | Deterministic code/test evidence | Physical status |
| --- | --- | --- |
| stale SafetyGate result vs newer accepted result | `TestStaleSafetyEvaluationCannotTearDownNewerSessionAsync`, `TestStaleCommandSafetyCannotTearDownNewerSessionAsync` | Code/CI can close ordering; M8B/M8C still provide real under-load context |
| safety preemption vs in-flight `ApplyAsync` | coordinator `TestSafetyPreemptsInFlightCommandAsync` plus M8C `TestThermalPreemptionCancelsInFlightApplyAsync` | Thermal physical restore remains M8C |
| lifecycle/admission fence vs command dispatch | `TestLifecycleFenceClosesBeforeCoordinatorGateAsync`, `TestCancellationAtPreDispatchPreventsWriteAsync` | M6/M7 already physically closed; M8 does not repeat them solely for history |
| watchdog liveness vs concurrent EC/telemetry failure | `TestControlDependencyFailurePreemptsUnsafeSafetyAsync`, `TestWatchdogLossWinsConcurrentEcFailureAsync` | Historical watchdog failure domains already qualified; M8B load soak still pending |
| external override before first write | `TestOwnershipConflictDoesNotClearExternalOverrideAsync`, `TestFirstCommandExternalOverrideIsNoWriteAsync` | no new destructive test required unless implementation changes |
| external override after durable WRITE_INTENT | `TestWatchdogPostIntentExternalRaceAsync`, `TestWatchdogCancellationAfterIntentAbortsAsync` | M5D/M5E already establish physical WRITE_ARMED boundaries; M8B load context pending |
| transient guard / torn-read confirmation | `TestStatusToleratesSingleGuardTransientAsync`, `TestStatusRejectsRepeatedGuardConflictAsync` | real M8B/M8C supervision observes the same production path |
| restore / journal cleanup ordering | `TestWatchdogCommitFailureRestoresAsync`, `TestWatchdogRestoreIpcFailureDoesNotBlockLocalRestoreAsync`, M5D/M5E invariants | local/independent physical closure remains required by M8B/M8C PASS criteria |

## Audit rule

A race is considered **code/CI covered** only when:

1. the production source still contains the corresponding ordering/fail-closed behavior;
2. a deterministic self-test or invariant exercises that boundary;
3. the test is executed by GitHub Actions;
4. no qualification-only bypass is exposed through the production GUI/runtime.

Code/CI coverage is not a physical PASS. M8B and M8C remain the only gates allowed to close the
remaining representative-load and thermal-preemption hardware evidence.

## No-repeat rule

M5D/M5E, M6 and M7 are not repeated only to reproduce historical evidence. A new physical
sub-gate is created only if implementation review or CI exposes a new race whose outcome depends
on real WMI/EC/tach/watchdog timing and cannot be resolved deterministically in synthetic code.

Automatic/adaptive policy remains OFF and `WatchdogRecoveryValidated` remains false until M8 is
physically closed.
