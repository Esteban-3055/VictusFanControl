# HP 8C40 watchdog M5 - failure-domain qualification

Status: **M5A/M5B CODE/CI/PHYSICAL PASS. M5C PENDING.**
M4 normal awake lease qualification is complete at equal 10/30/50.

Production watchdog construction and automatic/adaptive policy remain OFF.

## Objective

M5 validates failure recovery, not ordinary fan-level control.

The intended sequence is:

~~~text
M5A  controller death while watchdog remains alive
M5B  watchdog death while controller remains alive
M5C  controller + watchdog double death
~~~

Each gate remains a separate authorization boundary. A PASS in M5A does not
authorize M5B/M5C or production watchdog promotion.

## M5A - controller death

M5A is the first physical failure-domain gate on the exact HP 8C40 target.

It uses the already-qualified isolated service:

~~~text
VictusFanControlWatchdogM4
LocalSystem
Session 0
TargetProfileId = HP-8C40-9D0R1LA-F18
Pipe = VictusFanControl.Watchdog.M4.8C40.v2
~~~

The service still has no ordinary fan-target write method. Its hardware surface
remains read ownership + restore firmware only.

The M5A controller child uses the real:

~~~text
SafetyGate
 -> FanControlCoordinator
 -> qualification-only Hp8C40FanControlBackend
 -> target-bound named-pipe lease client
 -> HP WMI
 -> EC + dual-tach acknowledgement
~~~

and holds only equal `30/30`.

## Required M5A causal sequence

~~~text
clean firmware baseline FF/FF
  -> service Ready / LocalSystem / Session 0
  -> controller PREPARE
  -> durable WRITE_INTENT 30/30
  -> WMI SetFanLevel(30/30)
  -> EC 30/30 + both physical tachometers acknowledge
  -> COMMIT -> durable OWNED 30/30
  -> READY marker bound to exact controller PID + process creation time
  -> parent validates exact durable OWNED journal
  -> parent force-kills only that exact controller PID
  -> managed controller finally/Dispose cannot run
  -> original LocalSystem watchdog process remains alive
  -> watchdog detects proven owner-process death
  -> watchdog executes M3-qualified FF/FF -> LegacyDefault restore
  -> watchdog verifies EC FF/FF
  -> durable journal deleted
  -> independent final EC probe FF/FF
~~~

M5A PASS requires the same watchdog service PID before and after the controller
death. A watchdog restart would mix failure domains and is therefore a formal
M5A failure even if firmware is ultimately safe.

## READY boundary

The child mode is explicitly gated by:

~~~text
--8c40-m5a-controller-death-arm
--8c40-m5a-token 8C40-M5A-CONTROLLER-DEATH30
--8c40-m5a-ready-path <path>
~~~

The READY JSON is written only after:

- exact HP 8C40 target match;
- AC/battery sanity gate;
- initialized telemetry;
- light-load SafetyGate admission;
- coordinator Custom authority;
- real `30/30` backend command;
- EC ownership acknowledgement;
- both physical tachometers acknowledged;
- watchdog Commit completed.

Before the force-kill, the parent requires the durable schema-v2 journal to be
`OWNED 30/30`, exact target-bound and owned by the same PID plus process
creation time as the READY controller.

No independent out-of-band EC probe is opened between READY and the kill
boundary. The child keeps ordinary Probe/Heartbeat supervision alive during
that interval.

## Cleanup safety

A normal Ctrl+C or managed child failure is not the M5A fault injection path.
The child then performs the usual coordinator firmware restore in its managed
finally path.

The physical parent harness contains no direct HP/WMI restore command. If the
fault-injection run fails while a durable journal remains, cleanup first gives
the live watchdog time to recover. If evidence remains, it requests service-stop
recovery and then, if necessary, startup recovery through the same isolated
LocalSystem service. The journal is never deleted by the harness.

If durable ownership evidence still remains after those bounded attempts, the
test prints a critical stop condition and leaves the journal intact for manual
inspection. No later write gate may run in that state.

## Physical harness

After CI is green:

~~~powershell
git pull
.\scripts\test-watchdog-m5a-controller-death-8c40.ps1
~~~

The explicit token is:

~~~text
8C40-M5A-CONTROLLER-DEATH30
~~~

Keep the computer awake on AC power and under light load. Do not suspend,
hibernate, close the lid, kill the watchdog service, or start another fan
controller during M5A.

## PASS boundary

M5A passes only if all of the following are proven:

~~~text
baseline FF/FF
service exact target / LocalSystem / Session 0
durable OWNED 30/30
READY exact controller PID + creation time
force-kill exact controller
same watchdog service PID remains alive
service log causally reports controller-death RestoredFirmware
journal disappears after verified restore
independent final EC FF/FF
no direct parent-shell HP restore
~~~

A final FF/FF state by itself is not sufficient.

## Still blocked after M5A

After M5A physical PASS:

- M5B watchdog-death recovery remains pending;
- M5C double-death recovery remains pending;
- Modern Standby proactive release/reacquisition remains pending;
- hibernation and in-flight race gates remain pending;
- `WatchdogRecoveryValidated` remains false;
- automatic/adaptive policy remains OFF.


## M5A preparation CI result

The initial M5A implementation commit
`2c34b97697b16b7551fe5312a53efff4c3b97761` passed complete GitHub
Actions run **#586**. The follow-up owner-loss evidence matcher hardening commit
`21d0d67d7b8d0217ff137798b0185ed24e8a5d8e` passed complete GitHub
Actions run **#587** on 2026-09-28.

The green run includes the dedicated M5A static causal invariant, warnings-as-
errors build, historical Gate E/F/G0/G1/G2 invariants, legacy 88F8 isolation,
M0, Gate B/C, M2/M3/M4 self-tests, SafetyGate, FanControlCoordinator, HP BIOS
contracts and both HP backend self-tests.

M5A is therefore code/CI prepared. The physical controller-death evidence is
still pending and remains a separate gate.


## M5A physical result

**PASSED on real hardware, 2026-09-28**, on exact target
`HP-8C40-9D0R1LA-F18`.

Observed causal evidence:

~~~text
baseline:
  EC FF/FF
  service PID 2856
  LocalSystem / Session 0
  target HP-8C40-9D0R1LA-F18

controller:
  PID 23200
  startTicks 639262217241653750
  PREPARE generation 1
  WRITE_INTENT generation 2 target 30/30
  COMMIT generation 3 target 30/30
  READY authority=Custom
  EC 30/30
  RPM 2998/2994
  guards MaxFan=0x00 / FanSwitch=0x00

fault:
  exact controller PID 23200 force-killed
  parent issued no HP restore command

recovery:
  original LocalSystem watchdog remained authoritative
  WATCHDOG OWNER LOSS reason=named-pipe EOF
  disposition=RestoredFirmware
  detail=VFC-owned setpoint 30/30 restored to FF/FF
  durable journal cleared after verified restore
  independent EC FF/FF
  measured parent-observed recovery interval 0.573 s
  post-test EC FF/FF
~~~

The durable journal was bound to the exact controller PID plus process creation
time before the kill. The service log recorded the complete
PREPARE -> WRITE_INTENT -> COMMIT -> owner-loss recovery chain. The harness
requires the watchdog service PID to remain unchanged, so this result isolates
controller death from watchdog restart/recovery.

M5A is closed. This does not yet promote
`WatchdogRecoveryValidated=true`; M5B watchdog death and M5C double death remain
separate physical gates.


## M5B - watchdog death while controller remains alive

M5B is prepared as the complementary failure domain to M5A. It deliberately
kills only the LocalSystem watchdog process after the real controller has
reached durable `OWNED 30/30`.

The isolated M4 service remains demand/manual for this qualification. This is
intentional: the parent harness requires the watchdog process to remain absent
until the still-live controller has independently detected
`WATCHDOG_IPC_LOSS` and completed the local HP
`FF/FF -> LegacyDefault` restore. Only after that proof does the parent
manually start a replacement service process.

Required causal sequence:

~~~text
clean FF/FF
  -> LocalSystem M4 service Ready
  -> live controller PREPARE
  -> WRITE_INTENT 30/30
  -> WMI + EC + dual-tach ACK
  -> COMMIT / durable OWNED 30/30
  -> READY exact controller identity
  -> parent force-kills exact watchdog PID only
  -> watchdog service absent
  -> controller remains alive
  -> ordinary dependency Probe reports WATCHDOG_IPC_LOSS
  -> coordinator executes live-controller local restore
  -> backend verifies local FF/FF
  -> watchdog Release remains unverified because service is absent
  -> parent independently verifies FF/FF while watchdog is still absent
  -> durable OWNED journal must still exist
  -> parent manually starts replacement M4 service
  -> startup recovery sees retained journal + FF/FF
  -> service normalizes full firmware restore
  -> startup disposition RestoredFirmware
  -> journal deleted
  -> independent final FF/FF
  -> controller receives parent-complete marker and exits
~~~

The M5B parent contains no direct HP/WMI restore authority. On an abnormal test
failure, it may start the already-qualified LocalSystem recovery service if a
durable journal remains; that cleanup path cannot satisfy M5B PASS.

M5B explicit token:

~~~text
8C40-M5B-WATCHDOG-DEATH30
~~~

Code/CI preparation is green. Physical execution remains the next boundary.


## M5B preparation CI result

M5B code preparation commit
`d232c8edb27177c1d1122762b97c00d5838cca87` passed complete GitHub
Actions run **#590** on 2026-09-28.

The green run included:

- PowerShell syntax validation;
- historical Gate E/F/G0/G1/G2 causal invariants;
- HP 8C40 legacy isolation;
- M5A controller-death invariant;
- the new M5B watchdog-death invariant;
- warnings-as-errors solution build;
- M0, Gate B/C and HP 8C40 M2/M3/M4 self-tests;
- SafetyGate and FanControlCoordinator self-tests;
- HP BIOS-contract and backend self-tests.

M5B is therefore **CODE/CI PASS / PHYSICAL PENDING**. No production watchdog
promotion or automatic policy change is made by this preparation.


## M5B physical attempt 1 - recovery evidence complete, harness post-check false negative

The first physical M5B run on 2026-09-28 completed the entire hardware and
service-recovery sequence successfully, but the PowerShell parent emitted a
false FAIL after recovery because its `Start-Process -PassThru` process object
exposed a blank `ExitCode` immediately after `HasExited` became true.

The completed causal evidence before that bookkeeping failure was:

~~~text
baseline EC FF/FF
original watchdog PID 5096 / LocalSystem / Session 0 / exact target
controller PID 2840 / exact creation time
PREPARE generation 1
WRITE_INTENT generation 2 target 30/30
COMMIT generation 3 target 30/30
READY Custom / EC 30/30 / dual tach 1935/1942 / guards 0/0
force-kill watchdog PID 5096 only
controller remained alive
WATCHDOG_IPC_LOSS during Probe
live controller local restore -> EC FF/FF
LocalFirmwareAckVerified=true
WatchdogLeaseRequired=true
WatchdogReleaseVerified=false
independent local EC FF/FF while watchdog absent
durable OWNED journal retained while watchdog absent
replacement watchdog PID 16684
startup RecoveryDisposition=RestoredFirmware
retained Owned journal normalized and deleted
independent final EC FF/FF
controller observed parent completion marker and exited
post-test EC FF/FF
~~~

The service log independently recorded the original PREPARE / WRITE_INTENT /
COMMIT chain and the replacement startup
`M4 STARTUP RECOVERY disposition=RestoredFirmware` with
`journalRetained=False`.

Therefore the physical M5B recovery mechanism itself is complete. The only
failure occurred after the controller had already observed the parent's
completion marker and after all hardware/recovery assertions had passed.

The harness was hardened by synchronizing the native child-process termination
with `WaitForExit()` plus `Refresh()` before reading `ExitCode`. Commit
`9efbcd17908b32778a60a557066180ce39b623e6` passed complete GitHub
Actions run **#592**. The hardening does not alter fan authority, watchdog
authority, fault injection, restore ordering or any physical PASS criterion.

Because the complete physical/recovery PASS boundary was already established
before the stale/blank PowerShell ExitCode read, no second destructive watchdog
kill is required merely to reproduce a parent-shell bookkeeping check. M5B is
formally accepted as **PHYSICAL PASS** from this evidence.


### M5B accepted physical result

M5B is closed on the 2026-09-28 run:

~~~text
OWNED 30/30
  -> exact watchdog PID 5096 killed
  -> controller PID 2840 remained alive
  -> WATCHDOG_IPC_LOSS during Probe
  -> live-controller local FF/FF verified
  -> watchdog Release unavailable as expected
  -> independent FF/FF while watchdog absent
  -> durable OWNED journal retained
  -> replacement watchdog PID 16684
  -> startup RestoredFirmware
  -> journal deleted after verified normalization
  -> independent final FF/FF
  -> controller observed parent completion
  -> post-test FF/FF
~~~

The PowerShell ExitCode synchronization fix is a post-evidence harness
correction, not a second hardware qualification requirement.


## M5C stage 0 - no-write double-death preflight

M5C is the final M5 failure-domain gate. Before implementing or executing the
destructive double-death boundary, a separate no-write preflight now validates
the service/recovery substrate without changing fan ownership.

The stage-0 script is:

~~~powershell
.\scripts\test-watchdog-m5c-preflight-8c40.ps1
~~~

It is intentionally incapable of entering Custom or injecting either process
failure. Its required sequence is:

~~~text
build + existing M5/M4/SafetyGate/backend regressions
  -> independent EC baseline FF/FF
  -> refuse any existing durable lease journal
  -> install isolated M4 service
  -> start exact-target LocalSystem / Session 0 service
  -> require target HP-8C40-9D0R1LA-F18
  -> require pipe VictusFanControl.Watchdog.M4.8C40.v2
  -> configure temporary SCM restart policy
  -> verify ordered restart delays
  -> stop/reinstall ordinary M4 qualification service
  -> require Manual + Stopped baseline again
  -> independent final EC FF/FF
  -> require journal still absent
~~~

The temporary first restart delay defaults to 5000 ms. That deliberate window is
for the later M5C causal proof: after the original watchdog and controller are
both gone, the physical harness must be able to observe that neither original
recovery domain restored the still-owned setpoint before the replacement service
starts.

Stage 0 contains no `SetFanLevel`, no HP-auto restore command, no M4 lease
write token, no process fault injection and no durable-journal deletion. CI also
checks those invariants.

Passing this preflight does **not** close M5C. The destructive OWNED double-death
harness remains a later explicit boundary.


### M5C delayed service-start safety fallback

A separate delayed fallback is also prepared:

~~~powershell
.\scripts\watchdog-m5c-service-failsafe-8c40.ps1 -LogPath <path>
~~~

It has no HP/WMI restore authority. After its delay it acts only when all of
these are true:

- the schema-v2 journal still exists;
- the target is exactly `HP-8C40-9D0R1LA-F18`;
- the journal is still durable `OWNED 30/30`;
- the recovery service is installed;
- no recovery-service process is currently running.

In that case it starts the already-qualified
`VictusFanControlWatchdogM4` service and leaves journal ownership/recovery to
that service. It never deletes the journal and never calls `SetFanLevel` or a
direct HP-auto restore.

For a future M5C **PASS**, this delayed fallback must remain pending and be
cancelled only after replacement-watchdog recovery is independently proven. If
the delayed fallback actually has to start the service, that run is safety
recovered but cannot satisfy the intended autonomous SCM-restart M5C criterion.


### Planned M5C destructive boundary

The later destructive M5C gate is intentionally specified before implementation
so that no test code can silently weaken its causal requirements.

The required physical proof is:

~~~text
clean firmware baseline FF/FF
  -> exact LocalSystem / Session 0 watchdog
  -> controller acquires PREPARED
  -> durable WRITE_INTENT 30/30
  -> real HP WMI write
  -> EC 30/30 + both physical tachometers acknowledge
  -> COMMIT / durable OWNED 30/30
  -> exact controller PID + creation time verified
  -> exact watchdog PID + creation time verified

FAULT BOUNDARY:
  issue watchdog termination first
  -> issue exact controller termination immediately after
  -> no sleep / EC read / SCM query between the two fault requests
  -> bounded fault-request interval
  -> both original processes confirmed gone

PRE-RESTART CAUSAL WINDOW:
  replacement watchdog must still be absent
  -> EC must still be the durable owned 30/30
  -> exact schema-v2 OWNED journal must still exist
  -> no original controller local restore can have completed
  -> no original watchdog owner-loss restore can have completed

RECOVERY:
  SCM starts a distinct replacement watchdog
  -> startup reads exact target-bound durable OWNED journal
  -> observed 30/30 is lease-compatible
  -> service executes FF/FF + LegacyDefault
  -> service verifies EC FF/FF
  -> service deletes journal only after verified normalization
  -> status Ready / RecoveryDisposition=RestoredFirmware
  -> independent final EC FF/FF
~~~

A final FF/FF state alone is not sufficient. M5C must fail formally if the
pre-restart window cannot prove that both original recovery domains were gone
while the real owned setpoint and journal were still present.

The temporary SCM restart delay is therefore part of the qualification harness,
not a production-policy decision. After a successful or safely recovered test,
the ordinary M4 qualification service must be reinstalled as Manual/stopped so
the temporary recovery policy does not leak into later gates.

The delayed service-start failsafe is a safety layer only. If it actually fires,
that run cannot be counted as the autonomous SCM-restart M5C PASS.


## M5C stage-0 preparation CI result

The staged M5C preparation is green:

- `e5f1599e66edbd8f0dd2dacdd6c79d1238bf1d3f` / GitHub Actions **#594**:
  initial no-write exact-target preflight;
- `0d466aa3a31666147368dce2f8585042a2a786fb` / **#595**:
  delayed service-start safety fallback plus invariants;
- `5604e0d9470593bcc39145608658fb9af8d25565` / **#596**:
  formal M5C causal PASS boundary;
- `e1dd2510160bac04f7b0c3a92a850798a39f7483` / **#597**:
  fail-closed preflight cleanup hardening.

Run #597 passed PowerShell syntax, M5A/M5B/M5C invariants, warnings-as-errors
build, historical Gate E/F/G0/G1/G2 invariants, M0, Gate B/C, HP 8C40
M2/M3/M4 self-tests, SafetyGate, FanControlCoordinator, HP BIOS contracts and
both HP backend self-tests.

M5C stage 0 is therefore **CODE/CI PASS / PHYSICAL NO-WRITE PREFLIGHT
PENDING**. The destructive double-death gate remains intentionally unqualified.


## M5C stage-0 physical preflight attempt 1 - no-write compatibility failure

The first local M5C stage-0 preflight attempt on 2026-09-28 stopped during
**Step 1**, after a clean warnings-as-errors build and before Step 2, service
installation, SCM recovery-policy changes, any fan write, any durable lease, or
any fault injection.

The failure was a Windows PowerShell 5.1 compatibility bug in the M5 invariant
helpers:

~~~text
No overload for "Contains" and argument count "2"
Text.Contains(Needle, StringComparison.Ordinal)
~~~

The overload is available to the PowerShell Core/.NET runtime used by the
existing CI but not to the local Windows PowerShell 5.1/.NET Framework runtime.

The invariant helpers are hardened to use
`String.IndexOf(..., StringComparison.Ordinal)`, which preserves exact ordinal
matching and is available on Windows PowerShell 5.1. CI now also executes the
M5A, M5B and M5C invariant scripts explicitly under the
`powershell` (Windows PowerShell 5.x) shell in addition to `pwsh`.

This attempt is classified **NO_WRITE_FAIL_CLOSED** and does not count as the
physical no-write preflight PASS.


### Windows PowerShell 5.1 compatibility hardening result

Commit `8f510bee22b652bd605f5b0d8e714a9177503163` passed complete GitHub
Actions run **#599** on 2026-09-28.

The run includes a dedicated `shell: powershell` step and successfully executes
the M5A, M5B and M5C invariant scripts under Windows PowerShell 5.x, in addition
to the existing PowerShell Core path. The full warnings-as-errors build and all
historical watchdog, lifecycle, safety, coordinator, BIOS-contract and HP backend
self-tests also passed.

The local stage-0 preflight may therefore be retried. The previous attempt
remains recorded as **NO_WRITE_FAIL_CLOSED**.
