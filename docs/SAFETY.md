# Safety design

## Current version

The v0.3 normal GUI/controller path is still read-only and uses `DisabledFanControlBackend`. HP firmware owns both fans during ordinary application use.

The repository also contains two **explicit validation-only** BIOS/WMI operations:

- `FanMode=LegacyDefault` restore test;
- a fixed `FanLevel=30,30` first-write harness with a short bounded control window.

These commands are not exposed through the normal GUI or adaptive controller.

## Implemented pre-control protections

The project has a central safety gate that evaluates:

- exact validated target fingerprint, not Product ID alone;
- runtime state must be `Healthy`;
- every required telemetry field must be present;
- telemetry must be fresh;
- the NVML device identity must match the validated RTX 3060 target;
- sensor values must pass plausibility checks;
- conservative thermal handoff thresholds;
- explicit presence of a reviewed write/restore path before custom authority can be granted.

Additional protections include:

- independent telemetry freshness watchdog;
- suspend/resume epoch invalidation so reads from an old power cycle are discarded;
- duplicate resume-event coalescing;
- bounded EC retries with graduated backoff;
- coherent repeated reads for 16-bit fan tachometers;
- board/profile hard command range 14-50;
- central authority coordinator;
- forced firmware-restore attempt after uncertain/partial custom-entry failures;
- exact WMI and EC acknowledgement in the bounded first-write harness;
- continuous light-load envelope and periodic ownership checks during that harness.

## Required invariants before normal custom control

1. Control is enabled only on a validated profile.
2. Commands remain inside board-specific hard limits.
3. Missing, stale, implausible or wrong-device telemetry immediately removes custom-control permission.
4. High temperature overrides acoustic policy and hands authority to the validated safety path.
5. Both tachometers must acknowledge commands within bounded time.
6. Failures, exit and suspend must restore firmware authority when execution is still available.
7. Crash/forced-termination safety must be provided by independently validated firmware watchdog/countdown behavior; a process `finally` block is not sufficient.
8. Fan-up is fast; fan-down is slow/hysteretic.
9. No arbitrary direct EC writes are allowed in the normal controller.
10. A second active fan controller is not allowed unless coexistence is explicitly validated.
11. Any unknown state fails closed.

## Current blockers

Before integrating the real HP backend into `IFanControlBackend` or enabling a curve in the GUI:

- repeat the telemetry soak because the EC read/backoff/coherence implementation changed;
- repeat suspend/resume tests because the lifecycle epoch logic changed;
- validate our independent LegacyDefault call on the target;
- validate the bounded `30,30` test and OMEN Gaming Hub undervolt coexistence;
- characterize the 88F8 countdown/watchdog, including forced process termination;
- validate RPM-response thresholds and thermal emergency behavior under load.

See `PRE_CONTROL_CHECKLIST.md`.


## Fan-command tachometer acknowledgement model

The production HP 88F8 backend now treats a WMI write as successful only after two independent layers agree:

1. EC 0x34/0x35 must hold the requested fixed setpoints.
2. Both physical tachometers must acknowledge the requested direction within a bounded timeout.

The expected direction is derived from the requested level versus HP BIOS GetFanLevel's current-speed level. A materially higher request must produce a measurable RPM rise; a materially lower request must produce a measurable RPM fall. Near-current requests require valid non-zero tachometer continuity. The known physical fan ceilings are handled explicitly so a fan already at its measured ceiling is not required to accelerate further.

An external setpoint overwrite is treated as ownership loss and causes the command to fail rather than repeatedly fighting another controller. FanControlCoordinator then restores HP firmware authority.


## Runtime safety preemption

SafetyGate is now enforced continuously by the GUI runtime, not only when a new command is dispatched. If runtime state or telemetry stops permitting custom control while a backend command is still waiting for acknowledgement, FanControlCoordinator cancels that in-flight operation before waiting on its serialization gate. The command failure path then restores HP firmware authority with a non-cancellable fail-safe restore.

Suspend/resume uses the same mechanism. Resume admission remains blocked until post-resume telemetry has been revalidated, and stale SafetyGate results from before the lifecycle boundary cannot reacquire authority.

A read-only authority-acquisition conflict (for example, detecting a fixed setpoint already owned by another controller) is distinguished from an uncertain partial write. The coordinator does not send FF,FF merely because another controller was detected during admission.
