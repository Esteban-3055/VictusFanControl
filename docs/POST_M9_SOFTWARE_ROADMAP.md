# Post-M9 software roadmap — P10 to P14

This document defines the software-only work that follows M9 production-watchdog promotion on the exact HP 8C40 target.

Base state for P10 is canonical commit `9b49a57661b683a9ef67be510047d4ca364292f0`, whose canonical GitHub Actions run #1053 completed successfully.

## Non-negotiable boundary

P10-P14 are software preparation only. They must not reopen M9B/M9C/M9D physical gates and must not authorize a new fan write.

The following remain true throughout this software work:

- `control.enabledByDefault=false`
- `automaticPolicyEnabled=false`
- M9C and M9D qualification construction/execution gates are false
- equal-only 10..50
- no level 0/fan-stop
- SafetyGate + HP 8C40 temporal thermal confirmation remain authoritative
- no arbitrary EC writes
- strong restore semantics are unchanged

## P10 — post-M9 consolidation

Synchronize current documentation/profile language with the promoted M9 state and add invariants that distinguish immutable historical evidence from current runtime gates.

## P11 — production policy adapter, hardware gate closed

Join the pure adaptive engine to `FanControlCoordinator` through a narrow adapter. The adapter may execute only when a separate post-M9 authorization says so. It must suppress unchanged commands, reset on loss of firmware/custom state, and restore on policy/safety loss.

## P12 — conservative candidate curve

Define and version a conservative HP 8C40 candidate curve for shadow/replay. It remains `authorizedForProduction=false` and is not called a physically validated production curve.

## P13 — user-facing control surface

Prepare Firmware / Manual / Automatic modes, manual equal-level 10..50 controls, candidate-curve visualization, recommendation/status telemetry and explicit gate state. Startup remains Firmware and no saved setting may bypass the hardware gate.

## P14 — release-candidate productization

Add a software readiness audit, deterministic package build, versioned release-candidate artifacts and final CI coverage. The resulting package is a software RC only.

## P15 — first required target-side checkpoint

P15 is deliberately outside this document's software authorization. The first target-side validation must begin with ordinary startup/control OFF, followed by a separately authorized one-shot manual 30/30 transaction and verified strong restore. Only after that may an automatic-policy physical gate be considered.
