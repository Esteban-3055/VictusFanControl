# Watchdog Gate C - synthetic lease / journal / named-pipe state machine

Status: **Gate C PASSED in Windows CI on 2026-09-24.** This gate is synthetic by design and performs no real fan/EC writes.

Gate C deliberately does not touch real fan hardware. It establishes the
transaction and crash semantics that must exist before the watchdog is wired
into the production SetFanLevel boundary.

## Implemented state machine

~~~text
IDLE
  -> PREPARED
  -> WRITE_ARMED
  -> OWNED
  -> RESTORING
  -> IDLE
~~~

The durable journal is written and flushed to storage before WriteIntent can
return success. Only after that acknowledgement may a future production
controller dispatch SetFanLevel.

## Ownership rules

Recovery may restore only when the observed fixed setpoint matches a setpoint
that the durable VFC lease permits:

- OWNED target;
- pending target during WRITE_ARMED;
- previous owned target during an in-flight target transition.

FF/FF is cleared as already safe. An unknown fixed pair is ownership-ambiguous
and is never cleared.

## Named-pipe protocol

Gate C adds a bounded length-prefixed JSON protocol with:

- protocol version;
- request id;
- Hello;
- Prepare;
- WriteIntent;
- Commit;
- Heartbeat;
- RestoreBegin;
- Release.

The synthetic Windows named-pipe server validates the client PID obtained from
GetNamedPipeClientProcessId and compares process creation time before accepting
lease commands. The service-side ACL is intentionally deferred to the real
service hosting step; Gate C does not expose a persistent privileged pipe.

## Durable journal

The JSON journal records:

- schema version;
- random lease/session GUID;
- controller PID and process creation time;
- phase;
- generation;
- previous/pending/owned setpoints;
- diagnostic creation timestamp.

Critical stores use a same-directory temporary file, FileOptions.WriteThrough,
Flush(flushToDisk: true), then Win32 MoveFileEx with REPLACE_EXISTING +
WRITE_THROUGH. A WriteIntent success therefore cannot be returned before the
write-armed journal record and its rename/replace have been synchronously
committed through the Windows storage path.

Heartbeats are not persisted every second. The service records only its own
monotonic receive time for liveness; client wall-clock time is not authoritative.

A second important invariant was found during review: once WRITE_ARMED has been
durably entered, owner loss or service restart completes the validated firmware
restore even when the observed setpoint is already FF/FF. FF/FF can be the
midpoint of a partially completed FF/FF -> LegacyDefault handoff, so the durable
lease is not discarded until restore normalization is complete. PREPARED is the
only phase that can be abandoned without a restore because no hardware write is
yet permitted.

## Synthetic gates

The self-test covers:

- clean PREPARED -> WRITE_ARMED -> OWNED -> RESTORING -> release;
- owner death and service restart in PREPARED without a hardware write;
- pipe loss before the WriteIntent ACK is delivered;
- restart after WriteIntent but before WMI, including FF/FF restore normalization;
- restart after WMI but before Commit;
- restart after Commit;
- restart during RESTORING;
- previous/pending target ambiguity during a target transition;
- stale generation rejection;
- duplicate Release;
- OWNED heartbeat timeout;
- WRITE_ARMED deadline;
- RESTORING deadline;
- Prepare refusal when an external fixed override already exists;
- PREPARED restart while an external override appears, without clearing it;
- unknown external fixed override with an active lease;
- no-journal external override;
- restore failure retaining the durable ownership record;
- corrupted journal;
- malformed protocol frame;
- named-pipe client identity mismatch;
- broken-pipe response race treated as owner loss rather than a server fault;
- real named-pipe EOF while OWNED causing immediate synthetic restore;
- heartbeat renewal without rewriting the durable journal;
- OWNED heartbeat timeout, WRITE_ARMED deadline and RESTORING deadline takeover.

The Windows CI run executes the real named-pipe tests, including
GetNamedPipeClientProcessId identity verification and broken-pipe recovery.
All Gate C cases pass together with the existing Gate B, SafetyGate,
FanControlCoordinator, BIOS-contract and HP-backend regression suites.

Run locally:

~~~powershell
.\scripts\test-watchdog-gate-c.ps1
~~~

A Gate C PASS does not authorize real automatic fan control. Gate D is the first
real-hardware integration of this lease with the actual GUI/backend write
boundary.
