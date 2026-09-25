# Pre-automatic-control checklist

This checklist tracks what must be true before VictusFanControl is allowed to run an unattended adaptive fan policy on the validated HP 88F8 target.

## Telemetry and runtime

- [x] Direct CPU/GPU telemetry without LibreHardwareMonitor.
- [x] Independent CPU and GPU tachometer feedback.
- [x] Bounded EC/MSR/NVML retries and backend reconstruction.
- [x] EC mutex/backoff and coherent 16-bit tachometer reads.
- [x] 30-minute current-reader health soak: 1629/1629 complete, zero missing.
- [x] Suspend/resume detection, power-cycle epochs and duplicate resume coalescing.
- [~] Post-fix suspend/resume hardware regression: 2/5 planned cycles passed cleanly; remaining 3 were explicitly waived.
- [x] Runtime state machine and telemetry freshness watchdog.
- [x] Whole-payload freeze guard.
- [x] Exact target fingerprint and NVIDIA device identity checks.

## HP 88F8 write/restore route

- [x] Independent HP BIOS/WMI transport.
- [x] `SetFanLevel` contract validated against public OmenMon behavior and real hardware.
- [x] `GetFanLevel` semantics corrected: current speed-level telemetry, not command readback.
- [x] EC 0x34/0x35 fixed-setpoint acknowledgement.
- [x] Dedicated `FF,FF` fixed-level release sentinel.
- [x] Real hardware restore validated: `FF,FF -> LegacyDefault` returned EC setpoints to `FF/FF`.
- [x] Final bounded `30,30` test passed: both fans converged around 3000 RPM.
- [x] OMEN Gaming Hub remained open and CPU undervolt was unchanged before/after the bounded test.
- [x] Real `Hp88F8FanControlBackend` implemented behind `IFanControlBackend`.
- [x] Persistent EC session used by production backend acknowledgement path.
- [x] Central and backend-local hard command range 14-50.

## Authority and safety supervisor

- [x] `FanControlCoordinator` is the only normal-policy path to the backend.
- [x] Firmware / Custom / Restoring / Faulted authority states.
- [x] SafetyGate permission required before custom authority.
- [x] Invalid command, backend exception and uncertain partial entry restore HP authority.
- [x] Read-only ownership conflict does not clear another controller's override.
- [x] Continuous runtime SafetyGate enforcement.
- [x] In-flight backend acknowledgement can be cancelled by safety/lifecycle handoff.
- [x] Suspend closes custom admission and restores firmware authority.
- [x] Resume requires a post-boundary validated telemetry sample before admission can reopen.
- [x] Stale pre-resume SafetyGate results cannot reacquire authority.
- [x] Normal exit / Windows shutdown dispose the coordinator before telemetry teardown.
- [x] External EC setpoint overwrite is detected rather than fought.

## Fan command acknowledgement

- [x] EC setpoint acknowledgement is bounded.
- [x] Both physical tachometers participate in command acknowledgement.
- [x] Material increase/decrease requests require measured directional RPM response.
- [x] Near-current requests require valid non-zero tachometer continuity.
- [x] Physical fan ceilings are handled independently.
- [x] Synthetic CPU-tach failure, GPU-tach failure, ownership-loss and restore-failure tests run in CI.

## Remaining gates before automatic control

- [x] Integrated backend + coordinator real-hardware gate passed on 2026-09-24: Custom 30/30 acknowledged, six continuous supervision samples passed, restore returned EC 0x34/0x35 to FF/FF, and OMEN Gaming Hub undervolt remained unchanged.
- [x] Suspend-while-Custom real-hardware gate passed on 2026-09-24: the handler entered with Custom authority and a previously verified 30/30 EC setpoint, synchronously restored to Firmware + EC FF/FF before returning the suspend event, then recovered to Healthy/Firmware + FF/FF after resume; OMEN Gaming Hub undervolt remained unchanged.
- [x] Forced-process-termination / EC countdown characterized on 2026-09-24: after verified Custom 30/30, the VFC GUI was force-killed and managed cleanup could not run. EC 0x34/0x35 remained 30/30 while 0x63 counted down, then an external HP/OMEN-side component refreshed the countdown from 211 -> 239 and later 210 -> 239. Natural FF/FF recovery was not observed in 59.1 s. Explicit `FF,FF -> LegacyDefault` cleanup succeeded and OMEN Gaming Hub undervolt remained unchanged.
- [ ] Implement and validate an independent crash watchdog/lease process before unattended automatic control. It must survive GUI death, detect loss of the controller heartbeat/lease, restore through the validated `FF,FF -> LegacyDefault` path, and never depend on EC 0x63 expiring.
- [ ] Repeat validation under representative CPU load, GPU load and gaming.
- [ ] Validate thermal emergency handoff thresholds under load.
- [ ] Validate level-14 restart from a truly stopped fan before using it as a permanent minimum.
- [ ] Implement the shared physical-RPM adaptive controller, estimator, slew limits and independent per-fan compensation.
- [ ] Tune and validate that controller before enabling it by default.

## Current authority

Version 0.4 contains a real write-capable HP backend, but **automatic policy is OFF**. Launching the GUI does not acquire Custom authority or write a fan level. HP firmware remains authoritative until a future explicit policy passes SafetyGate and requests authority through `FanControlCoordinator`.
