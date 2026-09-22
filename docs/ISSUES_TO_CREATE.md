# Initial GitHub issues

These are the first issues to create after the repository is online.

## 1. Validate v0.1 telemetry on HP 88F8

**Goal:** verify CPU package temperature, CPU package power, CPU total load, NVIDIA GPU temperature/power/load and sensor naming on the development machine.

**Acceptance criteria:**
- no fan/BIOS/EC writes
- 15-minute idle CSV captured
- sensor inventory attached after removing private information

## 2. Add read-only 88F8 fan RPM telemetry

**Goal:** read CPU/GPU fan tachometers without controlling the fans.

Known candidates from the investigation:
- CPU tachometer: `0xB0` LE16
- GPU tachometer: `0xB2` LE16

**Safety:** read-only implementation only.

## 3. Characterize HP Auto behavior

Collect synchronized CPU/GPU temperature, power, utilization and fan RPM under idle, browser, video, CPU bursts and gaming. Determine whether fan response correlates with power/load before temperature rises.

## 4. Implement state estimator

Add temperature smoothing, `dT/dt`, power moving averages and workload persistence.

## 5. Implement safety supervisor

No fan writes until this issue is complete. Add board allowlist, sensor plausibility, thermal override, RPM response validation and firmware fallback state machine.

## 6. Add experimental 88F8 BIOS fan backend

Manual-test-only control using the already validated HP BIOS fan-level path. Hard-clamp to the board profile.

## 7. Implement adaptive controller

Use power feed-forward + thermal feedback + fast fan-up + delayed hysteretic fan-down.
