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
- [x] Normal GUI/controller fan-write path remains hard-disabled; explicit validation-only WMI commands are isolated from it.
- [x] Narrow IFanControlBackend boundary prevents arbitrary controller-side EC writes.
- [x] FanControlCoordinator owns authority transitions and fail-safe restoration.
- [x] Validated 88F8 command range 14-50 is enforced before a backend receives a command.
- [x] Synthetic coordinator tests verify disabled backend refusal, safety-loss restore, invalid-command restore, backend-failure restore and normal restore.


## Previously validated 88F8 control behavior with OmenMon

The target HP 88F8 has already been exercised successfully through OmenMon's BIOS fan interface.

- `OmenMon.exe -Bios FanLevel=30,30` produced approximately 3000 RPM on both fans.
- Fixed levels `14,14`, `20,20`, `40,40` and `50,50` were also exercised during characterization.
- The fixed-level operation populated the known fan set-points and started the manual countdown/watchdog at EC `0x63`.
- The previously used restore command was `OmenMon.exe -Bios FanMode=LegacyDefault`, returning fan authority to the HP BIOS/automatic policy.
- OmenMon's textual `Mode=LegacyDefault` / `Manual=False` fields were not sufficient by themselves to identify active fixed-level control on this board; countdown, set-point and measured RPM behavior were the useful evidence.

Therefore the project does **not** need to rediscover how to enter/leave fan control from scratch. The remaining task is to independently implement and revalidate the equivalent HP BIOS/WMI operations behind `IFanControlBackend`, without copying OmenMon GPL source.

## Still required before custom control can be enabled

- [ ] Validate the latest suspend/resume build on the HP 88F8 hardware.
- [ ] Repeat telemetry health tests under CPU load, GPU load and gaming.
- [ ] Validate external-controller ownership/conflict behavior with HP OMEN Gaming Hub while keeping the user's CPU undervolt active.
- [ ] During the first fan-write test, verify that the OMEN Gaming Hub undervolt remains unchanged before, during and after custom fan control and after restoring HP firmware authority.
- [x] Independently implement and contract-test the HP BIOS/WMI `FanLevel`, `GetFanLevel` and `FanMode=LegacyDefault` operations. Production `IFanControlBackend` integration remains intentionally disabled.
- [ ] Revalidate `LegacyDefault` restoration in our own backend, then validate HP firmware-auto restoration after normal exit, exception and forced process termination.
- [ ] Validate the 88F8 watchdog/countdown behavior and recovery semantics.
- [~] Bounded first-write harness now checks exact BIOS readback, EC setpoints and both tachometers; production-controller acknowledgement still remains to be integrated.
- [ ] Define bounded RPM-response timeout and mismatch thresholds from hardware measurements.
- [ ] Validate thermal emergency thresholds under load.
- [ ] Keep all write-capable code disabled unless board, telemetry, freshness and runtime gates pass.
- [ ] Only after all previous items pass: implement the adaptive/shared-RPM controller.

## Current authority

The current v0.3 development GUI is still read-only. **HP firmware owns both fans during normal application use.** Explicit CLI validation commands exist, but are not wired into the GUI/adaptive controller.

The GUI may display that the current preconditions are ready, but the central safety gate still reports the fan write/restore backend as absent, so custom fan control cannot be enabled.


## Latest deep-audit hardening

- [x] Exact target fingerprint added: HP 88F8 / 88.58, Victus 16-d0xxx, SKU prefix 62C37LA and expected RTX 3060 identity.
- [x] Coordinator now obeys `CustomControlPermitted` rather than only read-only preconditions.
- [x] Partial custom-entry failure forces a firmware-restore attempt.
- [x] Central hard 14-50 limits are independent of backend-advertised capabilities.
- [x] EC wait loop uses bounded spin/yield/sleep backoff rather than prolonged pure spinning.
- [x] 16-bit tachometers receive coherence validation against torn reads.
- [x] Power-cycle epochs discard reads/recoveries that belong to an obsolete suspend/resume cycle.
- [x] First-write WMI provider/session setup occurs before the write and WMI method execution is bounded.
- [x] First-write harness rejects known concurrent OmenMon/VictusFanControl GUI controllers while allowing OMEN Gaming Hub for undervolt validation.
- [ ] Re-run read-only hardware soak and suspend/resume regression after these changes.
