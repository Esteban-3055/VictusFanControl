# Backend integration milestone — v0.4

This milestone integrates the hardware-validated HP 88F8 write/restore route into the normal application architecture while deliberately leaving the automatic fan policy disabled.

## Step 1 — real HP backend

Implemented `Hp88F8FanControlBackend` behind `IFanControlBackend`.

Properties:

- exact target fingerprint required;
- ordinary commands hard-limited to 14-50;
- no arbitrary EC writes;
- WMI `SetFanLevel` for commands;
- EC 0x34/0x35 for fixed-setpoint acknowledgement;
- dedicated `FF,FF -> LegacyDefault` firmware restore;
- LegacyDefault is attempted even if the FF,FF WMI call reports an error, because HP can apply SetFanLevel despite an error return;
- persistent PawnIO EC session for command acknowledgement instead of reopening PawnIO for every poll;
- existing external fixed override is treated as an ownership conflict and is not cleared during failed admission.

Synthetic backend tests and the previously completed real 30/30 hardware test validate the protocol boundary.

## Step 2 — FanControlCoordinator integration

The GUI now constructs the validated backend and wraps it in `FanControlCoordinator`.

All future policy writes must pass through the coordinator. It owns:

- Firmware / Custom / Restoring / Faulted authority states;
- hard central command limits;
- SafetyGate permission;
- restore on command failure;
- restore on invalid command;
- partial-transition recovery;
- continuous runtime SafetyGate enforcement.

The automatic curve is still OFF, so merely launching the GUI does not acquire custom authority or issue fan-level commands.

## Step 3 — lifecycle integration

Suspend, resume and application exit are connected to the coordinator.

- suspend cancels an in-flight command and restores firmware before the power broadcast handler returns;
- resume closes admission and requires post-resume validated telemetry;
- stale pre-resume SafetyGate results cannot reacquire authority;
- duplicate Windows resume broadcasts do not leave admission permanently blocked;
- normal exit and Windows shutdown dispose the coordinator before telemetry is torn down;
- an in-flight acknowledgement can be cancelled immediately by a safety/lifecycle handoff.

Forced process termination remains a separate hardware-watchdog problem because no managed finally/Dispose path can run after an unconditional process kill.

## Step 4 — dual-tachometer acknowledgement

A command is accepted only when:

1. EC 0x34/0x35 acknowledges the requested setpoints;
2. both fan tachometers remain valid and acknowledge the requested direction;
3. the EC setpoint remains owned throughout acknowledgement.

Direction is derived from the requested level versus HP BIOS current fan level. Material increases/decreases require measurable RPM movement. Near-current commands require valid tachometer continuity. A fan already within 100 RPM of its measured physical ceiling is not required to accelerate further.

If another controller overwrites the EC setpoint, the backend reports ownership loss instead of repeatedly fighting it. The coordinator then hands control back to HP firmware.

## Current gate

The backend/control/safety/lifecycle route is integrated, but the adaptive curve is intentionally not enabled yet.

Still required before automatic control:

- hardware exercise of the integrated coordinator path, including suspend while Custom is active;
- hardware exercise of a deliberately failed tachometer/ownership acknowledgement where practical and safe;
- characterization of forced-process-termination recovery / firmware countdown behavior;
- implementation and tuning of the shared physical-RPM controller and per-fan compensation.
