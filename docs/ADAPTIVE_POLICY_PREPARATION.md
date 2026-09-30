# Adaptive fan policy preparation

Target scope: architecture only. **PURE ENGINE CODE/CI PASS; production integration is disabled.**

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
`automaticPolicyEnabled=false` and `WatchdogRecoveryValidated=false`.

Hardware integration remains blocked until M8 is physically closed.

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
