# HP 8C40 watchdog M5 - failure-domain qualification

Status: **M5A CODE/CI/PHYSICAL PASS. M5B/M5C PENDING.**
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
