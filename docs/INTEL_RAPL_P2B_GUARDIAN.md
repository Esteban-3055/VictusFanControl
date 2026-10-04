# Intel RAPL P2B — guardian and bounded external-writer reacquisition

P2B continues from the physically qualified P1 evidence and the P2A
`CpuPowerLimiter` software foundation. This branch still does **not** authorize
production RAPL writes or GUI integration.

## Step 1 — conflict policy (software only)

The external-writer policy is frozen as:

- first conflict -> `Contested`;
- no immediate write;
- one attempt every 30 active seconds;
- at most 5 attempts per conflict episode;
- exact readback is required;
- a successful reacquisition remains provisional for 60 active seconds;
- a new conflict inside those 60 seconds keeps the same attempt budget;
- attempt 5 failure, or renewed conflict after provisional attempt 5, -> `Yielded`;
- `Yielded` performs no automatic sixth write;
- explicit user action is required to start a fresh authority episode;
- safety/target/lifecycle logic can force immediate yield;
- active-time timing excludes sleep/hibernation.

A requested value that returns on its own while `Contested` can enter the
60-second stability window without consuming a write attempt.

## Step 2 — limiter ownership and handoff model

The pure conflict policy is now mapped into the production-domain
`CpuPowerLimiter`, still with an abstract backend and hardware-free fixtures.

New limiter states:

- `Contested`
- `ReacquiredPendingStability`
- `Yielded`

The original P2 states remain for normal apply/recovery/failure.

### Three distinct values

The limiter deliberately keeps three concepts separate:

1. **OriginalBaseline** — immutable snapshot read before the first user Apply.
2. **Requested/Applied value** — the exact raw value whose PL1/PL2 fields are
   currently owned by VictusFanControl.
3. **ExternalHandoff** — the latest full raw snapshot observed from an external
   writer before VictusFanControl performs a bounded reacquisition.

The original baseline remains evidence. Once an external writer has taken the
power fields and VictusFanControl later reacquires them, a later normal release
must hand control back to the captured external value rather than blindly
restoring the stale original baseline.

### Non-owned fields

A full-raw mismatch is not automatically a PL1/PL2 conflict.

The backend exposes `OwnedFieldsMatch()`. If the requested PL1/PL2 power fields
still match but another agent changed only non-owned fields, the limiter adopts
that exact raw value and remains `Active` without a write.

A bounded reacquisition is created through `BuildReacquirePlan()` from:

- immutable original baseline;
- original user request;
- latest current external snapshot.

The future physical backend must therefore preserve current non-owned fields
while changing only the qualified PL1/PL2 power fields.

### Bounded reacquisition

`VerifyActive()` now advances the conflict state machine:

- `Active` + owned-field change -> capture ExternalHandoff -> `Contested`.
- Before 30 s -> read-only observation, no write.
- At the deadline -> one pre-write read, one bounded plan, at most one write,
  exact readback.
- Exact readback -> `ReacquiredPendingStability`.
- Readback mismatch -> the observed external raw becomes the latest handoff and
  the attempt is consumed.
- Five failed attempts -> `Yielded`, with no automatic sixth write.
- 60 stable seconds -> `Active` and the retry budget resets, while the
  ExternalHandoff remains available for a later release.

A changing external value while already `Contested` updates the handoff but
does not postpone the current 30-second retry deadline.

### Release semantics

Release is ownership-conditional:

- If VictusFanControl still owns the requested PL1/PL2 fields, restore the
  current restore target (ExternalHandoff if one exists, otherwise
  OriginalBaseline).
- If another agent already owns the PL1/PL2 fields, preserve its current value
  and finish release without another write.
- If requested PL1/PL2 fields remain present but restoration is blocked, release
  is unresolved and returns failure.
- A lock appearing while VictusFanControl still owns the requested power fields
  is `Failed` and never triggers an unlock/bypass write.
- A locked value owned by an external writer is yielded without a write.

This prevents a `Yielded` session from later overwriting the external writer
with the stale 45/115 W session baseline.

## Step 2 verification

Hardware-free fixtures now cover:

- normal single-shot apply/verify/release;
- non-owned raw changes without false contention;
- delayed successful reacquisition;
- 60-second provisional stability;
- release restoring external handoff instead of stale original baseline;
- evolving external values becoming the latest handoff;
- exactly five rejected reacquisition writes followed by `Yielded`;
- no sixth automatic write after long elapsed time;
- release after `Yielded` preserving the external value with zero restore write;
- lock appearing while requested power remains owned -> unresolved fail-closed;
- initial rejected apply and unsupported backend behavior.

## Still not implemented

Step 2 does **not** add:

- a physical production RAPL backend;
- a detached production guardian process;
- durable conflict/journal persistence across process death;
- IPC/lease;
- AC/suspend/resume/parent-death handling for the production limiter;
- GUI, profiles or startup persistence.

Those are later P2B/P2C gates.

Production gates remain closed:

- `productionHardwareWritesAuthorized=false`
- `guiIntegrationAuthorized=false`
- `startupPersistenceAuthorized=false`
- `automaticProfileIntegrationAuthorized=false`


## Step 3A — durable journal contract and store

A production guardian cannot rely on in-memory ownership state. Step 3A adds a
durable session journal contract without connecting it to physical RAPL writes
yet.

The journal schema records:

- exact target profile id;
- session id and monotonic generation;
- phase;
- immutable original baseline snapshot;
- original user PL1/PL2 request;
- latest exact applied raw value;
- latest external handoff snapshot, when one exists;
- conflict state, attempt count and active-time timestamps;
- pending raw value for write-armed/reacquire/restoring phases;
- creation/update UTC timestamps.

Defined phases are:

- `WriteArmed`
- `Owned`
- `Contested`
- `ReacquireWriteArmed`
- `Stability`
- `Yielded`
- `Restoring`
- `Unresolved`

The JSON store uses a new temporary file, `FileOptions.WriteThrough`,
`Flush(flushToDisk: true)`, and on Windows a `MoveFileEx` replacement with
`MOVEFILE_WRITE_THROUGH`. Invalid records are rejected before the current
journal is replaced.

The loader is exact-target gated. A journal created for another hardware
profile is invalid and cannot be used to infer ownership or authorize a
restore.

Phase validation rejects impossible combinations such as:

- Contested without an external handoff;
- ReacquireWriteArmed without an in-flight consumed attempt;
- Inactive conflict state carrying an attempt budget/history;
- WriteArmed with a pending raw different from the intended applied raw.

Step 3A intentionally does **not** resume automatic reacquisition after a
guardian process crash. Durable state is being introduced first. Recovery
semantics for a restarted guardian remain fail-closed and will be implemented
as a separate gate: inspect current 0x610, restore only still-owned fields or
preserve an external writer, never blindly resume the retry timer.

Normal GUI/controller death is different: the future detached guardian is
expected to remain alive and continue the already-running conflict policy.
The journal exists for crash evidence and guardian recovery, not as permission
for an arbitrary new process to continue writes.

A focused `cpu-rapl` GitHub Actions workflow is also added for the isolated
CPU branch. The repository-wide build can currently fail in fan/ACPI
diagnostic smoke tests that are outside this branch's CPU scope; the CPU
workflow gives a same-head signal for RAPL fixtures and compilation without
weakening the later requirement to rebase onto the final fan/WMI base before
production integration.
