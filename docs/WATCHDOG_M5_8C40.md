# HP 8C40 watchdog M5 - failure-domain qualification

Status: **M5A CODE PREPARED / CI PENDING / PHYSICAL PENDING.**
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

Even after a future M5A physical PASS:

- M5B watchdog-death recovery remains pending;
- M5C double-death recovery remains pending;
- Modern Standby proactive release/reacquisition remains pending;
- hibernation and in-flight race gates remain pending;
- `WatchdogRecoveryValidated` remains false;
- automatic/adaptive policy remains OFF.
