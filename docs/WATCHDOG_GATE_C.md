# Watchdog Gate C - synthetic lease / journal / named-pipe state machine

Status: **Gate C PASSED in Windows CI on 2026-09-24 and passed a dedicated pre-Gate-D safety audit.** This gate is synthetic by design and performs no real fan/EC writes.

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

PREPARED may be cleared without a restore because no VFC write is yet
permitted. From WRITE_ARMED onward, even an observed FF/FF is normalized through
the full validated FF/FF -> LegacyDefault restore primitive before the durable
lease is cleared. An unknown fixed pair is ownership-ambiguous and is never
blindly cleared.

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
- Release at FF/FF still normalizes the full firmware restore before clearing;
- Release restore failure retains the RESTORING journal;
- duplicate Release without a second restore;
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
- late Heartbeat/WriteIntent commands cannot revive an expired OWNED lease;
- a late Commit cannot revive an expired WRITE_ARMED lease;
- out-of-range WriteIntent is rejected without journal mutation;
- Release can take over a still-owned target, and refuses an unknown external override while retaining the RESTORING journal.

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

## Pre-Gate-D audit result

The audit found and corrected two issues before real-service integration:

1. RELEASE previously treated an observed FF/FF as sufficient to delete the
   durable lease. That was too weak because FF/FF can be the midpoint of the
   validated FF/FF -> LegacyDefault handoff. RELEASE now always completes one
   watchdog-side restore normalization and verifies FF/FF before clearing the
   journal; restore failure retains the RESTORING record.
2. State deadlines were previously enforced only when the external deadline
   monitor called CheckDeadlinesAsync. A late Heartbeat or Commit could therefore
   arrive first after a scheduler stall and revive an already-expired lease.
   Deadline checks now also run synchronously on the WriteIntent, Commit,
   Heartbeat and RestoreBegin command paths. Expired commands trigger the same
   fail-closed recovery and return LEASE_EXPIRED.

The final Windows CI pass includes these regression cases together with the
existing Gate B, SafetyGate, coordinator, BIOS-contract and HP-backend suites.

Gate C still intentionally does not provide the persistent LocalSystem pipe ACL,
open controller process handle, real hardware adapter, hosted deadline loop or
Hp88F8 backend transaction hooks. Those are Gate D integration responsibilities,
not omissions from this synthetic gate.
