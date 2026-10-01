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

Implemented in software as `AdaptiveFanProductionController`. The adapter joins the pure engine to `FanControlCoordinator`, requires separate Manual/Automatic execution authorization, suppresses unchanged commands, resets across authority changes, and releases to firmware on policy/safety loss. Hardware authorization remains false; CI closure is recorded by the branch workflow rather than by opening a physical gate.

## P12 — conservative candidate curve

Define and version a conservative HP 8C40 candidate curve for shadow/replay. It remains `authorizedForProduction=false` and is not called a physically validated production curve.

## P13 — user-facing control surface

Prepare Firmware / Manual / Automatic modes, manual equal-level 10..50 controls, candidate-curve visualization, recommendation/status telemetry and explicit gate state. Startup remains Firmware and no saved setting may bypass the hardware gate.

### P13.1 — presentation-only mode surface

Closed in CI. The normal GUI exposed the intended Firmware / Manual / Automatic model with both execution gates compile-time false.

### P13.2 — mode selector wired through the production adapter

Closed in CI. Firmware / Manual / Automatic mode requests route only through `AdaptiveFanProductionController.SetModeAsync`; both execution gates remain closed.

### P13.3 — Manual equal-level control behind the closed gate

Closed in CI. The equal CPU/GPU selector is constrained to 10..50, routes only through the production adapter after the Manual gate, and persists only the numeric UI preference.

### P13.4 — live Automatic shadow preview and candidate visualization

Closed in CI. Each telemetry snapshot is evaluated by the no-write shadow evaluator; all six Candidate V1 curves and recommendation state are visible while Automatic execution remains absent/closed.

### P13.5 — tray/status hardening and software completion

Implemented: the tray now reports runtime state, CPU/GPU, fan authority, requested P13 mode, adaptive shadow recommendation and both post-M9 gate states. The control surface tracks actual coordinator authority instead of inferring it from the requested mode. P13 is software-complete with Manual and Automatic execution still compile-time false; no saved setting can restore a mode or authority at startup.

## P14 — release-candidate productization

Add a software readiness audit, deterministic package build, versioned release-candidate artifacts and final CI coverage. The resulting package is a software RC only.

### P14.1 — software readiness baseline

Implemented: the final P13 HEAD/CI is pinned as the P14 baseline, a machine-readable release contract is present, and CI now fails if any Manual/Automatic/default-policy/M9 qualification boundary is reopened. No publish package or release artifact is produced in this step.

### P14.2 — versioned win-x64 publish layout

Closed in CI: version `0.4.0-rc.1` is centralized, GUI and production watchdog publish into a fixed framework-dependent `win-x64` app/watchdog layout, qualification-only standalone executables are excluded, and CI #1076 built and verified the actual publish output. PawnIO modules and the final archive/hash manifest remain deferred to P14.3.

### P14.3 — deterministic package and SHA-256 manifest

Implemented pending CI closure: the pinned PawnIO.Modules 0.2.11 archive is hash-verified, only the required module binaries enter the package, every payload file is SHA-256 manifested, and the RC ZIP is produced with normalized ordering/timestamps. CI performs a two-build byte-for-byte ZIP reproducibility check. Artifact retention remains P14.4.

## P15 — first required target-side checkpoint

P15 is deliberately outside this document's software authorization. The first target-side validation must begin with ordinary startup/control OFF, followed by a separately authorized one-shot manual 30/30 transaction and verified strong restore. Only after that may an automatic-policy physical gate be considered.


### P13 formal software closure

P13 software implementation passed the full branch workflow at source HEAD `4f48d1ba68cccfc931f116793672d70efeb8ba52`, GitHub Actions #1069 (run ID `36892688980`), result **SUCCESS**. The closure records software readiness only: Manual and Automatic target-side execution remain closed, Candidate V1 remains unvalidated, and P15 remains the first hardware checkpoint.
