# Safety design

## Current version

v0.1 is telemetry-only. It contains no fan write path.

## Required invariants before any control release

A future control-capable build must satisfy all of the following before fan writes can be enabled by default.

1. **Board allowlist**
   - Control is disabled unless the detected HP Product ID has a validated profile.
   - Initial control target: `88F8` only.

2. **Hard command limits**
   - For the tested 88F8 profile, experimental level requests are clamped to a validated range.
   - Current measured range candidate: 14 through 50.
   - The limits must remain configurable per board, never globally assumed.

3. **Sensor plausibility**
   - Invalid, missing or frozen temperatures cause immediate control abort.
   - Missing CPU telemetry is always fatal to custom control.
   - GPU telemetry loss must have a conservative fallback.

4. **Thermal override**
   - High temperature overrides acoustic targets.
   - Critical temperature must transition to a firmware-safe/high-cooling state, not continue the adaptive policy.

5. **RPM feedback**
   - After a fan command, measured RPM must move toward an expected range within a bounded time.
   - Repeated non-response causes custom control to abort.

6. **Watchdog / firmware recovery**
   - The system must not depend on an infinite stream of custom commands to remain safe.
   - On crash, telemetry failure, unhandled exception or explicit exit, HP firmware control must be restored or allowed to recover automatically.

7. **Fast up, slow down**
   - Fan-up reacts quickly to rising thermal input.
   - Fan-down requires hysteresis and a stable low-load interval.

8. **No blind EC writes**
   - Direct EC writes are forbidden until the exact 88F8 behavior is independently validated.
   - Prefer a known working HP BIOS/WMI fan-level interface when possible.

9. **Single controller ownership**
   - Do not run HP Gaming Hub fan control and a custom write-capable controller simultaneously unless coexistence is explicitly tested.

10. **Fail closed**
    - Any unknown state means custom control stops and firmware control wins.

## Proposed safety state machine

```text
OFF
 |
 v
VALIDATING ----failure----> FIRMWARE_AUTO
 |
 success
 v
ACTIVE_CUSTOM
 |       |
 |       +-- telemetry invalid --------+
 |       +-- RPM mismatch -------------+--> FIRMWARE_AUTO
 |       +-- overtemperature ----------+
 |       +-- unexpected exception -----+
 |
 explicit stop
 v
FIRMWARE_AUTO
```
