# Watchdog Gate E - watchdog process death while GUI owns Custom

Status: **PASSED on real hardware, 2026-09-25.**

Gate E validates the opposite failure domain from Gate D.

Gate D proved that the independent LocalSystem watchdog can restore HP firmware
when the GUI/controller dies. Gate E proves that the still-live GUI/controller
can restore HP firmware when the watchdog process itself dies.

Automatic fan policy remains OFF. The hardware test uses only one explicit,
bounded 30/30 command under the same light-load envelope used by the earlier
hardware gates.

## Required failure sequence

~~~text
watchdog service Ready
        ->
GUI/controller PREPARED
        ->
durable WRITE_ARMED
        ->
real WMI 30/30
        ->
EC + dual-tach ACK
        ->
durable OWNED 30/30
        ->
force-kill watchdog service process
        ->
GUI heartbeat / backend status probe loses watchdog IPC
        ->
FanControlCoordinator safety supervisor requests handoff
        ->
live GUI executes local FF,FF -> LegacyDefault
        ->
GUI verifies EC FF/FF
        ->
SCM later restarts watchdog
        ->
startup recovery consumes retained OWNED journal
        ->
service normalizes firmware restore
        ->
journal deleted
        ->
service Ready
~~~

The parent PowerShell never invokes the HP restore CLI.

## Why SCM restart is temporarily delayed

Production SCM recovery is 1 s / 5 s / 10 s.

That is appropriate for normal use, but it makes Gate E causality ambiguous:
the service could restart and consume the durable journal before the GUI's local
fallback has time to prove itself.

The Gate E harness therefore temporarily changes the first SCM restart window to
25 seconds by default. This is not a production configuration change. It creates
an isolated proof window in which:

- the watchdog process is definitely dead;
- the GUI remains alive;
- the durable OWNED journal still exists;
- only the live controller can perform the local HP restore;
- the parent shell issues no HP restore.

After the test, the service is reinstalled with the production 1 s / 5 s / 10 s
SCM recovery policy so the failure counter and actions return to the normal
baseline.

An independent 120-second delayed emergency fallback is armed before 30/30 and
remains a last-resort safety net. If it fires, Gate E does not pass.

## GUI evidence

Gate E adds an explicit app mode:

~~~text
--gate-e-watchdog-death-test
--gate-e-test-token 88F8-GATEE30
~~~

The GUI uses the same production watchdog-protected backend transaction as Gate
D.

After OWNED 30/30, the normal telemetry-driven safety supervisor continuously
calls backend status. Heartbeat is coupled to that fresh ownership/feedback
probe.

If watchdog IPC fails, the coordinator transitions:

~~~text
Custom
  -> Restoring
     reason = Backend control-dependency probe failed during custom authority:
              WATCHDOG_IPC_LOSS during Probe: ...
  -> Firmware
~~~

The watchdog transport loss is explicitly classified. An unrelated EC/backend
failure may still trigger a safe firmware handoff, but it is recorded as an
unrelated restore and cannot satisfy the Gate E PASS condition.

The backend's RestoreWithWatchdog path deliberately treats watchdog IPC failure
as degraded lease handoff, not as a reason to skip the local HP restore.

Only after the local backend has completed and verified its
FF,FF -> LegacyDefault path does the Gate E app write:

~~~text
%LOCALAPPDATA%\VictusFanControl\gate-e.local-restore
~~~

The harness requires that marker while the old watchdog PID is dead and before a
new service PID exists.

## Retained journal is expected

Because the watchdog process is dead, the GUI cannot successfully complete
RestoreBegin / Release over IPC.

Therefore the durable service journal is expected to remain after the GUI has
already locally restored FF/FF.

This is intentional. It is the evidence SCM restart consumes.

On restart, the service sees the retained OWNED record. Even though EC already
reads FF/FF, the watchdog completes/normalizes the validated
FF,FF -> LegacyDefault restore before deleting the journal.

The expected startup disposition is:

~~~text
RestoredFirmware
~~~

## Physical test

Run from elevated PowerShell:

~~~powershell
git pull
.\scripts\test-watchdog-gate-e.ps1
~~~

Keep OMEN Gaming Hub open with the normal CPU undervolt. Close OmenMon,
OmenMon-Reborn and any ordinary VictusFanControl GUI. Do not run a workload.

The wrapper requires:

~~~text
UNDERVOLT-OK
GATEE30
~~~

The test:

1. builds and runs the existing Gate C / SafetyGate / coordinator / HP-backend
   synthetic regressions;
2. confirms a firmware-owned FF/FF baseline under light load;
3. installs the persistent LocalSystem watchdog;
4. temporarily configures an isolated 25-second SCM restart delay;
5. starts the watchdog and requires Ready / Session 0 / LocalSystem;
6. arms the independent delayed emergency fallback;
7. launches the Gate E GUI mode;
8. requires a READY marker proving the production backend already completed
   real EC + dual-tach acknowledgement and watchdog Commit, plus durable OWNED
   30/30 bound to the exact GUI PID + creation time;
9. performs no out-of-band EC probe or artificial dwell between READY and the
   watchdog kill boundary;
10. force-kills only the watchdog service PID;
11. requires the GUI local-restore marker to be classified as
    WATCHDOG_IPC_LOSS before any replacement service PID appears;
12. independently confirms EC FF/FF while the service is still absent;
13. requires the durable OWNED journal to still exist at that point;
14. waits for SCM to restart the service;
15. requires fresh service status Ready with startup
    RecoveryDisposition=RestoredFirmware;
16. requires lease.json to disappear only after that restart recovery;
17. independently confirms final EC FF/FF;
18. cancels the emergency fallback only after firmware safety is proven;
19. reinstalls the production watchdog service so SCM recovery returns to
    1 s / 5 s / 10 s and the service failure history is reset;
20. asks the user to confirm OMEN Gaming Hub undervolt is unchanged.

## Physical result

**PASSED on real hardware, 2026-09-25**, on
`c14c6b6fc77b54cebb36ab2a0b3fb90c96163248` after Windows CI #247 passed the
PowerShell syntax check, Gate E contention/causality invariants, build, Gate B/C,
SafetyGate, FanControlCoordinator, BIOS-contract and HP-backend self-tests.

Observed sequence:

~~~text
production postcheck:
  watchdog PID 28028 Ready / LocalSystem / Session 0
  no durable journal
  EC 255/255
  SCM recovery 1 s / 5 s / 10 s
  OMEN undervolt SAME

Gate E:
  watchdog PID 16724 Ready
  GUI PID 7544
  durable OWNED generation 3, target 30/30
  READY ack=backend-ec+tachs+watchdog-owned
  force-kill watchdog PID 16724
  live GUI -> LOCAL-RESTORE
    reason=WATCHDOG_IPC_LOSS during Probe: Pipe is broken
  EC -> 255/255 before replacement watchdog exists
  retained journal consumed by restarted watchdog PID 14184
  startup recovery=RestoredFirmware
  final EC 255/255
  post-test EC 255/255
  emergency fallback cancelled without firing
  production watchdog reinstalled as PID 492
  production SCM recovery re-verified 1 s / 5 s / 10 s
  OMEN undervolt SAME
~~~

The parent PowerShell issued no HP restore command. This closes Gate E.

## PASS boundary

Gate E passes only when real hardware proves all of the following:

~~~text
OWNED 30/30
       ->
watchdog service PID dies
       ->
no replacement watchdog process exists yet
       ->
live GUI reports watchdog-loss local restore
       ->
independent EC FF/FF
       ->
durable journal still retained
       ->
SCM starts a NEW watchdog PID
       ->
startup RestoredFirmware
       ->
journal deleted
       ->
service Ready
~~~

with no parent-shell HP restore and with the emergency fallback not firing.

Gate F remains responsible for the double-failure case where controller and
watchdog die close together. Gate G remains full suspend/resume validation with
the watchdog stack active.
