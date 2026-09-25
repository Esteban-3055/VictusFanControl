# Watchdog Gate C - synthetic lease / journal / named-pipe state machine

Status: implementation added; CI validation pending.

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

Critical stores use a same-directory temporary file, WriteThrough,
Flush(flushToDisk: true), then replace the active record.

Heartbeats are not persisted every second.

## Synthetic gates

The self-test covers:

- clean PREPARED -> WRITE_ARMED -> OWNED -> RESTORING -> release;
- owner death before any write;
- restart after WriteIntent but before WMI;
- restart after WMI but before Commit;
- restart after Commit;
- restart during RESTORING;
- previous/pending target ambiguity during a target transition;
- stale generation rejection;
- duplicate Release;
- OWNED heartbeat timeout;
- WRITE_ARMED deadline;
- RESTORING deadline;
- unknown external fixed override;
- no-journal external override;
- corrupted journal;
- malformed protocol frame;
- named-pipe client identity mismatch;
- real named-pipe EOF while OWNED causing immediate synthetic restore.

Run locally:

~~~powershell
.\scripts\test-watchdog-gate-c.ps1
~~~

A Gate C PASS does not authorize real automatic fan control. Gate D is the first
real-hardware integration of this lease with the actual GUI/backend write
boundary.
