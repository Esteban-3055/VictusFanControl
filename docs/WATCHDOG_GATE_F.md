# Watchdog Gate F - double failure / durable-journal recovery

Status: implementation in progress. **F1 (OWNED double death) PASSED on real
hardware on 2026-09-25.** F2 (WRITE_ARMED after real write/ACK but before
Commit) is now implemented and awaiting physical validation. Gate F is not
closed until F2 also passes.

Automatic fan policy remains OFF.

## Objective

Gate D proved controller death while the watchdog remains alive.
Gate E proved watchdog death while the controller remains alive.

Gate F removes both original recovery domains nearly simultaneously and requires
the replacement LocalSystem watchdog started by SCM to recover only from durable
lease evidence plus the observed HP 88F8 fixed setpoint.

The parent test shell is not a recovery authority. It never issues the HP fan
restore command. A delayed independent emergency fallback remains armed as a
last-resort safety mechanism; if it reaches its delay, the subgate cannot pass.

## Why Gate F is split

### F1 - durable OWNED recovery

F1 starts from the already validated production transaction:

~~~text
firmware FF/FF
  -> PREPARED
  -> durable WRITE_ARMED
  -> WMI 30/30
  -> EC + dual-tach ACK
  -> Commit
  -> durable OWNED 30/30
~~~

It then destroys both original failure domains:

~~~text
force-kill original watchdog
  -> immediately force-kill exact GUI
  -> both originals dead
  -> SCM starts replacement watchdog
  -> startup reads durable OWNED journal
  -> observed EC must be lease-compatible
  -> FF,FF -> LegacyDefault
  -> verify EC FF/FF
  -> delete journal
  -> Ready / RestoredFirmware
~~~

F1 validates the stable OWNED double-failure path.

### F1 physical result

**PASSED on real hardware, 2026-09-25**, on
`caf3d90cd3529226ae9612ed661ba44e18496e79`.

Observed evidence:

~~~text
production precheck:
  watchdog PID 492 Ready / LocalSystem / Session 0
  no durable journal
  EC 255/255
  SCM recovery 1 s / 5 s / 10 s
  OGH undervolt SAME

F1:
  production watchdog PID 10508
  GUI PID 16076
  durable OWNED generation 3, target 30/30
  READY ack=backend-ec+tachs+watchdog-owned
  emergency fallback PID 25396 proven alive
  watchdog kill issued first
  GUI kill issued 1.071 ms later
  both original processes confirmed dead
  no live-GUI restore-start marker
  SCM replacement watchdog PID 13288
  startup recovery=RestoredFirmware
  detail=VFC-owned setpoint 30/30 restored to FF/FF
  final EC 255/255
  post-test EC 255/255
  emergency fallback cancelled without firing
  production watchdog reinstalled as PID 27172
  production SCM recovery re-verified 1 s / 5 s / 10 s
  OGH undervolt SAME
~~~

The parent PowerShell issued no HP fan restore. The measured 1.071 ms kill-call
interval is well inside the 50 ms causal bound. F1 is therefore closed.

### F2 - WRITE_ARMED after write, before Commit

F2 will validate the more difficult transaction window:

~~~text
durable WRITE_ARMED pending=30/30
  -> real WMI 30/30
  -> EC + dual-tach ACK
  -> STOP before Commit
  -> kill watchdog + controller
  -> SCM restart
  -> recover WRITE_ARMED journal
~~~

F2 is implemented with a test-only
`GateF2CommitHoldWatchdogLeaseClient` wrapper around
`IFanControlWatchdogLeaseClient`. WriteIntent and every normal lease operation
are delegated to the real named-pipe client. When the production backend reaches
`CommitAsync`, the wrapper writes and flushes a dedicated READY marker, then
holds non-cancellably without forwarding Commit. Because production backend
ordering is WMI -> EC ACK -> dual-tach ACK -> Commit, this creates the required
real-hardware boundary without inserting a Gate-F branch into the HP backend.

## F1 causal requirements

F1 uses the real production SCM recovery configuration: restart after
1 s / 5 s / 10 s. Unlike Gate E, there is no artificial 25-second first-restart
delay because SCM restart is the recovery path being validated.

The fault injector opens and validates both exact process identities before the
fault. It issues:

~~~text
watchdog Process.Kill()
GUI Process.Kill()
~~~

with no sleep, EC probe or SCM query between the two calls. Watchdog is killed
first so its 250 ms controller-process monitor cannot win the race and perform a
Gate-D-style restore before the second kill. The harness measures the interval
between kill-call issue points and requires it to remain within the configured
50 ms causal bound.

The GUI publishes a synchronous
`gate-f1.local-restore-started` marker on the transition
`Custom -> Restoring`. The event is raised before the backend restore starts.
If this marker exists, F1 fails even if the machine later returns safely to
FF/FF: recovery can no longer be attributed solely to the replacement watchdog.

## F1 READY evidence

The dedicated app mode is:

~~~text
--gate-f1-owned-double-death-test
--gate-f1-test-token 88F8-GATEF1-30
~~~

READY is published only after the ordinary production backend has returned from
the 30/30 command:

~~~text
READY|...|authority=Custom|cpu=30|gpu=30|ack=backend-ec+tachs+watchdog-owned
~~~

The harness additionally requires the durable journal to be OWNED 30/30 and
bound to the exact GUI PID + process creation time.

No independent EC snapshot is opened between READY and the double-kill boundary.
The backend has already acknowledged the physical write. This preserves the
EC-arbitration rule established during Gates B/E.

## Durable startup rule under test

From WRITE_ARMED onward the replacement watchdog may restore only if the
observed setpoint is firmware-owned FF/FF or is one of the durable lease's
allowed previous/pending/owned values.

If the EC contains another fixed pair, recovery must return
`OwnershipAmbiguous` with:

- no restore attempt;
- durable journal retained;
- service not Ready for a new lease.

Gate C already exercises this rule synthetically. Gate F keeps that regression
mandatory but does not deliberately create an unknown external fixed override
on real hardware.

## F1 physical PASS boundary

F1 passes only if all of the following are observed:

~~~text
production baseline Ready / LocalSystem / Session 0
SCM production recovery = 1 s / 5 s / 10 s
EC baseline FF/FF
durable OWNED 30/30
exact GUI PID + creation time
exact watchdog PID + creation time
no GUI restore-started marker
watchdog kill issued first
GUI kill issued within <= 50 ms
both original processes confirmed dead
no parent-shell HP restore
replacement watchdog process
startup RecoveryDisposition=RestoredFirmware
journal cleared only after recovery
independent final EC FF/FF
emergency fallback still pending, then cancelled
production watchdog baseline reinstalled
SCM production policy re-verified
OMEN Gaming Hub undervolt SAME
~~~

A safe final state is not by itself a PASS. If the GUI begins local restore,
the emergency fallback reaches its delay, ownership becomes ambiguous, or the
replacement service reports a recovery other than RestoredFirmware, the test
fails formally even if later cleanup leaves the machine in firmware mode.

## Implementation files

- `src/VictusFanControl.App/Program.cs` - explicit F1 opt-in/token.
- `src/VictusFanControl.App/MainForm.cs` - production 30/30 transaction,
  READY evidence and synchronous local-restore-started marker.
- `scripts/test-watchdog-gate-f1.ps1` - F1 physical harness.
- `src/VictusFanControl.App/GateF2CommitHoldWatchdogLeaseClient.cs` - test-only
  pre-Commit hold after the production backend has completed WMI + EC/tach ACK.
- `scripts/test-watchdog-gate-f2.ps1` - F2 physical harness.
- `scripts/test-watchdog-gate-f-invariants.ps1` - F1/F2 static causal/safety
  invariants executed in Windows CI.
- `scripts/watchdog-gate-b-failsafe.ps1` - independent delayed emergency
  fallback reused unchanged.
- `WatchdogLeaseManager` / `GateDWorker` - unchanged production recovery
  implementation under test.

## Staging rule

F1 has satisfied its staging boundary: Windows CI was green, the real-hardware
double death passed, post-test EC remained FF/FF, the durable journal was
cleared by restart recovery, the production watchdog returned Ready with
1 s / 5 s / 10 s SCM recovery, and OGH undervolt remained unchanged.

F2 implementation is complete. Its CI must be green before the first hardware
run. Gate F remains open until the F2 physical run also passes.
