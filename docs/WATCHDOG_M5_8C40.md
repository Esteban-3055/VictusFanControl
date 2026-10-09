# HP 8C40 watchdog M5 - failure-domain qualification

Status: **M5A/M5B/M5C/M5D/M5E CODE/CI/PHYSICAL PASS for the complete awake OWNED/WRITE_ARMED failure matrix. M6 Modern Standby, M7 hibernation and M8 representative-load/thermal-preemption have subsequently passed physically. Production watchdog promotion remains a separate explicit post-M8 gate and `WatchdogRecoveryValidated=false`.**
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


## M5C stage-0 physical no-write preflight result

**PASSED on real hardware, 2026-09-28**, after the Windows PowerShell 5.1
compatibility hardening.

Observed evidence:

~~~text
warnings-as-errors build: PASS
M5A invariant: PASS
M5B invariant: PASS
M4 lease preparation: PASS
Gate C lease/journal/pipe: PASS
SafetyGate: PASS
FanControlCoordinator: PASS
HP backend: PASS

initial EC: FF/FF
initial durable journal: ABSENT

M4 qualification service:
  Ready=True
  Session=0
  Account=NT AUTHORITY\SYSTEM
  Target=HP-8C40-9D0R1LA-F18
  Pipe=VictusFanControl.Watchdog.M4.8C40.v2
  PID=15820
  StartupRecovery=Ready
  ordinary SetFanLevel authority=NONE

temporary SCM recovery policy:
  restart 5000 ms
  restart 5000 ms
  restart 10000 ms
  failure actions enabled

fan-level writes during preflight: NONE
fault injection during preflight: NONE

cleanup:
  service reinstalled to ordinary demand/manual M4 baseline
  final EC FF/FF
  durable journal ABSENT
~~~

The script reached its terminal
`PASS: HP 8C40 M5C no-write preflight completed`, which also proves its
fail-closed cleanup assertions for Manual/stopped service baseline completed.

Stage 0 is physically closed. This does not qualify the destructive M5C
double-death gate; it only authorizes preparing that next isolated test.


## M5C destructive-gate safety hardening after CI #602 review

Commit `e31879e237dbf948d9712df165817ca6568f006c` passed complete
GitHub Actions run **#602** and its destructive invariant was green under both
PowerShell Core and Windows PowerShell 5.1.

A post-CI causal/safety review found one remaining harness-level gap before
physical execution: the delayed fallback was armed only after durable OWNED
30/30 and treated a still-running watchdog as a no-op. If the parent PowerShell
were to disappear while the controller and watchdog both remained alive, the
qualification controller could therefore continue holding 30/30 without the
independent fallback forcing the test back toward firmware ownership.

The destructive gate is hardened before physical use:

- delayed fallback default is increased from 45 s to 120 s;
- the fallback is armed **before** launching the write-capable qualification
  controller;
- at the physical fault boundary the parent proves the fallback process is
  still alive;
- if the fallback timer expires with an exact schema-v2 OWNED 30/30 journal, it
  validates the journal controller PID **and process creation time**;
- if that exact controller is still alive, the fallback terminates only that
  controller so the already-qualified watchdog owner-loss path can restore;
- if the service is absent, the fallback starts the already-qualified M4
  service; if the exact owner is already gone and a running service leaves the
  journal unresolved, the fallback may restart that same recovery service;
- the fallback still has **no HP/WMI restore authority**, no ordinary fan-target
  authority and no journal-deletion authority;
- any fallback takeover still invalidates M5C PASS. It exists only as an
  independent safety recovery path.

This hardening does not change the M5C PASS boundary: autonomous SCM replacement
recovery after the intentional double death must still occur before the delayed
fallback fires.


## M5C destructive double-death physical result

**PASSED on real hardware, 2026-09-28**, on exact target
`HP-8C40-9D0R1LA-F18`.

The final destructive harness commit
`f67bfed6f959a974adc32a8be541ab11049589d4` passed complete GitHub
Actions run **#606** before the physical result was recorded.

Observed causal evidence:

~~~text
baseline:
  EC FF/FF
  exact LocalSystem / Session 0 watchdog
  original watchdog PID 2392
  watchdog startTicks 639262273456907747
  temporary SCM first restart delay 5000 ms

independent delayed safety fallback:
  PID 19112
  armed before write-capable controller
  delay 120 s
  did not take over during the qualifying recovery

controller / durable ownership:
  controller PID 22984
  controller startTicks 639262273921976648
  PREPARE generation 1
  WRITE_INTENT generation 2 target 30/30
  COMMIT generation 3 target 30/30
  READY authority=Custom
  EC 30/30
  dual physical tach acknowledgement 2863/2639 RPM
  guards MaxFan=0x00 / FanSwitch=0x00
  durable journal phase OWNED / generation 3 / exact controller identity

double-death boundary:
  original watchdog kill issued first
  exact controller kill issued immediately second
  measured kill-issue delta 1.445 ms
  both original processes confirmed dead

pre-restart causal window:
  replacement service still absent
  EC remained 30/30
  exact durable OWNED journal remained
  evidence collected 2652 ms after fault boundary
  configured SCM first restart remained 5000 ms

replacement recovery:
  distinct replacement watchdog PID 3788
  replacement startTicks 639262274014840305
  startup RecoveryDisposition=RestoredFirmware
  detail=service startup: VFC-owned setpoint 30/30 restored to FF/FF and verified before journal deletion
  journalRetained=False
  independent final EC FF/FF

cleanup:
  post-test EC FF/FF
  ordinary M4 qualification service reinstalled
  service baseline Manual/stopped
  temporary M5C SCM recovery actions removed
~~~

The service log independently recorded the original
PREPARE -> WRITE_INTENT -> COMMIT chain and the distinct replacement-process
startup recovery with `journalRetained=False`.

The result satisfies the planned durable-OWNED M5C causal boundary. A final
FF/FF state alone was not used as proof: the harness first proved both original
recovery domains dead while the real owned 30/30 state and exact durable journal
were still present.

M5A, M5B and the planned durable-OWNED M5C failure-domain gates are now closed
for the awake exact-target path.

This does **not** set `WatchdogRecoveryValidated=true` and does not enable
automatic/adaptive policy. Modern Standby proactive release/reacquisition,
hibernation/lifecycle behavior and any separately required in-flight
WRITE_ARMED crash qualification remain outside this physical M5C result.


## M5D - WRITE_ARMED post-WMI / pre-Commit controller crash

M5C closed the durable OWNED double-death case. M5D targets a narrower
transactional ambiguity window that is different from OWNED:

~~~text
PREPARE
  -> durable WRITE_INTENT 30/30
  -> real HP WMI SetFanLevel(30/30)
  -> EC 30/30 acknowledgement
  -> both physical tachometers acknowledge
  -> controller process dies
  -> watchdog Commit was never dispatched
~~~

The production backend now contains an **internal qualification-only hook** that
can pause exactly after real hardware acknowledgement and before the watchdog
Commit call. The public production constructor always sets this hook to null, so
ordinary runtime behavior is unchanged.

The M5D child:

~~~text
Hp8C40M5DWriteArmedCrashTest
token = 8C40-M5D-WRITE-ARMED-CRASH30
target = equal 30/30 only
~~~

uses the real SafetyGate, FanControlCoordinator, named-pipe M4 lease,
Hp8C40FanHardware, WMI command path, EC setpoints and both tachometers.

Its READY marker is written only from the pre-Commit qualification hook and
contains the exact process PID/creation-time plus real 30/30 EC/tach evidence.
The hook then waits indefinitely; only a parent force-kill bypasses managed
coordinator cleanup.

The physical parent must independently require:

~~~text
schema-v2 exact target
phase = WRITE_ARMED
generation = 2
PreviousOwned = null
Pending = 30/30
Owned = null
exact child PID + creation time
independent EC = 30/30
same original watchdog PID
~~~

immediately before killing the controller.

PASS then requires the original LocalSystem watchdog to recover that WRITE_ARMED
journal to verified FF/FF, delete the journal, and remain the same process.
Fresh service logs must contain PREPARE and WRITE_INTENT for that exact child,
must contain **no COMMIT** for it, and must causally record
`RestoredFirmware` after owner loss.

This gate is intentionally narrower than a later possible WRITE_ARMED
double-death/startup test. M5D first isolates controller death while the watchdog
remains alive.

M5D code/CI preparation is green. Physical execution remains the next
explicit qualification boundary.


### M5D preparation CI result

The complete M5D preparation head
`877aa9eb49d905e00511540eb78f2798260aee32` passed GitHub Actions
**#622** (run `36487711565`) on 2026-09-28.

The green run includes:

- PowerShell syntax validation;
- historical Gate E/F/G0/G1/G2 invariants;
- HP 8C40 M5A/M5B/M5C invariants;
- the new M5D causal invariant under both PowerShell Core and Windows
  PowerShell 5.1;
- warnings-as-errors solution build;
- M0, Gate B/C, HP 8C40 M2/M3/M4 self-tests;
- SafetyGate and FanControlCoordinator self-tests;
- HP BIOS-contract and both HP backend self-tests;
- the synthetic backend assertion that the qualification hook executes only
  after real-command acknowledgement ordering and before watchdog Commit.

Intermediate preparation runs failed fail-closed while the new hook types and
static invariant were being corrected; no physical M5D execution occurred in
those runs.

M5D is therefore **CODE/CI PASS / PHYSICAL PENDING**. This does not alter the
public production backend watchdog prohibition, `WatchdogRecoveryValidated`
remains false, and automatic/adaptive policy remains OFF.


## M5D physical result - WRITE_ARMED post-WMI / pre-Commit controller death

**PASSED on real hardware, 2026-09-28**, on exact target
`HP-8C40-9D0R1LA-F18`.

The first physical attempt reached the intended real post-WMI/pre-Commit
boundary, but the parent harness hit a Windows PowerShell automatic-variable
collision because a helper declared a case-insensitive `$pid` parameter.
Cleanup killed the exact controller, watchdog recovery returned EC to FF/FF,
and the run was correctly retained as a harness false negative rather than
accepted as M5D PASS.

The harness was corrected to use
`$ControllerProcessId` / `$ControllerStartTicks`, and its invariant now
forbids reintroducing a `$pid` parameter. The corrected head
`02a69b808fa86a4334262d741d4dadfc37bdfa73` passed complete GitHub
Actions run **#625** before the second physical execution.

Accepted physical result:

~~~text
baseline:
  EC FF/FF
  original watchdog PID 20824
  LocalSystem / Session 0
  exact target HP-8C40-9D0R1LA-F18

controller:
  PID 22592
  startTicks 639262445516182484
  real WMI target 30/30
  EC 30/30
  dual physical tach ACK 2882/2655 RPM
  guards MaxFan=0x00 / FanSwitch=0x00
  READY stage WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT

durable journal at crash boundary:
  schema 2
  phase WriteArmed
  generation 2
  PreviousOwned null
  Pending 30/30
  Owned null
  exact controller PID + creation time

independent pre-kill proof:
  EC 30/30
  original watchdog process unchanged
  watchdog Commit not dispatched

fault:
  force-kill exact controller PID 22592

recovery:
  same live watchdog detects named-pipe EOF
  disposition RestoredFirmware
  observed FF/FF
  restoreAttempted=True
  journalRetained=False
  independent final EC FF/FF
  measured parent-observed recovery interval 0.458 s
  post-test EC FF/FF
~~~

Fresh service-log evidence recorded only:

~~~text
PREPARE generation 1
WRITE_INTENT generation 2 target 30/30
OWNER LOSS named-pipe EOF -> RestoredFirmware
~~~

for the killed controller. The harness explicitly rejects any fresh
`WATCHDOG COMMIT ACK` for that PID, so the PASS proves that the crash remained
inside the intended durable WRITE_ARMED window rather than silently advancing
to OWNED.

M5D therefore closes the **controller-death** branch of the real
post-WMI/pre-Commit WRITE_ARMED ambiguity window.

This does not yet prove a simultaneous watchdog + controller loss while the
journal is WRITE_ARMED. A separate M5E-style double-death/startup-recovery gate
is still required before calling the entire awake transactional crash matrix
complete.

`WatchdogRecoveryValidated` remains false and automatic/adaptive policy
remains OFF.


## M5E - WRITE_ARMED post-WMI / pre-Commit double death

M5D proved the live-watchdog controller-death branch of the real
post-WMI/pre-Commit WRITE_ARMED window. M5E extends that exact physical boundary
to loss of **both** original recovery domains.

The M5E parent deliberately reuses the already-qualified M5D child instead of
adding another fan-write path:

~~~text
PREPARE generation 1
  -> durable WRITE_INTENT generation 2 / Pending 30/30
  -> real HP WMI SetFanLevel(30/30)
  -> EC 30/30 acknowledgement
  -> both physical tachometers acknowledge
  -> M5D qualification hook holds before watchdog Commit
~~~

Immediately before fault injection, M5E independently requires:

~~~text
READY stage = WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT
exact target HP-8C40-9D0R1LA-F18
exact controller PID + creation time
exact original watchdog PID + creation time
journal schema 2
phase WriteArmed
generation 2
PreviousOwned null
Pending 30/30
Owned null
independent EC 30/30
delayed safety fallback alive
~~~

The fault request ordering is fixed:

~~~text
watchdog Kill()
controller Kill()
~~~

with no sleep, EC read, SCM query or journal read between the two requests and
a default maximum request interval of 50 ms.

The temporary SCM first restart remains 5000 ms so the parent can prove a
pre-restart causal window where:

~~~text
both original processes are dead
replacement service is still absent
EC is still 30/30
exact durable WRITE_ARMED generation-2 journal is still present
Commit never occurred
~~~

Only then may a distinct SCM-restarted LocalSystem watchdog satisfy PASS by
loading the retained WRITE_ARMED journal, recognizing the physically observed
30/30 as the permitted Pending target, executing the already-qualified
firmware restore, verifying FF/FF and deleting the journal.

A dedicated delayed M5E fallback is armed before the write-capable child. It is
restricted to the exact target and exact generation-2 WRITE_ARMED pending 30/30
journal. It may terminate only the exact journal-bound controller and may only
start/restart the already-qualified M4 recovery service. It has no ordinary
fan-target authority, no direct HP/WMI restore authority and no journal-deletion
authority. If it takes over, the hardware may be safely recovered but the run
cannot count as M5E PASS.

Cleanup deliberately keeps that fallback alive until FF/FF plus journal absence
have been independently proven.

### M5E preparation CI result

M5E preparation is **CODE/CI PASS / PHYSICAL PENDING**.

The final preparation head
`f4bf35d7fb51bc53ee5cde2f8ec1f9cea8122852` passed GitHub Actions
**#631** (run `36512183955`) on 2026-09-29.

That run passed:

- PowerShell syntax validation;
- historical Gate E/F/G0/G1/G2 invariants;
- HP 8C40 M5A/M5B/M5C/M5D invariants;
- the new M5E invariant under PowerShell Core;
- the complete M5 invariant set under Windows PowerShell 5.1;
- warnings-as-errors solution build;
- M0, Gate B/C and HP 8C40 M2/M3/M4 self-tests;
- SafetyGate and FanControlCoordinator self-tests;
- HP BIOS-contract and HP backend self-tests.

No M5E physical fault injection has yet been executed.

`WatchdogRecoveryValidated` remains false and automatic/adaptive policy remains
OFF until the remaining physical/lifecycle qualification gates are complete.


### M5E physical attempt 1 - safe no-write admission anomaly

The first physical M5E attempt on 2026-09-29 was **not** a destructive-gate
attempt and does not count as PASS or FAIL of the double-death recovery.

Observed sequence:

~~~text
local M5E invariant PASS
initial durable journal ABSENT
warnings-as-errors build PASS
M5A/M5B/M5C/M5D/M5E invariants PASS
M4 / Gate C / SafetyGate / coordinator / backend regressions PASS
parent baseline EC FF/FF
M4 service Ready as LocalSystem / Session 0
original watchdog PID 8664
delayed M5E fallback PID 20500, 120 s

M5D child initial control evidence:
  setpoint FF/FF
  MaxFan 0x00
  FanSwitch 0x00
  RPM 2582/2403

subsequent backend admission read:
  setpoint 255/11

result:
  FanControlOwnershipConflictException
  no PREPARE
  no WRITE_INTENT
  no WMI fan command
  no durable journal
  no fault injection
  final EC FF/FF
  delayed fallback cancelled only after safety proof
  M4 baseline restored Manual/stopped
~~~

The isolated `255/11` sample is an asymmetric admission anomaly between
surrounding FF/FF evidence. It is not accepted as firmware-auto state and is not
silently converted into Custom authority.

The qualification-only M5D child is hardened without changing the production
backend admission policy: when an explicit no-write setpoint ownership conflict
occurs, the child may retry admission at most three times **only after** a
bounded read-only confirmation observes two consecutive FF/FF samples. A stable
equal non-FF pair remains an immediate external-owner refusal; unresolved
asymmetry remains fail-closed. The retry performs no compensating HP restore and
no fan write.

This hardening is intended only to prevent a transient/torn ownership sample
from making the M5D/M5E qualification harness flaky. M5E physical execution
remains pending until the hardening passes CI.


## M5E physical result - WRITE_ARMED double death

**Accepted physical PASS on real hardware, 2026-09-29**, on exact target
`HP-8C40-9D0R1LA-F18`.

The destructive run reached the intended real post-WMI/pre-Commit boundary:

~~~text
original watchdog:
  PID 17852
  LocalSystem / Session 0
  exact target HP-8C40-9D0R1LA-F18

controller:
  PID 16568
  startTicks 639263083112036599
  READY stage WRITE_ARMED_POST_WMI_EC_TACH_ACK_PRE_COMMIT
  EC 30/30
  RPM 1998/2041
  guards MaxFan=0x00 / FanSwitch=0x00

durable journal before fault:
  schema 2
  phase WriteArmed
  generation 2
  PreviousOwned null
  Pending 30/30
  Owned null

independent pre-kill EC:
  30/30
~~~

Fault injection was issued in the required order with no probe/sleep between
requests:

~~~text
watchdog Kill()
controller Kill()
measured issue delta = 0.815 ms
~~~

The parent then established the causal pre-restart window before the configured
5 s SCM restart:

~~~text
both original processes dead
service absent
elapsed = 2811 ms
EC still 30/30
exact WRITE_ARMED generation-2 journal still retained
~~~

A distinct LocalSystem replacement watchdog then started:

~~~text
replacement PID 7100
RecoveryDisposition = RestoredFirmware
detail = VFC-owned setpoint 30/30 restored to FF/FF and verified before journal deletion
journalRetained = False
~~~

The fresh service log contains PREPARE generation 1 and WRITE_INTENT generation
2 for controller PID 16568, followed by replacement startup recovery. It
contains no COMMIT for that controller, so the crash remained inside the
required WRITE_ARMED pre-Commit window.

The parent harness emitted a false FAIL after recovery because its first
independent post-recovery 0x34/0x35 read returned the asymmetric pair
`255/1`. This is the same class of transient/torn EC observation already seen
during qualification admission. The watchdog had already published
`RestoredFirmware` after verifying FF/FF and deleting the journal, and the
later independent post-test probe returned `255/255`. The delayed emergency
fallback was cancelled only after journal absence plus that independent FF/FF
proof, and the ordinary M4 Manual/stopped baseline was restored.

Therefore the hardware/recovery boundary itself is complete; the terminal
failure was a parent-shell single-sample verification false negative, not a
recovery failure.

The harness was subsequently hardened so final and cleanup ownership proof are
read-only, bounded and require two consecutive independent FF/FF samples. A
single unexpected pair is provisional; the same unexpected pair must repeat
before terminal failure. No additional restore or fan-write authority was
added. Commit
`b9d0a0e4b103856e392e3416b091de097202fe3d` passed complete GitHub
Actions **#635** (run `36622635895`).

As with the earlier M5B post-evidence harness correction, a second destructive
double-death run is not required merely to reproduce a parent verification
check after the full causal physical boundary has already been captured.

M5E is therefore **CODE/CI/PHYSICAL PASS**.

The awake transactional crash matrix is now complete:

~~~text
M5A controller death in OWNED             PASS
M5B watchdog death in OWNED               PASS
M5C watchdog + controller death in OWNED  PASS
M5D controller death in WRITE_ARMED       PASS
M5E watchdog + controller death in WRITE_ARMED PASS
~~~

`WatchdogRecoveryValidated` deliberately remained false at M5 closure. M6 Modern
Standby, M7 hibernation and M8 representative-load/thermal-preemption have since
closed physically. The current remaining boundary is a separate explicit post-M8
production watchdog/race promotion gate. Automatic/adaptive policy remains OFF.
