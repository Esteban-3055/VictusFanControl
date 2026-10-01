# Post-M9 roadmap — P10 to P15

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

Closed in CI #1078: the pinned PawnIO.Modules 0.2.11 archive was hash-verified, only the required module binaries entered the package, every payload file was SHA-256 manifested, and two independent builds produced identical ZIP SHA-256 `603ac7b2ca6816fe00002598410983ba031529ec168a70c5ec59041e5a71756c`. Artifact retention remains P14.4.

### P14.4 — retained CI artifact

Closed from implementation HEAD `1c7ee997b5bbac02eb89e1c7d3f91e7130828c1d` after GitHub Actions #1080 SUCCESS. Artifact ID `11186116708` was retained for 30 days, downloaded and independently verified as an exact three-file set (RC ZIP + `.sha256` + external attestation). GitHub wrapper digest and downloaded-wrapper SHA-256 both equal `eb42b9c7d8a5d29e7c30a4f35f19f7e07fecacb83dee26aaf638093cbf2826b2`. P14.5 remains the final software RC audit; no hardware gate was opened.

### P14.5 — final software RC audit

Closed from audited source HEAD `eebcdd5e833256466c1ae023c35f7cef8d40d6ec` after GitHub Actions #1084 SUCCESS. The retained RC artifact and separate final-audit artifact were both downloaded and independently verified; the RC manifest rehashed 61/61 payload files and the audit JSON recorded PASS with `p15NotExecuted=true` and `hardwareExecution=false`. Failed CI #1083 is retained as history for the corrected upload-artifact digest-format assumption. P14 software productization is closed; P15 remains unopened.

## P15 — first required target-side checkpoint

P15A startup/no-write and P15B one-shot production Manual 30/30 are both physically passed, independently evidence-reviewed, formally closed and re-blocked. P15B closure HEAD `4493f135b474f0a17ad0737ed9ebde8362e0eb47` passed GitHub Actions #1116 SUCCESS after preserving the software-only #1115 metadata-precision failure.

P15C now prepares the next narrow boundary: qualification of the **real P13 GUI Manual path**. Preparation adds a dedicated, default-false qualification-only GUI gate and exact process/token mode while keeping `Hp8C40PostM9UserControlGate.ManualExecutionAuthorized=false`, Automatic closed, Candidate V1 unvalidated/unauthorized, and M9C/M9D qualification gates closed. A future separately authorized run will require the real Manual button, exactly one real Apply 30/30, independent parent OWNED proof, the real Firmware button and production strong restore. No P15C physical execution is authorized during preparation. Automatic remains later and independent.


### P13 formal software closure

P13 software implementation passed the full branch workflow at source HEAD `4f48d1ba68cccfc931f116793672d70efeb8ba52`, GitHub Actions #1069 (run ID `36892688980`), result **SUCCESS**. The closure records software readiness only: Manual and Automatic target-side execution remain closed, Candidate V1 remains unvalidated, and P15 remains the first hardware checkpoint.
