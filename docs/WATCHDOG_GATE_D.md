# Watchdog Gate D - real lease integration and forced GUI kill

Status: implementation complete; physical validation pending.

Gate D is the first gate that connects the durable watchdog lease to the real
HP 88F8 SetFanLevel dispatch boundary.

Automatic fan policy remains OFF. The hardware test uses only one explicit,
bounded 30/30 command under the same light-load envelope used by the previous
hardware gates.

## Production transaction boundary

For a real target change, the protected HP backend now performs:

~~~text
existing EC ownership/control checks
        |
        v
watchdog PREPARED/OWNED
        |
        v
WRITE_INTENT(target)
        |
        | durable journal flush + atomic write-through replace
        v
watchdog ACK WRITE_ARMED
        |
        v
EC ownership/control re-check
        |
        v
mark writeAttempted = true
        |
        v
HP WMI SetFanLevel(target)
        |
        v
EC setpoint ACK
        |
        v
dual-tach ACK
        |
        v
COMMIT(target)
        |
        v
watchdog OWNED
~~~

The post-WriteIntent EC re-check is mandatory. The IPC + durable flush widens
the interval between the original pre-dispatch check and WMI, so the backend
must still preserve an external controller that appears during that interval.

## No-write rollback

Gate D adds two lease operations that are safe only before WMI dispatch:

- CancelPrepared: deletes PREPARED because no VFC hardware write was authorized.
- AbortWriteIntent: rolls WRITE_ARMED back only if EC still proves the previous
  safe state. For a first command this requires FF/FF. During a target change it
  requires the previously OWNED target.

If the observed EC state does not match that proof, AbortWriteIntent refuses and
the durable WRITE_ARMED journal remains armed. The controller never clears an
unknown external fixed override merely to simplify its bookkeeping.

## Heartbeat coupling

Heartbeat is not a blind timer. The HP backend sends Heartbeat only after its
normal continuous status path has freshly verified:

- expected EC ownership;
- Max Fan inactive;
- fan switch valid;
- both tachometers valid when an owned setpoint exists.

If status/ownership validation stops progressing, heartbeat also stops and the
service lease expires independently.

## Persistent LocalSystem service

Gate D adds the persistent Windows service:

~~~text
VictusFanControlWatchdog
~~~

The service:

- requires LocalSystem and Session 0;
- revalidates the exact HP 88F8 target on startup;
- runs startup journal recovery before accepting a controller;
- keeps the durable lease under ProgramData;
- hosts one controller pipe session at a time;
- checks lease deadlines every 250 ms;
- binds every lease mutation to the kernel-verified controller PID + process
  creation time;
- holds a real process handle for the verified controller and also maintains an
  independent service-side process monitor;
- recovers immediately on proven controller-process death;
- on pipe transport loss while that exact controller is still alive, retains
  the durable lease and permits a fresh kernel-verified reconnect instead of
  clearing ownership underneath a still-running controller;
- if the live controller does not reconnect/progress, the existing
  WRITE_ARMED/OWNED/RESTORING deadlines remain fail-closed recovery paths;
- performs service-stop/update recovery before exiting; Gate E/F still own the
  stronger watchdog-stop/restart fencing validation;
- never exposes an ordinary 14..50 fan-write operation.

SCM recovery is configured to restart the service after unexpected failure.

## IPC security

The production pipe uses an explicit protected DACL:

- LocalSystem: full control;
- Builtin Administrators: read/write;
- NETWORK SID: explicit deny.

The GUI already requires elevation. After connection, the service obtains the
client PID from the kernel named-pipe handle and checks process creation time
against the Hello identity before accepting lease commands. Every mutating
lease message is then checked against that same durable controller identity, so
a second Administrator process cannot take over a session merely by knowing its
session GUID/generation.

The ProgramData watchdog tree is also hardened to LocalSystem + local
Administrators only. The installer intentionally never deletes lease.json.

## Controller restore

A normal live-controller restore does not depend on watchdog IPC succeeding:

1. controller asks RestoreBegin when possible;
2. controller always executes its validated local FF,FF -> LegacyDefault path;
3. after local FF/FF verification, controller asks Release;
4. Release independently normalizes FF,FF -> LegacyDefault inside the service
   before the service deletes the durable journal.

If service IPC is unavailable, the alive controller still restores HP firmware.
Any surviving durable journal is recovered by the service when it returns.

## Physical Gate D test

Run elevated:

~~~powershell
git pull
.\scripts\test-watchdog-gate-d.ps1
~~~

Keep OMEN Gaming Hub open with the normal CPU undervolt. Close OmenMon,
OmenMon-Reborn and any normal VictusFanControl GUI. Do not run a workload.

The wrapper requires:

~~~text
UNDERVOLT-OK
GATED30
~~~

The test then:

1. installs and starts the persistent LocalSystem watchdog;
2. verifies its startup Ready state;
3. arms an independent 120-second emergency fallback before any 30/30 write;
4. launches the explicit Gate D GUI mode;
5. requires READY only after EC + dual-tach ACK + watchdog OWNED Commit;
6. checks the durable journal is OWNED 30/30;
7. force-kills the exact GUI PID;
8. does not invoke the parent CLI restore path;
9. waits for the watchdog service to recover and delete its journal;
10. independently probes EC and requires FF/FF;
11. requires the same watchdog service PID throughout this Gate D test;
12. requires service log evidence of RestoredFirmware from either the pipe
    owner-loss path or the independent controller-process monitor;
13. cancels the emergency fallback only after journal deletion + independent
    FF/FF verification;
14. asks the user to confirm OMEN Gaming Hub undervolt is unchanged.

The emergency fallback is a last-resort hardware safety net. It does not count
as a Gate D PASS. A PASS requires recovery by the persistent watchdog itself.

## PASS boundary

Gate D passes only when real hardware proves:

~~~text
watchdog service Ready
        ->
PREPARED
        ->
durable WRITE_ARMED
        ->
real WMI 30/30
        ->
EC + dual-tach ACK
        ->
durable OWNED 30/30
        ->
exact GUI forced kill
        ->
watchdog detects exact GUI process death
        ->
FF,FF -> LegacyDefault
        ->
EC FF/FF
        ->
journal deleted
~~~

with no parent-shell HP restore and with OMEN Gaming Hub undervolt unchanged.

Gate E/F remain responsible for watchdog-process death and service-restart /
double-failure recovery. Gate G remains suspend/resume validation with the full
watchdog stack.
