# Adaptive fan policy preparation

Target scope: architecture + offline shadow planning/replay. **Production integration is disabled.**

This preparation intentionally stops before the policy can command hardware. The
`AdaptiveFanPolicyEngine` is a pure telemetry-to-equal-level state machine with no reference to
the HP backend, coordinator, watchdog, WMI, EC, GUI or lifecycle authority.

## Implemented behavior

The engine:

- produces one equal CPU/GPU fan level;
- accepts configurable monotonic piecewise-linear curves for CPU temperature, GPU temperature,
  CPU package power, GPU power, CPU load and GPU load;
- chooses the maximum demand across those domains;
- clamps every result to the configured fan envelope;
- limits upward and downward slew independently;
- requires configurable consecutive evidence before reducing fan level;
- applies a configurable decrease deadband;
- refuses duplicate/out-of-order telemetry;
- refuses telemetry continuity gaps;
- refuses non-finite or implausible telemetry;
- resets state explicitly when lifecycle/control ownership is abandoned.

The policy has no baked-in production curve. Self-tests use synthetic curves only to prove
interpolation and state-machine behavior. Final HP 8C40 curve values remain a later tuning task.

## Offline shadow/replay preparation

A second hardware-independent layer is prepared around the pure engine so future tuning can be
done against recorded telemetry before any automatic control is authorized.

The shadow path combines:

- the production `SafetyGate` evaluated with `fanWritePathPresent:false`;
- the exact HP 8C40 temporal CPU thermal-confirmation semantics;
- the pure adaptive policy engine;
- a pure notional intent planner that emits only `EnterCustomAndApply`,
  `ApplyChangedLevel`, `HoldCustom`, `ReleaseToFirmware` or `HoldFirmware`;
- an offline replay reader for the existing VictusFanControl telemetry CSV format.

The replay never constructs `HardwareTelemetryReader`, `FanControlCoordinator`,
`Hp8C40FanControlBackend`, a watchdog lease, WMI or a PawnIO write session. Its output is a CSV
of recommendations and **notional** control intents only; hardware writes are structurally absent.

Replay configuration is deliberately external and strict. A JSON document must identify
`HP-8C40-9D0R1LA-F18`, declare `purpose="shadow-only"`, explicitly set
`authorizedForProduction=false`, remain inside the physically validated equal-only 10..50
envelope and pass the same engine validation used by code. The checked-in
`profiles/HP-8C40.adaptive-shadow.example.json` is synthetic/illustrative and is **not** a
production fan curve.

This offline shadow replay does not authorize production integration. It exists to let future
recorded workloads be compared against candidate curves while the M8B/M8C physical gates were still pending; those gates are now closed, but
production integration remains separately blocked.

## Shadow/replay code and CI closure

The offline shadow/replay layer is **CODE/CI PASS** at commit
`0c547f8e80ede4b014adbc12b2d4ae07d1c8cf3c`, GitHub Actions **#791**
(run `36675569560`). The workflow passed the PowerShell 7 and Windows PowerShell 5.1
isolation invariants, warnings-as-errors build, deterministic pure-engine tests, notional-intent
tests, exact-target SafetyGate/thermal-confirmation shadow tests, CSV parser/replay tests and the
existing M5-M8/coordinator/backend regressions.

No hardware fan write, watchdog lease, production backend construction or GUI integration was
added. Automatic/adaptive policy remains OFF and this code/CI closure does not promote M8B or
M8C physical status.

## Safety boundary

This work does **not**:

- instantiate `FanControlCoordinator`;
- instantiate `Hp8C40FanControlBackend`;
- issue `SetFanLevel`;
- access PawnIO EC/MSR fan control;
- acquire a watchdog lease;
- expose policy controls in the GUI;
- enable automatic/adaptive control.

The production profile remains `enabledByDefault=false`,
`automaticPolicyEnabled=false`; after formal M9 promotion, `WatchdogRecoveryValidated=true` while user/adaptive control remains separately blocked.

The historical M8-era watchdog/race blocker is now closed by M9 promotion. Adaptive hardware execution remains blocked by a separate post-M9 policy-integration and target-side validation gate.

Historical M8 invariant text retained for provenance: `Hardware integration remains blocked after M8 physical closure` described the pre-M9 state and is superseded by the current paragraph above. Offline shadow replay may
evaluate recorded telemetry, but it cannot acquire fan authority or execute its notional intents.

## Code/CI closure

The hardware-independent engine and its isolation boundary are **CODE/CI PASS** at commit
`ccb11f285a92c6f789929eee6911b5213fd95ddd`, GitHub Actions **#773**
(run `36669038036`). The workflow passed:

- PowerShell syntax and the adaptive-policy isolation invariant under PowerShell 7;
- the same isolation invariant in the Windows PowerShell 5.1 compatibility block;
- warnings-as-errors build;
- deterministic interpolation, maximum-demand selection, bounded upward/downward slew,
  decrease confirmation/deadband, 10..50 envelope, duplicate/out-of-order refusal,
  telemetry-gap refusal and invalid-input/config rejection;
- the existing M5-M8, SafetyGate, coordinator, BIOS and HP-backend regressions.

No production curve has been defined or validated. The engine remains disconnected from the
GUI, coordinator and HP backend; automatic/adaptive policy remains OFF.


## Current post-M9 status

The pure engine and offline shadow/replay layers remain valid. M9 has promoted the exact-target production watchdog path, but this does not authorize adaptive control. P10-P14 may connect and productize the software path only while all post-M9 manual/automatic hardware execution gates remain closed. See `POST_M9_SOFTWARE_ROADMAP.md`.


## P11 production adapter

A narrow `AdaptiveFanProductionController` now connects policy intent to `FanControlCoordinator` in software. It has no HP backend, WMI, EC, PawnIO or watchdog dependency of its own. Manual and Automatic execution each require an explicit constructor authorization; the repository profile records hardware authorization as false. The adapter emits equal-only commands, suppresses unchanged targets, and releases Custom authority when effective SafetyGate admission is lost. Deterministic fake-backend tests exercise the closed-gate, manual, automatic, no-retransmit and safety-release paths. This is software integration only and does not authorize a target-side fan write.
