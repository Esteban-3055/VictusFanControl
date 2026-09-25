# Safety design

## Current version

Version 0.4 integrates the hardware-validated HP 88F8 backend behind `FanControlCoordinator`. The automatic fan policy remains **OFF**.

The existence of a write-capable backend is intentionally separate from authority: a policy can write only after SafetyGate permits it and the coordinator grants Custom authority.

## Safety layers

The current implementation checks:

- exact HP target fingerprint and expected RTX 3060 identity;
- runtime state must be `Healthy`;
- complete, fresh and plausible telemetry;
- conservative CPU/GPU thermal handoff thresholds;
- central 14-50 command bounds independent of backend-advertised capabilities;
- backend-local 14-50 validation;
- no pre-existing fixed EC setpoint during authority acquisition;
- EC 0x34/0x35 acknowledgement after a write;
- both physical tachometers acknowledge the requested direction;
- EC ownership remains unchanged while acknowledgement is in progress;
- verified `FF,FF -> LegacyDefault` restoration.

The coordinator continuously enforces the latest SafetyGate result. Safety loss cancels an in-flight acknowledgement before waiting for coordinator serialization, then restores HP authority through a non-cancellable fail-safe path.

## Lifecycle safety

Suspend/resume establishes a freshness boundary.

- suspend closes admission, cancels any in-flight command and returns authority to HP;
- resume keeps admission closed;
- only telemetry sampled after the lifecycle boundary can reopen admission;
- duplicate Windows resume broadcasts are coalesced;
- stale SafetyGate results cannot reacquire Custom authority;
- normal application exit and Windows shutdown dispose/restore the fan coordinator before telemetry is torn down.

Forced process termination is fundamentally different: Windows cannot run managed cleanup after an unconditional kill. Real-hardware characterization showed that EC 0x63 is **not** an independent crash fail-safe in the validated OMEN Gaming Hub coexistence configuration: after the VFC GUI was killed with 30/30 active, the fixed setpoint remained 30/30 while an external HP/OMEN-side component refreshed the countdown twice. Gate B has now physically proven that an independent LocalSystem Session 0 service can restore that orphaned 30/30 state through the validated `FF,FF -> LegacyDefault` path and independently verify FF/FF. Unattended automatic control still requires the lease/journal/IPC layer that tells this service when that restore authority is legitimately VFC-owned.

## Ownership / coexistence

An existing fixed EC setpoint is treated as an ownership conflict during admission. Because no write has occurred at that point, the coordinator does **not** send `FF,FF` merely to clear another controller.

After VictusFanControl owns a setpoint, any external overwrite is treated as ownership loss. VictusFanControl does not continuously fight it; the command fails and the coordinator returns control to HP.

OMEN Gaming Hub was kept open during the real bounded 30/30 test and the configured CPU undervolt remained unchanged. EC manual/countdown fields are deliberately not written by VictusFanControl.

## Fan-command tachometer acknowledgement

A normal command succeeds only after two independent layers agree:

1. EC 0x34/0x35 holds the requested fixed setpoints.
2. Both fan tachometers acknowledge the requested direction within a bounded timeout.

Direction is inferred from the requested level versus HP BIOS current fan level. Material increases/decreases require a measurable RPM change; near-current requests require valid non-zero tachometer continuity. A fan already within 100 RPM of its measured physical ceiling is not required to accelerate further.

The physical ceilings are independent: approximately 4330 RPM CPU and 4670 RPM GPU on the development target.

## Remaining blockers before unattended automatic control

- Gate C-G crash-watchdog lease/journal/IPC implementation and physical validation;
- load/gaming and thermal-emergency validation;
- level-14 restart-from-rest validation;
- implementation and tuning of the shared-RPM adaptive policy.

Unknown or ambiguous state remains fail-closed. See `PRE_CONTROL_CHECKLIST.md`.
