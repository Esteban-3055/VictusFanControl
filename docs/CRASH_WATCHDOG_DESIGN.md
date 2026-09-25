# Independent crash-watchdog / lease design

Status: research/design phase complete. Gate A read-only Windows Service validation passed on real hardware under LocalSystem. No lease or watchdog restore logic is integrated yet.

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

Owner process handle becomes signaled / pipe breaks.

If a hardware write may have occurred, restore immediately. Do not wait for the
heartbeat timeout.

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
watchdog: clear durable armed state
```

If the GUI dies anywhere before RELEASE, the watchdog still owns crash cleanup.

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

Before acknowledging WRITE_INTENT, write the new journal to a temporary file,
flush it to disk, then atomically replace the previous record. Only after that
durable step may SetFanLevel be dispatched.

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
- process death / pipe loss: immediate takeover when hardware may have been
  written.

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

Implementation is ready; physical validation is pending. The test uses the already-validated GUI coordinator/backend path to reach 30/30, arms an ownership-safe delayed fallback, force-kills the exact GUI PID so managed cleanup cannot participate, then starts a one-shot LocalSystem service. The service refuses any pre-state other than the explicit 30/30 test ownership and its only write-capable operation is `FF,FF -> LegacyDefault`, followed by mandatory EC FF/FF verification. See `WATCHDOG_GATE_B.md`.

### Gate C - synthetic lease state machine

Use fake hardware and kill/restart simulations for every boundary:

- death before WriteIntent ACK;
- death after WriteIntent ACK but before WMI;
- death after WMI but before Commit;
- death after Commit;
- death during RESTORING;
- stale generation;
- malformed message;
- duplicate Release;
- pipe loss;
- service restart with each journal state.

### Gate D - real GUI forced kill

- watchdog Ready;
- validated 30/30;
- lease OWNED;
- kill GUI;
- watchdog detects process death;
- watchdog restores automatically;
- no parent PowerShell cleanup;
- EC FF/FF;
- undervolt unchanged.

### Gate E - watchdog death

While GUI owns 30/30:

- kill watchdog service process;
- GUI detects watchdog loss and locally restores;
- SCM restarts watchdog;
- service starts cleanly and reports Ready.

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
