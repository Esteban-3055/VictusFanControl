# Adaptive fan policy preparation

Target scope: architecture only. **Production integration is disabled.**

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
