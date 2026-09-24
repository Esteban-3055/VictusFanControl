# Pre-control checklist

VictusFanControl must complete this checklist before the first fan-control write is enabled.

## Implemented read-only prerequisites

- [x] Direct CPU/GPU telemetry without LibreHardwareMonitor.
- [x] Independent CPU and GPU tachometer feedback.
- [x] Bounded EC/MSR/NVML retries and backend reconstruction.
- [x] Strict telemetry soak test.
- [x] Suspend/resume detection and backend revalidation.
- [x] Duplicate Windows resume-event coalescing.
- [x] Runtime state machine.
- [x] Telemetry freshness watchdog.
- [x] Whole-payload freeze guard for repeated identical sensor snapshots.
- [x] HP 88F8 runtime board identification / allowlist gate.
- [x] Sensor plausibility gate.
- [x] Conservative thermal handoff gate.
- [x] Persistent application/event log.
- [x] Notification-area status and diagnostics GUI.
- [x] Single VictusFanControl GUI instance per Windows session.
- [x] GUI requires elevation so PawnIO access cannot silently run unprivileged.
- [x] Synthetic fail-closed SafetyGate self-test runs in CI.
- [x] Read-only OMEN/Gaming Hub process discovery for target-machine conflict mapping.
- [x] Fan write path remains physically absent / hard-disabled.

## Still required before custom control can be enabled

- [ ] Validate the latest suspend/resume build on the HP 88F8 hardware.
- [ ] Repeat telemetry health tests under CPU load, GPU load and gaming.
- [ ] Validate external-controller ownership/conflict behavior with HP OMEN Gaming Hub.
- [ ] Implement the write backend behind an interface that can always restore HP firmware authority.
- [ ] Validate HP firmware-auto restoration after normal exit, exception and forced process termination.
- [ ] Validate the 88F8 watchdog/countdown behavior and recovery semantics.
- [ ] Implement fan-command acknowledgement using both tachometers.
- [ ] Define bounded RPM-response timeout and mismatch thresholds from hardware measurements.
- [ ] Validate thermal emergency thresholds under load.
- [ ] Keep all write-capable code disabled unless board, telemetry, freshness and runtime gates pass.
- [ ] Only after all previous items pass: implement the adaptive/shared-RPM controller.

## Current authority

The current v0.3 development GUI is still read-only. **HP firmware owns both fans at all times.**

The GUI may display that the current preconditions are ready, but the central safety gate still reports the fan write/restore backend as absent, so custom fan control cannot be enabled.
