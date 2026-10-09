# Integrated coordinator hardware gate

This is the next bounded hardware validation after the standalone 30/30 write test.

It exercises the normal production control route without enabling any automatic fan policy:

```text
live telemetry
    |
    v
SafetyGate
    |
    v
FanControlCoordinator
    |
    v
Hp88F8FanControlBackend
    |
    +-- HP BIOS/WMI SetFanLevel(30,30)
    +-- EC 0x34/0x35 ownership acknowledgement
    +-- CPU + GPU tachometer acknowledgement
    `-- FF,FF -> LegacyDefault restore
```

## Purpose

The standalone `test-first-fan-write.ps1` validated the HP 88F8 protocol directly on real hardware. Version 0.4 then placed that validated route behind `IFanControlBackend` and `FanControlCoordinator`.

CI verifies the coordinator/backend contract with synthetic hardware, but cannot prove the complete production route on the physical Victus. This gate is specifically for that missing validation.

## Safety boundaries

- fixed target: CPU 30 / GPU 30;
- exact validated HP 88F8 fingerprint is required;
- automatic policy remains OFF;
- light-load CPU/GPU temperature and power envelope is enforced;
- normal SafetyGate freshness, plausibility and thermal limits remain active;
- production backend requires EC 0x34/0x35 plus both fan tachometers;
- six post-ACK samples exercise continuous coordinator ownership/feedback supervision;
- custom authority is bounded to 20 seconds;
- a scheduling gap over 3 seconds aborts;
- Ctrl+C requests the managed coordinator restore path;
- forced process termination is out of scope until the firmware countdown/watchdog is characterized.

OMEN Gaming Hub may stay open for undervolt coexistence checking. OmenMon, OmenMon-Reborn and the VictusFanControl GUI must be closed.

## First-command ownership race

A review before this gate identified a specific race:

1. `EnterCustomModeAsync` verifies FF/FF but performs no hardware write.
2. Another controller can theoretically acquire a fixed setpoint before our first `SetFanLevel`.
3. A generic restore at that point would incorrectly send FF,FF and could clear the external state.

The backend now classifies a valid first command that fails before the first `SetFanLevel` attempt as a no-write `FanControlAdmissionException`. It drops its logical custom reservation. The coordinator returns to Firmware without sending FF,FF.

Synthetic backend and coordinator tests cover this race.

## Running

From the repository root on the target Victus:

```powershell
git pull
.\scripts\test-integrated-coordinator.ps1
```

The wrapper builds with warnings-as-errors, runs the synthetic regressions, performs read-only live probes, asks for undervolt confirmation, and finally requires `COORD30` before the hardware write.

A valid successful run must end with:

```text
PASS: SafetyGate -> FanControlCoordinator -> real HP backend -> EC/tach ACK -> HP restore.
```

EC 0x34/0x35 must also return to FF/FF. Do not mark this gate complete until the target-machine console output has been reviewed.
