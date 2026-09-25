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
- [x] Independent crash watchdog/lease Gate A passed on 2026-09-24 under LocalSystem: three Session 0 start/stop cycles matched the exact target, read PawnIO EC state, read HP WMI GetFanLevel, stayed at EC FF/FF, and final interactive verification remained FF/FF. The LocalService comparison failed before any EC/WMI hardware read because `Global\Access_EC` denied access, so the hardware watchdog service context is LocalSystem with a deliberately narrow command surface.
- [x] Watchdog Gate B service-only restore passed on real hardware on 2026-09-24: after validated backend EC+dual-tach ACK at Custom 30/30, the exact VFC GUI process was force-killed, an independent probe confirmed orphaned 30/30, and the LocalSystem Session 0 service alone executed `FF,FF -> LegacyDefault` and verified EC FF/FF in about 443 ms. A separate parent probe re-confirmed FF/FF, the delayed fallback was cancelled only after verification, and OMEN Gaming Hub undervolt remained unchanged.
- [x] Gate C synthetic lease/journal/IPC validation passed in Windows CI and a dedicated pre-Gate-D safety audit on 2026-09-24: PREPARED -> WRITE_ARMED -> OWNED -> RESTORING transitions are generation-checked; WriteIntent is durably journaled before acknowledgement; client PID + creation time are verified from the named pipe; heartbeat/deadline and broken-pipe owner-loss paths are covered; restart at every write-risk phase either completes the validated restore or refuses ambiguous external ownership; corrupt journals and unknown fixed setpoints never trigger blind restore; RELEASE cannot clear the lease on FF/FF alone—it first normalizes the complete `FF,FF -> LegacyDefault` restore and retains the journal if that restore fails; and late Heartbeat/WriteIntent/Commit commands cannot revive a lease whose state deadline has already expired.
- [x] Gate D passed on real hardware on 2026-09-25: persistent LocalSystem watchdog started Ready in Session 0; the real backend reached durable OWNED 30/30 with journal PID + creation-time matching the exact GUI; that GUI was force-killed with no managed cleanup; the parent shell issued no HP restore; the same watchdog service process recorded owner-loss `RestoredFirmware`, executed the validated restore, cleared the journal, and a separate EC probe confirmed 255/255. The emergency fallback did not fire and OMEN Gaming Hub undervolt remained unchanged.
- [~] Gate E implementation is complete and synthetic/CI build coverage is in place; physical validation is pending. The test reaches durable OWNED 30/30, force-kills only the watchdog PID, requires the live GUI to report a watchdog-loss local restore and independently reach EC FF/FF before SCM restart, then requires a new service PID to consume the retained journal with startup RestoredFirmware and return Ready. The harness temporarily delays SCM restart only to prove causality, then reinstalls the production 1 s / 5 s / 10 s recovery policy.
- [ ] Gate F: double-failure / durable-journal recovery with controller + watchdog failure near WRITE_ARMED/OWNED.
- [ ] Gate G: suspend/resume with the full watchdog stack.
- [ ] Repeat validation under representative CPU load, GPU load and gaming.
- [ ] Validate thermal emergency handoff thresholds under load.
- [ ] Validate level-14 restart from a truly stopped fan before using it as a permanent minimum.
- [ ] Implement the shared physical-RPM adaptive controller, estimator, slew limits and independent per-fan compensation.
- [ ] Tune and validate that controller before enabling it by default.

## Current authority

Version 0.4 contains a real write-capable HP backend, but **automatic policy is OFF**. Launching the GUI does not acquire Custom authority or write a fan level. HP firmware remains authoritative until a future explicit policy passes SafetyGate and requests authority through `FanControlCoordinator`.
