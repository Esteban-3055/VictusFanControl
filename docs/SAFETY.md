# Safety design

## Current version

The current development branch is telemetry/GUI-only. It contains no fan write path.

## Implemented pre-control protections

The project now has a central read-only safety gate that evaluates:

- HP motherboard allowlist (initial target: `88F8`)
- runtime state must be `Healthy`
- every required telemetry field must be present
- telemetry must be fresh (maximum age currently 3 seconds)
- sensor values must pass plausibility checks
- conservative thermal handoff thresholds
- write/restore backend presence

The final item is intentionally hard-coded absent in the current build, so custom control is impossible even when every read-only prerequisite passes.

A separate telemetry watchdog transitions the runtime out of `Healthy` if no read completes for 4 seconds.

## Required invariants before any control release

A future control-capable build must satisfy all of the following before fan writes can be enabled by default.

1. **Board allowlist**
   - Control is disabled unless the detected HP Product ID has a validated profile.
   - Initial control target: `88F8` only.

2. **Hard command limits**
   - For the tested 88F8 profile, experimental level requests are clamped to a validated range.
   - Current measured range candidate: 14 through 50.
   - The limits must remain configurable per board, never globally assumed.

3. **Sensor plausibility / freshness**
   - Invalid, missing or stale temperatures cause immediate control abort.
   - Missing CPU telemetry is always fatal to custom control.
   - No old sensor value may be silently reused as if it were current.
   - A blocked telemetry loop must be detected independently of the sampling loop.

4. **Thermal override**
   - High temperature overrides acoustic targets.
   - Critical temperature must transition to firmware authority / validated maximum cooling, not continue the adaptive policy.
   - Current GUI thresholds are conservative pre-control placeholders and must be validated under load before release.

5. **RPM feedback**
   - After a fan command, measured RPM must move toward an expected range within a bounded time.
   - Both tachometers remain independently authoritative.
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
   - The current GUI is single-instance; external-controller conflict detection remains a blocker for write mode.

10. **Fail closed**
    - Any unknown state means custom control stops and firmware control wins.

## Proposed write-capable safety state machine

```text
FIRMWARE_AUTO
     |
     v
VALIDATING ----failure----> FIRMWARE_AUTO
     |
   success
     v
ACTIVE_CUSTOM
 |       |
 |       +-- telemetry invalid/stale ---+
 |       +-- RPM mismatch --------------+--> FIRMWARE_AUTO
 |       +-- overtemperature -----------+
 |       +-- unexpected exception ------+
 |
 explicit stop / suspend / exit
     |
     v
FIRMWARE_AUTO
```

See `PRE_CONTROL_CHECKLIST.md` for the remaining blockers.
