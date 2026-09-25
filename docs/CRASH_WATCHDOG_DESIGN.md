# Independent crash-watchdog / lease design

Status: Gates A, B, D and E have passed on real hardware under LocalSystem, and Gate C passed synthetic Windows CI. Gate D physically proves controller death -> independent service restore; Gate E physically proves watchdog death -> live-controller local restore followed by durable-journal recovery. Gates F-G remain.

## 1. Hardware fact that drives the design

Forced-kill testing on the validated HP 88F8 target established that EC 0x63 is
not an independent crash failsafe in the OMEN Gaming Hub coexistence
configuration.

After VictusFanControl reached validated Custom 30/30 and the GUI process was
force-killed:

- EC 0x34/0x35 remained 30/30;
- both physical fans remained near 3000 RPM;
- EC 0x63 counted down;
- another HP/OMEN-side component refreshed 0x63 from 211 -> 239 and later
  210 -> 239;
- natural FF/FF release was not observed;
- explicit `FF,FF -> LegacyDefault` returned EC to FF/FF;
- OMEN Gaming Hub undervolt remained unchanged.

Therefore the watchdog must be independent of EC 0x63 and independent of the GUI
process.

## 2. Chosen production architecture

Use a dedicated Windows service as the watchdog failure domain:

```text
VictusFanControl.App / controller
        |
        | authenticated local IPC
        | lease + operation protocol
        v
VictusFanControl.Watchdog  (Windows service)
        |
        | failure-only authority
        | never sets normal 14..50 targets
        v
Hp88F8 restore primitive
FF,FF -> LegacyDefault -> EC FF/FF verification
```

The service is deliberately narrower than the GUI/backend:

- it never runs the fan curve;
- it never requests ordinary fan levels;
- it never writes EC 0x62 or 0x63;
- it does not need Intel MSR or NVML;
- its only hardware write authority is the already validated HP-auto restore.

A one-shot helper process is useful for tests but is not the final design because
it has no Service Control Manager recovery, no durable restart state and is tied
more closely to an interactive session.

## 3. Required pre-implementation hardware gate: service context

The current GUI runs elevated. The watchdog will run in Session 0, so WMI/PawnIO
access must not be assumed.

Before any lease integration:

1. build a read-only service probe;
2. run the service under the least-privileged practical account;
3. verify exact target fingerprint;
4. verify HP WMI read access;
5. verify PawnIO + LpcACPIEC module loading;
6. read EC 0x34/0x35 and tachometers;
7. stop/restart the service several times.

Physical Gate A result:

- LocalService started correctly in Session 0 and matched the target, but access
  to the shared Global\Access_EC mutex was denied before the EC/WMI probe could
  continue.
- LocalSystem passed three complete Session 0 start/stop cycles, including
  PawnIO EC reads and HP WMI GetFanLevel.
- EC remained FF/FF before, during and after the test.

The production watchdog hardware service will therefore use LocalSystem on this
target. This choice is compensated by keeping the service surface deliberately
small, using a service SID, strict IPC ACL, no arbitrary EC/WMI API and no
ordinary 14..50 fan commands. We are not modifying the ACL of the shared
Global\Access_EC mutex solely to make LocalService work.

A later Gate B arming attempt exposed a second EC-arbitration rule: while Custom
authority is active, diagnostic/test code must not open extra full EC
control-state readers outside the production backend path. The backend already
serializes command/status operations internally and does not return from a fan
command until EC setpoint plus dual-tach acknowledgement succeeds. An
out-of-band full EC snapshot bypasses that IO gate and competes on
Global\Access_EC; the safety supervisor correctly treats a failed ownership
probe as unsafe and hands control back to firmware. Independent EC verification
should therefore happen before Custom admission or after the controller process
has relinquished/terminated, not between a validated backend command and the
continuous ownership supervisor.

## 4. IPC choice

Use one persistent local named pipe connection between controller and watchdog.

Recommended properties:

- full-duplex;
- asynchronous;
- local-machine only;
- reject remote clients;
- explicit pipe ACL;
- service side accepts SYSTEM/Administrators only for the first development
  version;
- server obtains the real pipe client PID;
- service opens a process handle to that PID;
- claimed PID/process-start identity in protocol must match the kernel-observed
  process;
- maximum one active control lease.

Do not use a file timestamp as the live lease. Files are for crash persistence,
not heartbeat transport.

## 5. Controller identity

PID alone is insufficient because PIDs are reused.

At lease acquisition the watchdog should bind to:

- actual pipe client PID;
- process creation time;
- an open process handle with SYNCHRONIZE / query rights;
- service-generated random lease/session identifier.

Keeping the process handle open means process termination can be detected from
the process object itself rather than by periodically asking whether that PID
still exists.

## 6. Two different liveness failures

The watchdog must distinguish:

### 6.1 Process death

Owner process handle becomes signaled.

If a hardware write may have occurred, restore immediately. Do not wait for the
heartbeat timeout.

A pipe transport break by itself is **not** equivalent to proven process death
once WRITE_INTENT has been acknowledged. If the exact kernel-verified
PID/creation-time owner is still alive, immediate restore+lease deletion could
race that live controller resuming into SetFanLevel. In Gate D, transport loss
therefore retains the durable lease, allows only the same verified controller to
reconnect, and leaves process-death plus state deadlines as the fail-closed
recovery mechanisms.

### 6.2 Controller hang

The process is alive but the safety/control loop no longer makes progress.

Use an explicit renewable heartbeat. Heartbeat must be coupled to successful
controller/safety progress; a blind independent timer that keeps running while
the controller is deadlocked would defeat the watchdog.

The service records its own receive time. Do not trust a client-provided wall
clock.

## 7. Lease state machine

A simple boolean "armed" flag is not sufficient because HP WMI may apply a
SetFanLevel write even if the caller subsequently sees an error, and the process
can die between any two instructions.

Required states:

```text
IDLE
  |
  | Prepare
  v
PREPARED
  |
  | WriteIntent(target) durably acknowledged
  v
WRITE_ARMED
  |
  | SetFanLevel may now occur
  | EC+tachs ACK
  | Commit(target)
  v
OWNED
  |
  | new WriteIntent(nextTarget)
  +-----------------------> WRITE_ARMED
  |
  | normal firmware handoff begins
  v
RESTORING
  |
  | EC FF/FF verified
  | Release
  v
IDLE
```

### PREPARED

No fan write is permitted yet. If the controller dies here, the service clears
the lease without touching hardware.

### WRITE_ARMED

This state is critical. It must be entered and durably recorded **before** the
backend dispatches SetFanLevel.

If the controller dies after the service acknowledges WriteIntent, the service
must assume the WMI write may have taken effect even if Commit was never seen.

### OWNED

The last command was acknowledged by the existing production backend. Normal
heartbeat supervision applies.

### RESTORING

The controller is performing its normal validated restore. The watchdog remains
armed until FF/FF is verified. If the controller dies during restore, the
watchdog takes over.

## 8. Exact integration point with Hp88F8FanControlBackend

The watchdog cannot be bolted on after ApplyAsync. The first crash-sensitive
boundary is already known in the backend:

```text
...
pre-dispatch EC validation
cancellation check
WRITE_INTENT must be durable here
SetFanLevel(target)
wait EC target
wait dual tach ACK
COMMIT target
...
```

The watchdog's WriteIntent acknowledgement must happen before the line where the
backend currently marks `writeAttempted = true` and calls SetFanLevel.

Gate D must preserve the backend's existing external-ownership race protection.
The IPC/durable-journal round trip necessarily widens the interval between an EC
ownership check and WMI dispatch, so the integration must not simply insert
WriteIntent and remove the final ownership validation. The exact ordering and
any post-WriteIntent ownership recheck must be tested explicitly while keeping
all EC access serialized and avoiding the diagnostic-probe contention discovered
during Gate B.

This preserves the existing safety rule: a WMI write is considered potentially
effective from the instant dispatch begins.

For later target changes, WriteIntent should record both the previously owned
pair and the pending pair. If the controller dies mid-transition, either value
may legitimately be present.

## 9. Exact release ordering

Never disarm the watchdog before hardware restore is proven.

Correct order:

```text
controller -> watchdog: RESTORE_BEGIN
controller: FF,FF
controller: LegacyDefault
controller: verify EC 0x34/0x35 == FF/FF
controller -> watchdog: RELEASE
watchdog: independently normalize FF,FF -> LegacyDefault once more
watchdog: verify EC FF/FF
watchdog: clear durable armed state
```

If the GUI dies anywhere before RELEASE, the watchdog still owns crash cleanup.
RELEASE itself is not trusted as proof that LegacyDefault completed: FF/FF can
be the midpoint of the restore sequence, so the watchdog performs one final
idempotent restore normalization before deleting the durable lease.

Closing the pipe is last.

## 10. Durable crash journal

The service itself can crash or be restarted. Therefore WRITE_ARMED / OWNED /
RESTORING must survive watchdog-process death.

Store a small machine-level journal under ProgramData, for example:

```text
%ProgramData%\VictusFanControl\watchdog\lease.json
```

Fields should include only machine-safety state:

- schema/protocol version;
- session/lease GUID;
- controller PID;
- controller creation time;
- lease state;
- generation number;
- previous owned CPU/GPU setpoint;
- pending/owned CPU/GPU setpoint;
- creation timestamp for diagnostics.

Heartbeat does **not** need to be flushed to disk every second.

Before acknowledging WRITE_INTENT, write the new journal to a same-directory
temporary file using WriteThrough, call Flush(flushToDisk: true), then replace
the live record with MoveFileEx(REPLACE_EXISTING | WRITE_THROUGH). Only after
that durable step may SetFanLevel be dispatched.

On clean FF/FF verification, clear the durable armed record only after RELEASE.

## 11. Watchdog restart rule

On service startup:

1. exact hardware fingerprint check;
2. read durable journal;
3. read EC 0x34/0x35;
4. if no active durable lease:
   - FF/FF -> Ready;
   - fixed external setpoint -> Blocked, do not clear it;
5. if active durable lease:
   - EC FF/FF -> complete/normalize restore and clear journal;
   - EC matches a lease-allowed previous/pending target -> restore;
   - EC is another fixed pair -> ownership ambiguous; do not blindly clear an
     unknown external override.

A service restart while a lease is active should prefer a conservative firmware
handoff instead of trying to reconstruct and continue Custom control.

## 12. Ownership rule

The watchdog must not use 0x62 or 0x63 as ownership evidence.

For this target:

- 0x34/0x35 are the fixed-setpoint ownership evidence;
- 0x62/0x63 are externally maintained by HP/OMEN in the validated setup.

The service may restore only when its durable lease shows that a VFC write may
have occurred and the observed fixed setpoint is compatible with that lease.

There is an unavoidable hardware limitation: the EC does not contain a VFC
owner token. Another controller that independently writes the exact same
CPU/GPU pair during the same tiny transaction window is indistinguishable. The
project already requires other fan controllers closed; this residual race must
be documented rather than hidden.

## 13. Timeouts

Use state-specific deadlines instead of one timeout for every state.

Initial validation values, not final production constants:

- OWNED heartbeat interval: 1 s;
- OWNED missed-heartbeat timeout: 5 s;
- WRITE_ARMED operation deadline: 12 s;
- RESTORING takeover deadline: 8 s;
- proven process death: immediate takeover when hardware may have been written;
- pipe loss while the exact owner is still alive: retain the durable lease,
  allow same-identity reconnect, and let state deadlines/process death decide
  recovery.

Why WRITE_ARMED is longer: the current HP backend can spend up to ~1.5 s on
setpoint ACK plus up to ~8 s on tachometer ACK, with additional WMI/EC overhead.

A timeout during WRITE_ARMED or RESTORING is itself a safety failure and should
return authority to firmware.

## 14. Suspend / resume

The existing GUI lifecycle remains the primary path:

```text
suspend
 -> close admission
 -> cancel command
 -> restore FF/FF -> LegacyDefault
 -> verify Firmware
 -> watchdog RELEASE
 -> sleep
```

The watchdog is a second failure domain, not a replacement for that logic.

The service should also accept Windows power notifications for diagnostics and
additional fencing. If an armed lease somehow survives into suspend, it should
be treated as abnormal and restored.

For elapsed timeout accounting, use an explicit Windows monotonic source with
documented sleep semantics rather than wall-clock timestamps. The implementation
must keep that choice stable across future .NET upgrades.

## 15. Service failure behavior

A watchdog that silently stops is worse than no watchdog.

Configure SCM recovery to restart the service after unexpected failure. Also
ensure fatal BackgroundService exceptions terminate the service process with a
failure exit code; a graceful host stop may not trigger Windows Service recovery
actions.

On watchdog connection loss while the GUI is still alive:

1. immediately block new Custom writes;
2. controller performs local validated restore if Custom is active;
3. do not reacquire Custom until a fresh watchdog Ready handshake succeeds.

On watchdog restart with an active durable journal, the service independently
restores before accepting a new lease.

## 16. Service stop / update

A requested service stop or software update must not simply remove the safety
process while Custom is active.

Required sequence:

1. service enters DRAINING and refuses new Prepare/WriteIntent;
2. ask connected controller to hand off to firmware;
3. wait a bounded interval for FF/FF;
4. if still armed, watchdog performs restore itself;
5. verify FF/FF;
6. only then stop.

## 17. Security boundary

The watchdog is a privileged hardware-safety component, so the command surface
must remain minimal.

Do not expose:

- arbitrary WMI command IDs;
- arbitrary EC write;
- ordinary 14..50 fan write;
- shell execution;
- caller-selected module paths.

Allowed external operations should be narrow protocol messages such as:

- Hello / Status;
- Prepare;
- WriteIntent;
- Commit;
- Heartbeat;
- RestoreBegin;
- Release.

The restore action is internal and always the fixed
`FF,FF -> LegacyDefault -> verify FF/FF` sequence.

## 18. Staged validation plan

Do not integrate all pieces at once.

### Gate A - service environment, read-only

- service installs/starts;
- correct account/Session 0;
- target fingerprint;
- PawnIO EC read;
- HP WMI read;
- restart recovery;
- no fan writes.

### Gate B - service restore primitive

**PASSED on real hardware, 2026-09-24.** After the production backend reported
EC + dual-tach acknowledgement at 30/30, the exact GUI process was force-killed.
A post-kill independent probe confirmed orphaned 30/30. The LocalSystem Session
0 service then independently executed `FF,FF -> LegacyDefault`, verified EC
FF/FF in about 443 ms, and exited. A separate parent probe again confirmed
FF/FF before the ownership-safe fallback was cancelled. OMEN Gaming Hub
undervolt remained unchanged. See `WATCHDOG_GATE_B.md`.

### Gate C - synthetic lease state machine

**PASSED in Windows CI, 2026-09-24.** The implementation now includes the
PREPARED / WRITE_ARMED / OWNED / RESTORING state machine, generation checks,
durable write-through journal, service-side monotonic deadlines, bounded framed
JSON protocol, real Windows named-pipe PID verification, and fail-closed
ownership recovery using fake hardware.

Validated boundaries include:

- owner loss before WriteIntent;
- transport loss after durable WriteIntent but before ACK consumption while the owner process remains alive -> retain WRITE_ARMED; if no reconnect/progress follows, WRITE_ARMED deadline restores;
- restart after WriteIntent before WMI;
- restart after WMI before Commit;
- restart after Commit;
- restart/death during RESTORING;
- old/new target acceptance during an in-flight owned-target transition;
- stale generation and malformed protocol rejection;
- duplicate Release;
- heartbeat timeout, WRITE_ARMED deadline and RESTORING deadline;
- late Heartbeat/WriteIntent/Commit cannot revive an already expired lease even
  if the background deadline monitor has not run yet;
- proven owner death while OWNED -> immediate restore;
- live-owner pipe loss while OWNED -> retain lease and permit same-identity reconnect;
- service restart in every durable phase;
- corrupt journal / unknown fixed setpoint -> no blind restore;
- external fixed override -> Prepare refused;
- restore failure -> journal retained.

From WRITE_ARMED onward the watchdog completes/normalizes the firmware restore
even if EC already reads FF/FF, because FF/FF can be the midpoint of a partial
FF/FF -> LegacyDefault handoff. PREPARED alone can be cleared without restore.

Gate C does not host a persistent privileged service pipe or touch hardware.
The explicit service ACL, real hardware adapter, process-lifetime monitor and
backend transaction hooks are Gate D integration work.

### Gate D - real GUI forced kill

**PASSED on real hardware, 2026-09-25.**

The implementation now includes:

- persistent LocalSystem / Session 0 service with SCM restart policy;
- exact-target startup recovery before accepting a controller;
- restore-only hardware adapter;
- protected named-pipe DACL for SYSTEM/Administrators with NETWORK denied;
- kernel-observed client PID + process creation-time verification;
- an open controller process handle plus independent 250 ms process-lifetime
  monitoring that survives pipe-session reconnects;
- every lease mutation bound to the durable kernel-verified PID + process
  creation time;
- live-owner pipe loss retains the lease rather than restoring underneath a
  controller that can still execute;
- 250 ms service-side lease deadline monitoring;
- durable ProgramData journal ACL restricted to SYSTEM/Administrators;
- service-stop/update recovery;
- real backend Prepare / WriteIntent / post-intent EC recheck / WMI / EC+dual-tach ACK / Commit ordering;
- heartbeat coupled to fresh backend ownership/feedback validation;
- no-write CancelPrepared / AbortWriteIntent rollback;
- live-controller local restore preserved even if watchdog IPC fails;
- explicit hardware harness that never invokes parent-shell HP restore.

Physical result:

- watchdog Ready under LocalSystem / Session 0;
- validated real 30/30 and durable OWNED generation 3;
- journal bound to the exact GUI PID + process creation time;
- exact GUI PID force-killed;
- no parent PowerShell HP restore;
- watchdog owner-loss path recorded RestoredFirmware and cleared the journal;
- independent EC probe confirmed FF/FF;
- watchdog service PID remained unchanged;
- emergency fallback did not fire;
- OMEN Gaming Hub undervolt remained unchanged.

See `WATCHDOG_GATE_D.md`.

### Gate E - watchdog death

**PASSED on real hardware, 2026-09-25.**

Gate E used the dedicated GUI mode and hardware harness to isolate the inverse
failure domain from Gate D.

Physical result:

- production postcheck started from watchdog Ready under LocalSystem / Session 0,
  no durable journal, EC FF/FF and SCM recovery 1 s / 5 s / 10 s;
- the real backend reached READY after EC + dual-tach acknowledgement and durable
  watchdog Commit at OWNED generation 3, target 30/30;
- the journal was bound to GUI PID 7544 + exact process creation time;
- only watchdog service PID 16724 was force-killed;
- the parent shell issued no HP restore;
- the live GUI classified the failure as
  `WATCHDOG_IPC_LOSS during Probe: Pipe is broken`;
- the controller completed its local validated firmware restore;
- an independent EC probe confirmed FF/FF while the old watchdog was dead and
  before a replacement service PID existed;
- the durable OWNED journal remained available for restart recovery;
- SCM started watchdog PID 14184, whose startup recovery reported
  `RestoredFirmware`, normalized the restore and cleared the journal;
- final and post-test EC probes remained FF/FF;
- the delayed emergency fallback did not fire;
- production watchdog was reinstalled and PID 492 returned Ready;
- production SCM recovery was re-verified at 1 s / 5 s / 10 s;
- OMEN Gaming Hub CPU undervolt remained unchanged.

The final harness also encodes contention/causality invariants: there is no
out-of-band full EC snapshot or artificial dwell between READY and watchdog
kill, watchdog dependency is probed before EC health validation, and only a
classified watchdog transport loss can satisfy the Gate E local-restore PASS.

See `WATCHDOG_GATE_E.md`.

### Gate F - double-failure / durable journal

- lease reaches WRITE_ARMED or OWNED;
- kill controller and watchdog near-simultaneously;
- SCM restarts watchdog;
- watchdog reads durable journal and restores without the test shell.

### Gate G - lifecycle

Repeat suspend/resume with watchdog installed and prove:

- no false lease timeout;
- pre-sleep release still completes;
- watchdog remains/disarms consistently;
- resume does not allow Custom until telemetry + watchdog are both ready.

Only after these gates should load/gaming validation and the adaptive RPM policy
be allowed to depend on unattended Custom authority.
