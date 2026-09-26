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
- [x] Gate E passed on real hardware on 2026-09-25: after a clean production postcheck, the real backend reached READY with EC+dual-tach ACK and durable OWNED generation 3 at 30/30, bound to GUI PID 7544. The harness force-killed only watchdog PID 16724; the still-live GUI classified the failure as `WATCHDOG_IPC_LOSS during Probe: Pipe is broken`, restored locally to Firmware, and an independent probe confirmed EC FF/FF before any replacement watchdog existed. The retained journal was then consumed by SCM-restarted watchdog PID 14184 with startup `RestoredFirmware`; final/post-test EC remained FF/FF, the emergency fallback did not fire, production watchdog PID 492 returned Ready with SCM recovery re-verified at 1 s / 5 s / 10 s, and OMEN Gaming Hub undervolt remained unchanged.
- [x] Gate F passed on real hardware on 2026-09-25 as two independent double-failure subgates. F1 proved durable OWNED recovery: watchdog PID 10508 and GUI PID 16076 reached OWNED generation 3 at 30/30, watchdog-first/GUI-second kill calls were issued 1.071 ms apart, both originals died, no GUI restore began, and SCM replacement PID 13288 recovered with `RestoredFirmware` to verified FF/FF. F2 then proved the narrower first-write WRITE_ARMED post-WMI/post-EC+dual-tach-ACK but pre-Commit window on `ae1781d9299784096dc95379be4d14e6eb2391a6`: GUI PID 9164 had generation 2 with Pending=30/30, PreviousOwned=null and Owned=null; watchdog PID 4316 was killed first and the GUI kill followed 0.244 ms later; both originals were confirmed dead; SCM replacement PID 25200 recovered the retained WRITE_ARMED journal with `RestoredFirmware`, restored 30/30 to FF/FF and cleared the journal. The emergency fallback did not fire, production watchdog PID 22836 returned Ready with SCM recovery re-verified at 1 s / 5 s / 10 s, the parent shell issued no HP restore, and OGH undervolt remained unchanged.
- [x] Gate G0 passed on physical S3 hardware on 2026-09-25: production `QueryUnbiasedInterruptTime` excluded about 10.546 s across a real Application API suspend/resume boundary while the 5 s / 12 s / 8 s watchdog deadlines remained unchanged; Kernel-Power 42 -> 107 was observed and CI #285 was green. The system resumed before the requested +20 s wake-timer deadline, so wake-timer causality is not claimed.
- [x] Historical Gate G1 hardware cycle passed on 2026-09-25: watchdog PID 29972 and GUI PID 26944 reached exact durable OWNED generation 3 at 30/30; that specific PBT_APMSUSPEND happened to complete RestoreBegin -> local FF/FF -> LegacyDefault -> Release before S3, and recovery/re-entry/final Firmware + FF/FF + journal-absent checks all passed with OGH undervolt SAME. This remains valid evidence for the lifecycle, but later repetitions proved that full pre-S3 completion is not a stable Windows user-mode guarantee and is no longer the required production contract.
- [ ] Gate G1 revised-contract regression + Gate G2 5/5: later physical runs exposed three distinct PBT_APMSUSPEND races (redundant EC proof crossing S3; watchdog status/journal proof crossing S3; caller finally/marker continuation crossing S3). The latest regression then showed TelemetryWorker reached Suspended in 266.7 ms but the validated restore itself completed only after wake (restore evidence 31016.5 ms, Firmware 31016.9 ms). Windows only grants approximately two seconds to PBT_APMSUSPEND and may interrupt longer work, so the production invariant is now: fence admission + mark telemetry Suspended promptly before blocking restore IO; permit the same durable OWNED/RESTORING transaction to span S3 under the sleep-excluding watchdog clock; before accepting the first resume, require local FF/FF evidence, successful watchdog Release, original watchdog PID Ready, journal absent and current Firmware authority. Only the pre-block phase is constrained to 1800 ms. Re-pass one-cycle G1 under this contract, then run 5/5 consecutive cycles without intentional GUI/service restart, with one accepted resume per cycle, duplicate resumes coalesced, same watchdog PID, no stale re-admission, no journal leak, and final Firmware + FF/FF + journal absent.
- [ ] Repeat validation under representative CPU load, GPU load and gaming.
- [ ] Validate thermal emergency handoff thresholds under load.
- [ ] Validate level-14 restart from a truly stopped fan before using it as a permanent minimum.
- [ ] Implement the shared physical-RPM adaptive controller, estimator, slew limits and independent per-fan compensation.
- [ ] Tune and validate that controller before enabling it by default.

## Current authority

Version 0.4 contains a real write-capable HP backend, but **automatic policy is OFF**. Launching the GUI does not acquire Custom authority or write a fan level. HP firmware remains authoritative until a future explicit policy passes SafetyGate and requests authority through `FanControlCoordinator`.
