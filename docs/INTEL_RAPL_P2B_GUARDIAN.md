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
- `PresetSwitchWriteArmed`
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


## Step 3B — journal-before-write integration

The durable journal is now connected to `CpuPowerLimiter` itself. The backend
remains abstract and there is still no production PawnIO write path.

Every limiter write is now guarded by a durable phase:

```
initial Apply:
WriteArmed durable
  -> backend write
  -> exact readback
  -> Owned durable

bounded reacquire:
Contested durable
  -> ReacquireWriteArmed durable
  -> backend write
  -> exact readback
  -> Stability durable
  -> 60 active seconds
  -> Owned durable

release/restore:
Restoring durable
  -> compare-read unchanged
  -> backend write
  -> exact readback
  -> journal delete
```

If the required journal transition cannot be persisted **before** a write, the
write does not occur.

A new Apply also refuses to start when any unresolved journal already exists.
That journal must be handled by the future recovery planner first.

Important failure semantics are intentionally conservative:

- If `WriteArmed` persists and the hardware write succeeds but persisting
  `Owned` fails, the old `WriteArmed` record is retained. The limiter reports
  failure and does not issue an unjournaled restore from Dispose.
- If `ReacquireWriteArmed` cannot be persisted, the external value is left
  untouched and no reacquisition write occurs.
- If `Restoring` cannot be persisted, no restore write occurs.
- If a write may have happened but its post-write state cannot be read, the
  last write-armed journal is intentionally retained for recovery.
- A lock while VictusFanControl still appears to own the requested PL fields is
  stored as `Unresolved`; no unlock/bypass write exists.
- Once an external writer already owns PL1/PL2, release may close the session
  without a hardware write and delete the resolved journal.

The fake backend used by the self-test now refuses any write unless the
corresponding durable phase exists and its `PendingRaw` exactly equals the raw
value being written. This makes journal-before-write an executable invariant,
not only documentation.

Step 3B fixtures additionally cover:

- unresolved journal blocks a fresh Apply with zero writes;
- failed initial WriteArmed persistence -> zero writes;
- failed Owned persistence after a confirmed write leaves the conservative
  WriteArmed record and forbids an unjournaled Dispose restore;
- failed ReacquireWriteArmed persistence -> external value preserved, zero
  retry writes;
- failed Restoring persistence -> current owned limit remains untouched, zero
  restore writes;
- normal apply/reacquire/restore prove their required journal phase existed at
  the instant the fake hardware write was invoked.

The next gate is the **recovery planner**. It will consume a journal found after
guardian death and classify the observed 0x610 state. It will be recovery-only:
a restarted guardian will not resume retry timers or continue automatic
reacquisition merely because a journal says attempt 3/5.


## Step 4 — guardian-death recovery planner

A hardware-free recovery planner now classifies a durable session together with
the currently observed CPU power-limit snapshot. It has one strict rule:

> a restarted guardian is release-only; it never resumes the 30-second retry
> timer and never spends the remaining 5-attempt reacquisition budget.

The planner exposes only four dispositions:

- `ClearAlreadyReleased` — baseline/handoff/armed restore is already present;
  delete the resolved journal without a write.
- `PreserveExternalAndClear` — requested PL1/PL2 ownership is no longer
  attributable to VictusFanControl; preserve the current external value and
  delete the stale session journal.
- `RestoreOwnedThenClear` — requested PL1/PL2 fields still match the durable
  VFC-owned value; one conditional **release** restore may be attempted to the
  captured ExternalHandoff or, if none exists, the OriginalBaseline.
- `BlockedByLockRetainJournal` — requested PL1/PL2 fields still appear owned
  but the register is locked; retain the journal and perform zero writes.

There is intentionally no `ResumeReacquire`, `RetryContested` or equivalent
action.

Phase semantics after guardian death:

- `WriteArmed`: if baseline is still present, clear; if requested PL fields
  are present, release them; otherwise preserve the external owner.
- `Owned`: release still-owned PL fields to ExternalHandoff/baseline.
- `Contested`: preserve current state and clear. Never restart the retry
  episode, even if the current watts happen to equal the old requested values.
- `ReacquireWriteArmed`: if the reacquire write appears to have happened,
  release to ExternalHandoff; if handoff/external values are present, clear
  without retrying.
- `Stability`: release a still-present provisional reacquire; never resume the
  60-second stability timer.
- `Yielded`: preserve current state and clear; a restart does not regain
  authority.
- `Restoring`: if the restore target/pending restore is already present,
  clear; if requested PL fields are still present and unlocked, permit one
  conditional recovery restore; otherwise preserve the external owner.
- `Unresolved`: if the obstacle disappeared and requested PL fields still
  match, permit release-only restore; if locked, retain; if another writer owns
  the fields, preserve it and clear.

The planner is pure and performs no I/O. The next gate will be a recovery
executor that consumes only `RestoreOwnedThenClear` plans, persists/updates a
`Restoring` journal before any recovery write, performs a compare-read,
writes at most once, requires exact/equivalent release readback, and never
converts recovery into reacquisition.


## Step 5 — recovery executor

The recovery-only planner is now paired with a hardware-abstraction executor.
There is still no production PawnIO RAPL backend; all executor writes in CI are
performed by a fake backend.

The executor contract is:

```
load durable journal
  -> read current package power limit
  -> recovery-only planner
  -> clear / preserve / blocked
     OR
  -> build release restore
  -> confirm journal generation still matches
  -> Restoring durable with exact PendingRaw
  -> compare-read hardware
  -> confirm durable Restoring generation still matches
  -> at most ONE release write
  -> readback
  -> clear journal only when resolved
```

### No reacquisition authority

The executor never calls `BuildApplyPlan()` or `BuildReacquirePlan()`. Its
only possible write comes from `PlanRestore()` after the planner returned
`RestoreOwnedThenClear`.

The executor therefore cannot resume a prior 3/5 retry episode or write the
requested 20/40 W value merely because the old journal contains that request.

### Journal-before-write remains mandatory

Before the single permitted recovery write, the executor persists:

- the same session id;
- generation + 1;
- phase `Restoring`;
- recovery-normalized conflict state with no in-flight attempt;
- exact `PendingRaw` returned by the backend release plan.

A failed `Restoring` store produces zero hardware writes.

The fake recovery backend enforces this at the instant of `Write()`: phase
must be `Restoring` and `PendingRaw` must exactly equal the raw value being
written.

### Compare-read and concurrent writers

After `Restoring` is durable, the executor reads the hardware again.

If raw 0x610 changed since planning, it performs zero writes and reclassifies
the new state:

- release/handoff already present -> clear journal;
- external owner present -> preserve it and clear journal;
- lock while still owned -> retain journal;
- requested ownership still appears present but raw changed -> retain
  `Restoring` and defer. The executor does not chase the race.

The executor also rechecks journal session/generation/phase/PendingRaw before
the hardware write. A concurrent journal mutation causes zero writes.

### Post-write ambiguity

Only one recovery write is permitted per `Execute()`.

If write throws or readback differs, the executor reads/reclassifies once:

- release target/equivalent owned fields present -> resolved and clear;
- external writer now owns PL1/PL2 -> preserve external and clear;
- lock -> retain;
- requested fields still require release -> mark `Unresolved` best-effort and
  retain the journal.

There is no second corrective write in the same recovery execution.

### Step 5 fixtures

The hardware-free recovery executor fixtures prove:

- no journal -> zero hardware I/O;
- already released -> delete journal, zero writes;
- external owner -> preserve and delete, zero writes;
- still-owned value -> exactly one journaled release restore;
- Contested restart -> zero reacquire writes and session closes;
- failed `Restoring` persistence -> zero writes;
- hardware changes to external value during compare-read -> preserve external,
  zero writes;
- hardware changes only while still matching requested ownership -> defer,
  zero writes;
- write exception after the release actually took effect -> readback resolves,
  no second write;
- external takeover after the one recovery write -> preserve external and issue
  no second write;
- locked still-owned value -> retain journal, zero writes;
- journal deletion failure is explicit: resolved hardware does not falsely
  imply the durable journal disappeared.

The next P2B gate is process/lifecycle containment: a detached CPU guardian
host, global single-writer mutex, parent-liveness/lease semantics and IPC
surface. Production RAPL hardware writes remain closed until those pieces and
their death/restart fixtures are qualified.


## Step 5.5 — AC and Battery CPU preset foundation

P2B now defines two independent CPU power preset slots:

- `AC`
- `Battery`

Each slot carries:

- Enabled/Disabled;
- PL1 watts;
- PL2 watts.

No production wattage is hard-coded. The default preset set is
`Disabled/Disabled`; the qualified 20/40 W value remains test evidence, not a
product default.

`CpuPowerPresetPolicy` is pure selection logic:

```
power source = AC      -> AC slot
power source = Battery -> Battery slot
power source = Unknown -> no preset authority
```

An enabled preset requires finite values, PL1 >= 10 W and PL2 >= PL1. The
physical backend will later apply the stricter qualified hardware envelope.

A disabled slot may retain configured wattage for UI convenience, but resolves
to no active request. At runtime that will mean: if VFC currently owns CPU
limits and the newly selected source slot is Disabled, release ownership to the
captured ExternalHandoff/original baseline instead of inventing a new cap.

### Runtime semantics implemented at the domain boundary

Power-source switching must be owned by the detached guardian, not by the GUI.
The intended production source signal is Windows AC/DC power notification,
confirmed against a direct power-source query before a transition.

The domain transition helper now enforces:

- Active AC -> Battery with Battery Enabled: perform one journaled owned-to-owned
  preset transition; do **not** restore OEM baseline and then apply again.
- Active Battery -> AC with AC Enabled: same one-write transition in reverse.
- Newly selected slot Disabled: perform a normal conditional release.
- Unknown source: no preset-switch write.
- `Yielded`: a power-source change does not silently reacquire authority.
  Explicit user action is still required.
- `Contested`: do not use source change to bypass an external writer; the
  conflict/yield semantics must be resolved first.
- A successful owned-to-owned source switch starts a fresh conflict budget for
  the newly active preset.
- OriginalBaseline remains immutable across AC/Battery switching.
- ExternalHandoff, if one exists from a prior external-writer episode, remains
  the release target across later AC/Battery switches.

The journal now has the explicit PresetSwitchWriteArmed phase so crash
recovery can distinguish the old VFC-owned raw value from the pending new
VFC-owned raw value. A restarted guardian remains recovery-only: if a
source-transition write was interrupted, it may release whichever VFC-owned
value is observed, but it will not finish/retry the source switch.

CpuPowerPresetTransitionController connects confirmed source selection to the
existing limiter without owning Windows notifications. Enabled AC/Battery
selection can switch only an already Active VFC-owned session. A disabled
destination performs conditional Release. Unknown source performs no preset
switch and fails closed by releasing existing preset authority when a normal
conditional release is safe. Contested/Yielded cannot use an enabled source
change to reacquire authority.

Startup persistence remains closed. Merely detecting AC or Battery at process
startup does not authorize Apply; an enabled selection observed while the
limiter is Disabled performs zero writes.


## Step 5.5B — shared AC/Battery source with GPU clock presets

The AC/Battery source abstraction is now shared between CPU and GPU preset
selection. CPU RAPL ownership/recovery remains a separate subsystem; this
change does not connect GPU writes to the CPU journal.

The requested GPU preset values are recorded exactly as:

```
AC:
  nvidia-smi -lgc 210,1850

Battery:
  nvidia-smi -lgc 210,1200
```

The corresponding pure preset requests are:

- AC: graphics clock range 210..1850 MHz.
- Battery: graphics clock range 210..1200 MHz.

These values are configuration intent only. The commit does not spawn
`nvidia-smi`, does not call a new NVML setter and does not grant startup
authority.

### Production GPU direction

The repository already has an NVML client for NVIDIA telemetry. The production
GPU controller should extend the NVML path for locked graphics clocks instead
of periodically spawning `nvidia-smi`. The command-line tests remain useful
as physical feasibility evidence and as an operator fallback.

GPU control must have its own ownership/journal/recovery domain. A combined
AC/Battery performance profile may select both CPU and GPU targets, but a
partial failure in one subsystem must not make the other subsystem claim a
false atomic transaction.

The intended source profile is therefore conceptually:

```
AC:
  CPU: configurable PL1 / PL2
  GPU: 210..1850 MHz

Battery:
  CPU: configurable PL1 / PL2
  GPU: 210..1200 MHz
```

Source = Unknown grants neither CPU preset-switch authority nor GPU clock
preset-switch authority.

For GPU runtime work, the same ownership principle should be retained: if
another application repeatedly changes the locked-clock range, a bounded
reacquisition policy may be used and then yield instead of fighting forever.
That GPU policy must be implemented and qualified independently rather than
reusing the CPU RAPL journal.

The GPU path should also avoid a design that continuously invokes
`nvidia-smi` merely to enforce a cap while the discrete GPU would otherwise be
idle. Source selection records desired intent; the future GPU controller will
decide when NVML authority is available without turning source detection into a
high-frequency polling loop.


## Step 5.6A — journaled CPU AC/Battery owned-to-owned transition

The limiter now has an explicit owned preset transition path. It is available
only while the current CPU session is Active; Contested, provisional Stability,
Yielded, Failed and recovery states cannot use a power-source change to gain
authority.

The transaction is:

    old VFC-owned raw
      -> durable PresetSwitchWriteArmed
         (AppliedRaw = old VFC raw, PendingRaw = new VFC raw)
      -> exactly one backend write
      -> exact raw readback
      -> Request/AppliedRaw become the new preset
      -> conflict budget resets
      -> durable Owned

No OriginalBaseline/ExternalHandoff restore occurs between enabled AC and
Battery presets. OriginalBaseline remains immutable, and an existing
ExternalHandoff remains the final release target.

If PresetSwitchWriteArmed cannot be persisted, there is no hardware write. If
the one write does not produce exact readback, the limiter fails closed and
retains the armed journal with both old and new VFC raw values for the
recovery-only gate. No second corrective write is issued by the switch method.

This is still software/fixture qualification only. It does not authorize a
physical RAPL backend, GUI integration, startup persistence or automatic
profile authority.


## Step 5.6B — guardian-restart recovery during preset switch

RecoveryPlanner and RecoveryExecutor now understand PresetSwitchWriteArmed.
A restarted guardian remains release-only and never completes, retries or
resumes the old AC/DC source transition.

The durable switch record intentionally carries:

- AppliedRaw = old VFC-owned preset raw;
- PendingRaw = new VFC-owned preset raw;
- OriginalBaseline unchanged;
- ExternalHandoff unchanged.

Recovery classification is therefore:

- old VFC raw present -> one conditional release-only restore;
- new VFC raw present -> one conditional release-only restore;
- release target already present -> clear journal, zero writes;
- neither old nor new VFC-owned PL fields present -> preserve external owner
  and clear journal;
- VFC-owned old/new fields locked -> retain journal, zero writes.

Before the recovery write the executor still persists Restoring, performs the
compare-read and rechecks journal generation. It selects the old or new owned
raw only to build the release plan; it never calls BuildApplyPlan,
BuildReacquirePlan or BuildOwnedTransitionPlan.

If hardware flips between the old and new VFC candidates during the recovery
compare/readback race, the executor issues no second write. It restores the
PresetSwitchWriteArmed evidence for a later release-only execution instead of
mistaking the other VFC candidate for an external owner.

Fixtures cover old raw, new raw and external raw at guardian restart. These are
synthetic recovery tests only; no physical PASS is claimed.

## Step 5.6C — confirmed-source transition dispatcher

The source-to-preset selector is now connected to the limiter through
CpuPowerPresetTransitionController, still without Windows notification wiring.

The dispatcher is deliberately authority-limited:

- enabled AC/Battery can call SwitchOwnedPreset only from Active;
- Disabled cannot be turned into an Apply by source detection;
- a disabled destination performs the existing conditional Release;
- Unknown never selects AC or Battery; if a normal owned session exists, it
  gives up preset authority through conditional Release;
- Contested/Yielded cannot use an enabled source change to regain authority;
- Failed/Applying/Recovering states are not bypassed.

Fixtures exercise the confirmed AC->Battery path, disabled destination,
Unknown source, Contested blocking and the no-startup-Apply invariant.

Windows AC/DC notifications plus direct-query confirmation are intentionally
left for the detached guardian/process-lifecycle gate. No production source
listener, startup persistence, GUI authority or physical write authorization
is added here.

## Step 5.7 — NVML locked-graphics-clock backend foundation

The GPU path now has an in-process NVML command transport and a domain backend
contract, but it is deliberately not wired into runtime authority.

### Native NVML surface

NvmlClient resolves the following exports optionally so existing telemetry does
not fail merely because clock-control exports are unavailable on a driver:

- nvmlDeviceSetGpuLockedClocks
- nvmlDeviceResetGpuLockedClocks
- nvmlDeviceGetClockInfo

The set/reset transport methods perform exactly one native invocation. They do
not retry and do not silently reinitialize NVML after a write result. A future
journaled owner must decide what to do with an ambiguous result.

### Verification limitation discovered during API review

The public NVML command surface provides set/reset for GPU locked clocks, but
does not provide a getter for the exact min/max range previously requested by
nvmlDeviceSetGpuLockedClocks. nvmlDeviceGetClockInfo reports the current
graphics clock only.

Therefore the current graphics clock is explicitly modeled as an observation,
not as exact locked-range readback and not as proof of VFC ownership. Step 5.8
must solve ownership/interference semantics without pretending that a sampled
frequency is equivalent to CPU RAPL raw readback.

### Domain backend contract

NvmlGpuClockLimitBackend exposes:

- SetLockedGraphicsClocks(request)
- ResetLockedGraphicsClocks()
- ReadObservation()
- typed capabilities and failure classification

The backend contains no journal, no retry budget, no AC/Battery policy and no
ownership state. Each write method maps to at most one native mutation.

The production write gate defaults to closed. Constructing the backend normally
sets HardwareWritesAuthorized=false, so Set/Reset return WriteGateClosed and
make zero native write calls. Hardware-free fixtures may opt into the fake
transport solely to validate one-call semantics.

The fixtures prove:

- default gate closed -> zero native writes;
- accepted set -> exactly one transport call;
- rejected set -> no hidden retry;
- reset -> exactly one transport call;
- current clock observation never becomes ownership proof;
- missing exports fail closed;
- GPU_IS_LOST is classified as device unavailable for the future driver-reset
  recovery path.

No NvmlClient is instantiated by these fixtures and no physical GPU write is
performed. productionHardwareWritesAuthorized remains false.

## Step 5.8A — GPU ownership observability gate

A review of the public NVML clock-control/query surface changes the GPU
ownership design materially.

nvmlDeviceSetGpuLockedClocks accepts a requested min/max range and
nvmlDeviceResetGpuLockedClocks releases it, but public NVML does not expose a
getter that returns the exact min/max range installed by that command.
nvmlDeviceGetClockInfo returns the current frequency only.

Two additional read-only signals are now exposed for qualification:

- nvmlDeviceGetClock(..., NVML_CLOCK_ID_APP_CLOCK_TARGET), which reports the
  deprecated application-clock target;
- nvmlDeviceGetCurrentClocksEventReasons, which reports clock event-reason
  bits.

Neither signal is documented as the exact locked min/max range or as process
ownership. The locked-clock command supersedes application clocks, so a
numerically matching application target must not be promoted to lock-range
readback. Event reasons can show that a clock policy is affecting clocks but
cannot identify the writer or the installed range.

GpuClockOwnershipQualification therefore fails closed unless a future backend
can provide an exact qualified min/max observation:

- successful NVML Set alone -> no managed ownership;
- current graphics clock match -> no managed ownership;
- application target match -> no managed ownership;
- clock event-reason match -> no managed ownership;
- combinations of those heuristic signals -> still no managed ownership;
- only exact observed min/max == requested min/max may enable automatic
  reacquire and conditional reset.

This means the originally planned GPU 5x/30s bounded reacquire policy remains
closed under NVML-only observability. Implementing it anyway would risk an
undetectable write war or resetting a lock installed by another application.

The next qualification step is read-only physical characterization on the
exact RTX 4060 Laptop target. It may tell us which diagnostic signals are
useful operationally, but it cannot by itself convert an undocumented
heuristic into exact ownership.

productionHardwareWritesAuthorized remains false.

## Step 5.8B — read-only exact-target observability probe

A separate VictusFanControl.GpuProbe executable now captures candidate NVML
signals on the exact RTX 4060 Laptop target without any GPU write:

- current graphics clock;
- deprecated application graphics-clock target;
- current clock event reasons;
- export availability;
- the ownership qualification result for both 210..1850 and 210..1200.

The probe never calls nvmlDeviceSetGpuLockedClocks,
nvmlDeviceResetGpuLockedClocks or nvidia-smi. Its --self-test path does not
load NVML at all, so CI remains hardware-free.

scripts/test-gpu-nvml-observability.ps1 builds and invokes the read-only probe.
The intended physical characterization is to capture separate labeled reports
while the operator independently establishes baseline, AC-lock, Battery-lock
and reset states. Those reports may establish useful target-specific
diagnostics, but Step 5.8A still forbids treating them as exact min/max
ownership unless an exact getter is found.

No GPU hardware qualification PASS is claimed by this commit.

## Step 5.8C — physical observability result and ownership decision

Four read-only captures were taken on the exact HP-8C40-9D0R1LA-F18 target
around operator-established GPU states:

| State | Current graphics clock | APP_CLOCK_TARGET | Clock event reasons | Exact locked range |
|---|---:|---:|---:|---|
| baseline | 2010 MHz | NVML error 3 | 36 | unavailable |
| AC 210..1850 | 1260 MHz | NVML error 3 | 36 | unavailable |
| Battery 210..1200 | 1200 MHz | NVML error 3 | 36 | unavailable |
| reset | 1125 MHz | NVML error 3 | 36 | unavailable |

The result is decisive for the current design:

- the instantaneous graphics clock is not a lock-range getter; it changed in
  all states and only coincidentally equaled 1200 MHz in the Battery capture;
- the application-clock target path is unsupported on this target
  (NVML result 3 in all four captures);
- the clock-event-reasons bitmap remained exactly 36 in every state and cannot
  distinguish baseline, either locked preset, or reset;
- public NVML still exposes no exact installed min/max locked range.

Therefore the exact-ownership safety gate remains closed. The planned GPU
5x/30s bounded reacquire policy is NOT authorized under NVML-only
observability, because VFC cannot reliably detect that another application
replaced the clock range. Conditional automatic reset is also NOT authorized:
without exact ownership evidence it could erase another application's lock.

This does not invalidate the physical feasibility of the user-requested
210..1850 and 210..1200 presets. It means only that the stronger product
semantics requested for coexistence with external writers cannot be proven by
the available public NVML read surface.

A consolidated evidence record is stored at:

    release/gpu-nvml-observability-8c40-2026-10-04.json

The next research gate is to look for an exact, supported read surface outside
the current NVML query set. Official NVAPI current/base/boost clock queries and
CUPTI lock-status APIs may be useful diagnostics, but neither should be treated
as exact arbitrary min/max ownership unless the API contract explicitly
provides that information. Undocumented/private driver interfaces must not be
promoted to production without a separate stability and compatibility gate.

productionHardwareWritesAuthorized remains false.

## Step 5.9A — read-only GPU power-limit qualification surface

The next GPU performance-control candidate is the NVML power-management limit,
because unlike locked graphics clocks, public NVML provides an exact getter for
the configured power-management limit.

NvmlClient now resolves the following power-management exports optionally:

- nvmlDeviceGetPowerManagementMode
- nvmlDeviceGetPowerManagementLimit
- nvmlDeviceGetPowerManagementDefaultLimit
- nvmlDeviceGetPowerManagementLimitConstraints
- nvmlDeviceGetEnforcedPowerLimit
- nvmlDeviceSetPowerManagementLimit (presence only; never called by this step)

The dedicated INvmlGpuPowerLimitReadTransport contains no setter method. The
setter export is exposed only as capability metadata.

GpuPowerLimitQualification is pure and read-only. A target becomes a candidate
for a later controlled write qualification only when power management is
enabled, current/default/min/max are readable and internally consistent,
min < max proves an adjustable range, and the setter export exists.

Even then ProductionWriteAuthorized remains false. A successful read-only
qualification means only that a carefully bounded physical write/readback/
restore experiment may be worth performing next.

The configured power-management limit and enforced power limit are modeled
separately. nvmlDeviceGetPowerManagementLimit is the configured field a future
setter would mutate; nvmlDeviceGetEnforcedPowerLimit may be lower because
other limiters can participate in the effective cap.

Hardware-free fixtures cover adjustable, fixed-range, disabled, missing getter,
missing setter-export and configured-vs-enforced cases.

No GPU power-limit write path exists in this step.
productionHardwareWritesAuthorized remains false.

## Step 5.9B — exact-target GPU power-limit read-only probe

VictusFanControl.GpuProbe schema v2 now captures the public NVML
power-management surface in the same read-only artifact used for clock
observability:

- power-management mode;
- configured power-management limit;
- default power-management limit;
- min/max power-limit constraints;
- enforced power limit;
- presence of the nvmlDeviceSetPowerManagementLimit export;
- the pure GpuPowerLimitQualification result.

The probe still exposes no write command. It never invokes
nvmlDeviceSetPowerManagementLimit, GPU clock Set/Reset, or nvidia-smi.

The physical gate is intentionally two-stage. First, run this read-only probe
on HP-8C40-9D0R1LA-F18 and inspect whether the RTX 4060 Laptop reports an
enabled, adjustable, internally consistent power range. Only if that succeeds
will a later commit add a separately token-gated one-write/readback/restore
qualification harness.

A readable adjustable range plus setter-export presence is not itself proof
that the laptop permits the setter. It authorizes only the design of the
controlled write qualification; ProductionWriteAuthorized remains false.

No GPU power-limit hardware PASS is claimed by this step.

## Step 5.9C — legacy power getter result and requested-limit field fallback

The first physical power-limit read-only capture on HP-8C40-9D0R1LA-F18
showed a mixed NVML surface:

- nvmlDeviceSetPowerManagementLimit export: present;
- nvmlDeviceGetPowerManagementMode: NVML error 3 (not supported);
- nvmlDeviceGetPowerManagementLimit: NVML error 3 (not supported);
- default limit: 60000 mW;
- constraints: 5000..75000 mW;
- enforced limit: 70000 mW.

Therefore the Step 5.9A legacy-getter ownership route remains blocked. The
driver advertises an adjustable range and the setter export exists, but the
legacy configured-limit getter cannot provide exact readback. No write test is
authorized on that evidence.

A second official NVML read surface was identified before abandoning the power
path: nvmlDeviceGetFieldValues. Current NVIDIA headers/documentation define:

- NVML_FI_DEV_POWER_MIN_LIMIT = 187;
- NVML_FI_DEV_POWER_MAX_LIMIT = 188;
- NVML_FI_DEV_POWER_DEFAULT_LIMIT = 189;
- NVML_FI_DEV_POWER_CURRENT_LIMIT = 190;
- NVML_FI_DEV_POWER_REQUESTED_LIMIT = 192.

The requested-limit field is specifically documented as the power limit
requested by NVML or another userspace client. That makes it materially
different from the enforced/current field and a candidate exact ownership
field for a future controller.

NvmlClient now exposes a read-only INvmlGpuPowerFieldReadTransport that queries
all five fields in one nvmlDeviceGetFieldValues call. Each field retains both
the top-level query result and its individual nvmlReturn/value type. Only
unsigned integral value types are accepted for power-limit qualification.

GpuPowerFieldQualification fails closed unless min/max/default/requested are
all readable, internally consistent and the requested value lies inside the
advertised range. A successful field qualification still does not authorize a
production write; it authorizes only designing the one-write/readback/restore
physical qualification.

This changes the next gate:

1. run the read-only schema-v3 probe on the exact target;
2. if NVML_FI_DEV_POWER_REQUESTED_LIMIT is readable, consider a token-gated
   physical write/readback/restore harness;
3. if the requested field is unsupported too, close the exact-ownership GPU
   power-limit path on this target.

The legacy physical capture is consolidated in:

    release/gpu-nvml-power-read-8c40-2026-10-04.json

No GPU power-limit setter is called by Step 5.9C.
productionHardwareWritesAuthorized remains false.

## Step 5.10A — GPU locked-clock ActiveUnverified session controller

The product path returns to the originally selected GPU strategy:

- AC: 210..1850 MHz;
- Battery: 210..1200 MHz;
- direct NVML locked-graphics-clock control;
- no periodic nvidia-smi enforcement;
- no GPU power-limit product path.

The clock controller deliberately does NOT reuse CPU RAPL ownership semantics.
Public NVML cannot read back the exact installed min/max locked range on this
target, so a successful Set enters ActiveUnverified rather than Owned.

A dedicated GPU journal is introduced with these phases:

- ApplyWriteArmed;
- ActiveUnverified;
- PresetSwitchWriteArmed;
- ReleaseWriteArmed;
- RecoveryRequired.

The GPU journal is physically and semantically independent from the CPU RAPL
journal.

### Normal in-process contract

The session controller follows an exclusive-controller contract while active:
VFC is the only application expected to modify locked graphics clocks during a
managed session.

Apply:
    durable ApplyWriteArmed
    -> exactly one SetGpuLockedClocks
    -> durable ActiveUnverified

AC <-> Battery switch:
    durable PresetSwitchWriteArmed with old+pending request
    -> exactly one SetGpuLockedClocks
    -> durable ActiveUnverified

Normal release:
    durable ReleaseWriteArmed
    -> exactly one ResetGpuLockedClocks
    -> delete journal
    -> Disabled

No baseline reset occurs between enabled AC/Battery presets.

### Explicit limitations

Because exact range readback is unavailable:

- there is no automatic external-writer detection;
- there is no GPU 5x/30s reacquire loop;
- there is no polling/re-enforcement loop;
- ActiveUnverified must never be reported as exact ownership.

### Crash, suspend and driver-reset behavior

Any persisted GPU session journal found after process restart causes
RecoveryRequired with zero automatic GPU writes. The restarted process does
not reapply the preset and does not issue a blind ResetGpuLockedClocks.

Suspend/resume, GPU_IS_LOST, driver reload or another event that invalidates
the NVML session may call MarkAuthorityUnknown. That persists
RecoveryRequired and performs zero Set/Reset calls.

Dispose also performs zero implicit reset writes. A normal controlled shutdown
must explicitly call Release while the original active process still owns the
exclusive-controller contract.

Fixtures prove journal-before-write ordering, one-write apply/switch/release,
zero-write journal failures, stale-journal recovery blocking, authority-loss
invalidation and zero-write Dispose behavior.

This step remains software/fixture qualification only. The production NVML
write gate is still closed and no new physical GPU write is authorized here.

## Step 5.10B — confirmed AC/Battery dispatcher for GPU clocks

GpuClockPresetTransitionController now connects the shared confirmed
PerformancePowerSourceKind to an already-established GPU clock session.

It deliberately has no startup Apply authority:

- session Disabled + AC/Battery detected -> zero writes, NoActiveSession;
- session ActiveUnverified + enabled destination -> direct SwitchPreset;
- same enabled destination already active -> zero-write no-op;
- disabled destination -> normal journaled Release;
- Unknown source -> normal journaled Release;
- RecoveryRequired/Applying/Switching/Releasing/Failed/Unsupported -> blocked.

The AC/Battery values remain exactly:

- AC: 210..1850 MHz;
- Battery: 210..1200 MHz.

An enabled AC<->Battery transition therefore remains one direct
nvmlDeviceSetGpuLockedClocks call after PresetSwitchWriteArmed and never
performs an intermediate ResetGpuLockedClocks.

Disabled/Unknown release is allowed only while the original live process is in
ActiveUnverified under the exclusive-controller contract. A stale journal after
restart remains RecoveryRequired and cannot use a source event to perform Set
or Reset.

This dispatcher still does not own Windows power notifications. Step 6 will
confirm AC/DC with a real OS query before invoking it.

No production GPU write gate, GUI authority, startup persistence or automatic
profile integration is enabled by this step.

## Step 5.10C — qualification-only direct NVML clock write harness

A dedicated qualification harness now exercises the direct C# -> NVML
locked-clock command path without enabling product/runtime authority.

The harness accepts only the two fixed product presets:

- ac -> 210..1850 MHz;
- battery -> 210..1200 MHz.

It rejects arbitrary clock ranges and requires the exact target confirmation
token HP-8C40-9D0R1LA-F18 before loading NVML or authorizing a write.

The transaction uses the same GPU session controller and journal semantics:

    exact target token
    -> durable ApplyWriteArmed
    -> one nvmlDeviceSetGpuLockedClocks
    -> ActiveUnverified
    -> short bounded hold
    -> durable ReleaseWriteArmed
    -> one nvmlDeviceResetGpuLockedClocks
    -> journal delete

If Apply does not complete normally, or the session reaches RecoveryRequired,
the journal is retained and a second write test is blocked until that evidence
is reviewed. The harness never retries a Set/Reset implicitly and never uses
nvidia-smi.

A successful harness run qualifies only the direct NVML command path and normal
in-process release. It does NOT claim exact locked-range readback or exact
ownership because public NVML still lacks that getter.

CI runs only --clock-write-self-test through
scripts/test-gpu-nvml-clock-write.ps1 -SelfTest. That path validates the exact
target token, fixed-preset restriction and bounded hold arguments without
loading NVML or performing hardware I/O.

The explicit physical commands are:

    .\scripts\test-gpu-nvml-clock-write.ps1 -Preset ac -ConfirmTargetProfile HP-8C40-9D0R1LA-F18

and:

    .\scripts\test-gpu-nvml-clock-write.ps1 -Preset battery -ConfirmTargetProfile HP-8C40-9D0R1LA-F18

productionHardwareWritesAuthorized remains false. The qualification harness has
its own explicit, one-shot authorization path and is not wired to GUI, startup,
power-source notifications or automatic profile integration.

## Step 5.10C physical result — direct NVML preset Set/Reset

The qualification-only C# -> NVML path has now been exercised physically on
HP-8C40-9D0R1LA-F18 / NVIDIA GeForce RTX 4060 Laptop GPU for both product
presets.

AC qualification:

- request: 210..1850 MHz;
- Apply succeeded;
- observation before: 1800 MHz;
- observation during: 1845 MHz;
- normal Release/Reset succeeded;
- final session state: Disabled;
- exception: none.

Battery qualification:

- request: 210..1200 MHz;
- Apply succeeded;
- observation before: 1890 MHz;
- observation during: 1200 MHz;
- normal Release/Reset succeeded;
- final session state: Disabled;
- exception: none.

This physically qualifies the direct nvmlDeviceSetGpuLockedClocks command path
for both fixed presets and the normal in-process
nvmlDeviceResetGpuLockedClocks release path.

It still does NOT provide exact installed min/max readback. The observed
graphics clock remains telemetry only, ActiveUnverified remains the correct
session state, and exact ownership is not claimed.

The consolidated evidence record is:

    release/gpu-clock-nvml-preset-write-qualification-8c40-2026-10-04.json

The next physical gate is a single live session:

    AC 210..1850
    -> Battery 210..1200
    -> AC 210..1850
    -> Reset

with no intermediate Reset between enabled presets.

productionHardwareWritesAuthorized remains false.

## Step 5.10D — direct AC -> Battery -> AC transition qualification harness

The next qualification harness exercises the exact product transition sequence
inside one live GPU clock session:

    Apply AC 210..1850
    -> direct SwitchPreset to Battery 210..1200
    -> direct SwitchPreset back to AC 210..1850
    -> one final normal Reset

There is no ResetGpuLockedClocks call between enabled presets.

The harness uses the production-domain GpuClockSessionController and
GpuClockPresetTransitionController rather than issuing raw NVML calls directly.
That means the same durable ordering is exercised:

- ApplyWriteArmed before the first Set;
- PresetSwitchWriteArmed before AC -> Battery Set;
- PresetSwitchWriteArmed before Battery -> AC Set;
- ReleaseWriteArmed before the single final Reset.

The physical entry point requires the exact target token and accepts no custom
clock range or custom sequence:

    .\scripts\test-gpu-nvml-clock-transition.ps1 -ConfirmTargetProfile HP-8C40-9D0R1LA-F18

The default hold is 3 seconds per enabled preset and is bounded to 1..30
seconds.

If a transition enters RecoveryRequired, the harness retains the durable
journal and performs no blind Reset. If an exception occurs while the original
live session is still ActiveUnverified, the harness may use the ordinary
journaled Release path as same-session safety cleanup.

CI executes only the argument/token self-test. The CI path never loads NVML and
never performs hardware I/O.

A successful physical run will qualify the direct enabled-preset switching
path, but it still cannot prove exact locked min/max ownership because the
public NVML getter does not exist.

productionHardwareWritesAuthorized remains false.

## Step 5.10D physical result — direct enabled-preset switching

The direct in-session transition harness has now passed physically on
HP-8C40-9D0R1LA-F18 / NVIDIA GeForce RTX 4060 Laptop GPU.

Observed sequence:

    baseline observation: 810 MHz
    -> AC request 210..1850, observed 1845 MHz
    -> direct Battery switch 210..1200, observed 1200 MHz
    -> direct AC switch 210..1850, observed 1845 MHz
    -> one final normal Reset, final session state Disabled

Both preset-transition results were successful and reported
GPU_CLOCK_PRESET_SWITCHED. The final Release succeeded, ExitCode was 0,
no safety cleanup was required and no exception was recorded.

The harness was constructed so enabled AC/Battery transitions use
PresetSwitchWriteArmed + one SetGpuLockedClocks and do not issue an
intermediate Reset. The final Reset is only the normal end-of-session release.

This physically qualifies:

- direct C# -> NVML initial AC apply;
- AC -> Battery enabled-preset switch;
- Battery -> AC enabled-preset switch;
- one final normal release/reset.

It still does not qualify exact ownership. CurrentGraphicsClockMHz is
observation-only and public NVML still cannot return the installed arbitrary
min/max locked range. ActiveUnverified remains the correct GPU session state.

Consolidated evidence:

    release/gpu-clock-nvml-transition-qualification-8c40-2026-10-04.json

productionHardwareWritesAuthorized remains false.

## Step 6A — direct Windows AC/DC confirmation query

With CPU preset switching and GPU locked-clock preset switching independently
qualified, the next integration gate starts with source confirmation only.

A future Windows power notification is treated only as a trigger. Before any
CPU or GPU preset transition, PerformanceGuardian must query the current source
directly from GetSystemPowerStatus.

SystemPowerStatusReader now preserves the raw ACLineStatus byte in addition to
the existing AcOnline compatibility property. The performance mapper uses the
documented values:

- ACLineStatus 1 -> PerformancePowerSourceKind.Ac;
- ACLineStatus 0 -> PerformancePowerSourceKind.Battery;
- ACLineStatus 255 or any unexpected value -> Unknown.

Unknown is fail-closed and must never be coerced to Battery.

WindowsPerformancePowerSourceReader exposes a typed read-only observation with
the mapped source, raw AC line status, battery percentage and battery flags.
Query failure also maps to Unknown with no preset authority.

A separate VictusFanControl.PerformanceProbe and
scripts/test-performance-power-source.ps1 provide a read-only exact-target
characterization path. The probe performs one GetSystemPowerStatus query and
no CPU/GPU hardware write.

CI exercises only the source-mapping self-test. The next physical gate is to
capture one report while the charger is connected and one while physically
running on battery. Only after both direct-query states are confirmed will a
Windows notification listener be allowed to dispatch confirmed source changes
to the CPU/GPU transition controllers.

No notification listener, guardian IPC, startup Apply authority, GUI authority
or automatic profile integration is enabled by Step 6A.

## Step 6A physical result — direct Windows AC/DC query

The direct Windows source query has now passed physically on the exact target.

Connected AC capture:

- GetSystemPowerStatus succeeded;
- RawAcLineStatus = 1;
- mapped source = Ac;
- status = WINDOWS_POWER_SOURCE_CONFIRMED_AC;
- no hardware writes.

Battery capture after physically disconnecting the charger:

- GetSystemPowerStatus succeeded;
- RawAcLineStatus = 0;
- mapped source = Battery;
- status = WINDOWS_POWER_SOURCE_CONFIRMED_BATTERY;
- no hardware writes.

This qualifies the direct confirmation source that PerformanceGuardian will use
after a Windows power notification. Notification payloads themselves will not
be trusted as preset authority.

Consolidated evidence:

    release/performance-power-source-query-8c40-2026-10-04.json

## Step 6B — Windows power-source notification trigger qualification

Microsoft documents RegisterPowerSettingNotification for applications using a
window handle. GUID_ACDC_POWER_SOURCE notifications arrive as
WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE events.

A new read-only PerformanceProbe qualification harness registers specifically
for GUID_ACDC_POWER_SOURCE:

    5D3E9A59-E9D5-4B00-A6BD-FF34FF516548

The native POWERBROADCAST_SETTING data is deliberately not used to select a
preset. The notification is only a trigger. On each matching notification the
harness performs a fresh WindowsPerformancePowerSourceReader.Read(), which in
turn calls GetSystemPowerStatus. Only that direct query may confirm Ac or
Battery.

The physical harness is:

    .\scripts\test-performance-power-source-notification.ps1 -Expect battery

or:

    .\scripts\test-performance-power-source-notification.ps1 -Expect ac

It requires the initial direct source to differ from the expected destination,
then waits up to 60 seconds by default for a GUID_ACDC_POWER_SOURCE signal.
The destination is PASS only when the post-notification direct query matches the
requested source.

The harness uses a hidden WinForms NativeWindow solely to host the registered
Windows power-setting notification. It performs no RAPL, NVML, WMI or EC
hardware write.

CI runs only --notification-self-test, which validates argument/timeout gates
without registering a native listener.

No CPU/GPU transition controller is connected to the native listener yet.
That dispatch remains the next gate after physical notification qualification.

## Step 6B physical result — Windows notification trigger

GUID_ACDC_POWER_SOURCE notification qualification now passes physically in
both directions on HP-8C40-9D0R1LA-F18.

AC -> Battery:

- initial direct source: Ac, RawAcLineStatus=1;
- registration produced an immediate same-source notification, again confirmed
  as Ac by GetSystemPowerStatus;
- after physical charger removal, a second notification arrived;
- the direct query confirmed Battery, RawAcLineStatus=0;
- ExpectedSourceConfirmed=true;
- ListenerError=null;
- no hardware writes.

Battery -> AC:

- initial direct source: Battery, RawAcLineStatus=0;
- registration again produced an immediate same-source notification;
- after physical charger insertion, a second notification arrived;
- the direct query confirmed Ac, RawAcLineStatus=1;
- ExpectedSourceConfirmed=true;
- ListenerError=null;
- no hardware writes.

The initial same-source signal is important: the production coordinator must
prime its source before dispatch and suppress duplicate same-source
notifications, otherwise listener registration could cause an unnecessary
preset operation.

Consolidated evidence:

    release/performance-power-source-notification-8c40-2026-10-04.json

## Step 6C — source transition coordinator

PerformanceSourceTransitionCoordinator now provides the software-only bridge
between a power-source notification trigger and the independent CPU/GPU domain
transition controllers.

Rules:

- every signal performs one fresh IPerformancePowerSourceReader.Read();
- the notification payload itself never selects a preset;
- the first direct observation only primes current source and dispatches zero
  CPU/GPU operations;
- repeated notifications whose direct-query source equals the current source
  are suppressed;
- a real source change is dispatched independently to CPU and GPU;
- a CPU failure/exception does not suppress the GPU attempt;
- a GPU failure/exception does not roll back or corrupt the CPU domain;
- query failure maps to Unknown and causes one fail-closed Unknown dispatch
  when transitioning away from a previously known source;
- repeated Unknown/query-failure signals are suppressed to avoid retry storms.

CpuPowerPresetTransitionController and GpuClockPresetTransitionController now
implement narrow source-transition sink interfaces consumed by the coordinator.
Their existing per-domain journal/write semantics are unchanged.

The coordinator does not grant startup Apply authority. If the underlying CPU
limiter or GPU session is Disabled, their existing transition controllers still
refuse to create a new active session merely because a source signal arrived.

Hardware-free fixtures cover priming, duplicate suppression, confirmed
AC/Battery dispatch, independent-domain failure handling, fail-closed Unknown
dispatch and unprimed signal behavior.

Step 6C still does not connect the native Windows listener to real CPU/GPU
hardware writes. That combined live guardian path remains a later gate.

productionHardwareWritesAuthorized remains false.

## Step 6C physical-composition harness

A final read-only composition harness is added before any notification-driven
hardware write is considered.

The harness:

1. primes PerformanceSourceTransitionCoordinator with one direct
   GetSystemPowerStatus read;
2. registers the already-qualified GUID_ACDC_POWER_SOURCE Windows listener;
3. feeds each matching notification into the coordinator;
4. confirms the source again through GetSystemPowerStatus;
5. suppresses same-source notifications;
6. sends a real changed source to recording CPU and GPU sinks only.

The recording sinks implement the same narrow interfaces as the real CPU and
GPU transition controllers but perform zero hardware I/O.

Physical entry points:

    .\scripts\test-performance-source-coordinator.ps1 -Expect battery

and:

    .\scripts\test-performance-source-coordinator.ps1 -Expect ac

PASS requires the expected destination to be directly confirmed and dispatched
exactly once to both recording domains.

This specifically validates the initial same-source notification behavior seen
in Step 6B: registration may emit an immediate event for the current source,
which must remain a zero-dispatch duplicate.

No CPU RAPL write, GPU NVML Set/Reset, WMI write or EC write exists in this
qualification path.

Only after this composition gate passes should the detached PerformanceGuardian
process be allowed to bind the native listener to the real per-domain
controllers under explicit runtime/startup authority rules.

## Step 6C physical result — notification/query/dedup/dispatch composition

The read-only composition harness has now passed physically in both directions
on HP-8C40-9D0R1LA-F18.

AC -> Battery:

- prime direct query: Ac, RawAcLineStatus=1;
- initial same-source notification: suppressed;
- suppressed event performed zero CPU and zero GPU dispatches;
- charger removal direct query: Battery, RawAcLineStatus=0;
- exactly one CPU recording dispatch;
- exactly one GPU recording dispatch;
- both recording domains returned success;
- listener error: none;
- hardware writes: none.

Battery -> AC:

- prime direct query: Battery, RawAcLineStatus=0;
- initial same-source notification: suppressed;
- suppressed event performed zero CPU and zero GPU dispatches;
- charger insertion direct query: Ac, RawAcLineStatus=1;
- exactly one CPU recording dispatch;
- exactly one GPU recording dispatch;
- both recording domains returned success;
- listener error: none;
- hardware writes: none.

This physically qualifies the complete read-only composition:

    GUID_ACDC_POWER_SOURCE
    -> fresh GetSystemPowerStatus query
    -> source comparison / duplicate suppression
    -> independent CPU and GPU dispatch

It does not yet bind the dispatcher to physical RAPL/NVML controllers.

Consolidated evidence:

    release/performance-source-coordinator-qualification-8c40-2026-10-04.json

The next gate is the detached PerformanceGuardian process/lifecycle and its
explicit session/startup authority. Only after that lifecycle contract is
qualified should the native listener be bound to real CPU/GPU controllers.

productionHardwareWritesAuthorized remains false.

## Step 6D — detached PerformanceGuardian lifecycle and semantic IPC

A new VictusFanControl.PerformanceGuardian executable is introduced as a
software-only lifecycle/IPC gate. This step deliberately does not expose a
production run mode and does not bind real CPU RAPL or GPU NVML hardware.

### Launch identity and single instance

The guardian launch tuple is fixed by the launcher:

- exact target profile id;
- owner PID;
- owner process start UTC ticks;
- random session nonce;
- named-pipe name.

The guardian opens and retains a real Process handle for the owner and waits on
that handle for liveness. PID polling is not used.

A target-scoped named mutex prevents two guardians from becoming the
performance writer at the same time. The native process fixture uses a
fixture-specific mutex name to avoid unrelated CI collisions while preserving
the same duplicate-guardian behavior.

### Semantic IPC only

The bounded length-prefixed JSON protocol exposes only:

- HELLO;
- ENABLE_SESSION;
- DISABLE_SESSION;
- STATUS;
- SHUTDOWN.

There is no raw MSR value, arbitrary PL1/PL2 value, arbitrary GPU clock range,
WMI method id, EC offset or generic hardware-write command on the wire.

Every transport connection must perform HELLO. HELLO must match the exact
target, launch nonce, owner PID and owner start time. Reconnect with the same
launch tuple is allowed and preserves the live session.

### Startup/apply authority

Merely starting PerformanceGuardian, observing AC/Battery or receiving a power
notification grants zero startup Apply authority.

ENABLE_SESSION is the only semantic command that grants live startup/apply
authority, and it can enable CPU, GPU or both domains. An active domain
selection cannot silently mutate; it must be disabled before a different
selection is enabled.

Recovery authority remains separate. This gate does not convert a stale CPU or
GPU journal into startup authority.

### Parent death and cleanup

If the owner process handle signals exit, the detached guardian remains alive
long enough to release the live session through its domain-lifecycle interface,
revokes authority, records ParentLost and exits.

For Step 6D the domain lifecycle is a recording implementation only. It counts
enable/release operations and performs zero hardware I/O.

The native Windows process fixture proves:

- wrong nonce is rejected;
- explicit CPU+GPU session enable succeeds;
- pipe reconnect with the same launch tuple preserves the session;
- a second guardian sharing the same mutex is rejected;
- the guardian survives abrupt launcher exit;
- owner-handle death is detected without PID polling;
- exactly one simulated release occurs on parent death;
- the guardian exits and releases its named mutex;
- zero hardware writes occur.

Production run mode, real power-source listener binding, real RAPL/NVML domain
construction, GUI client integration and startup persistence remain closed.

productionHardwareWritesAuthorized remains false.
guiIntegrationAuthorized remains false.
startupPersistenceAuthorized remains false.
automaticProfileIntegrationAuthorized remains false.

### Side-effect envelope hardening

DISABLE_SESSION and SHUTDOWN are cleanup-capable commands. Their protocol
version, request id, exact target and session nonce are now validated before any
domain Release callback can run. A malformed or wrong-nonce request therefore
cannot trigger cleanup merely by naming a side-effect command.

The detached-process fixture also waits for the guardian process itself to exit
before asserting named-mutex release, avoiding a report-file/mutex-release race
in the test.

## Step 6E — Guardian AC/Battery listener with recording CPU/GPU domains

The detached PerformanceGuardian now owns the qualified Windows
GUID_ACDC_POWER_SOURCE listener for the lifetime of an explicitly enabled
performance session. This remains a software/lifecycle qualification gate:
the Guardian uses recording CPU/GPU source sinks only and performs zero Intel
RAPL writes and zero NVML Set/Reset operations.

### Authority and startup ordering

The live ordering is now:

    Guardian process start
      -> HELLO with exact launch tuple
      -> explicit ENABLE_SESSION
      -> direct GetSystemPowerStatus prime
      -> RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)
      -> WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE
      -> fresh GetSystemPowerStatus query
      -> source comparison / duplicate suppression
      -> selected recording CPU/GPU sink dispatch

Starting the Guardian does not register the source listener and does not create
Apply authority. A HELLO or a source observation also grants no Apply
authority. Only an accepted ENABLE_SESSION starts the source runtime.

The prime always happens before listener registration. This preserves the
physical Step 6B/6C observation that Windows may emit an immediate
same-source notification during registration: that event is compared against
the primed source and remains a zero-dispatch duplicate.

The native POWERBROADCAST_SETTING.Data payload is still ignored. Every
matching notification is only a trigger for a fresh direct
WindowsPerformancePowerSourceReader.Read() / GetSystemPowerStatus query.

### Explicit domain selection

PerformanceSourceTransitionCoordinator now accepts an explicit CPU/GPU
dispatch mask while preserving the original parameterless both-domain path.
This is required because ENABLE_SESSION may authorize CPU only, GPU only or
both. A source transition never invokes a domain that was not explicitly
selected by the active Guardian session.

Selected domains remain independent:

- CPU recording failure does not block the GPU recording attempt;
- GPU recording failure does not revert or suppress a successful CPU recording
  attempt;
- Unknown/query failure is dispatched once as the existing fail-closed Unknown
  episode, then repeated Unknown notifications are suppressed;
- Disabled/no-session state has no listener and therefore no
  notification-driven startup path.

### Listener/session lifecycle

The Windows listener runs on a dedicated STA message thread and registers only
for:

    GUID_ACDC_POWER_SOURCE
    5D3E9A59-E9D5-4B00-A6BD-FF34FF516548

DISABLE_SESSION and SHUTDOWN first pass the existing side-effect envelope
validation. A wrong/stale nonce therefore cannot unregister the listener or
invoke release merely by naming a cleanup-capable command.

For a valid cleanup, notification dispatch is fenced before listener
unregistration and before the recording domain Release callback.

The listener is session-scoped rather than pipe-connection-scoped. A client may
disconnect and reconnect with the same exact launch tuple while the source
runtime stays active. Reconnect does not re-prime the source and does not
register a second listener.

If the held owner Process handle signals exit while the listener is active,
the Guardian:

1. fences/unregisters source notifications;
2. releases the active recording domain lifecycle exactly once;
3. marks authority ParentLost;
4. writes final evidence;
5. exits and releases the target-scoped mutex.

### Step 6E fixtures

The Guardian self-test now covers with synthetic source observations and a
manual listener:

- no listener and no dispatch before explicit Start/ENABLE authority;
- direct AC prime before registration;
- initial same-source notification suppression;
- AC -> Battery recording dispatch;
- Battery -> AC recording dispatch;
- one fail-closed Unknown/query-failure dispatch followed by duplicate
  suppression;
- CPU recording failure while GPU still executes;
- GPU recording failure without reverting CPU;
- explicit CPU-only domain selection with zero GPU sink calls.

The detached native-process fixture additionally uses the real Windows
notification registration path and proves:

- exact HELLO + explicit CPU/GPU ENABLE_SESSION;
- source runtime starts once and listener registers once;
- wrong/stale nonce DISABLE_SESSION is rejected while the active session
  remains intact;
- pipe reconnect preserves the same session and does not register a second
  listener;
- duplicate Guardian loses the named-mutex writer gate;
- abrupt owner death while the listener is active stops the source runtime,
  performs one simulated domain release and exits;
- named mutex is released after Guardian exit;
- HardwareWritesPerformed remains false.

Step 6E therefore qualifies Guardian ownership of the Windows source listener
and its semantic/lifecycle composition with recording domains. It does not
qualify physical notification-driven CPU/GPU mutation.

productionHardwareWritesAuthorized remains false.
guiIntegrationAuthorized remains false.
startupPersistenceAuthorized remains false.
automaticProfileIntegrationAuthorized remains false.

The next gate is **Step 6F**: replace the recording source-transition sinks
with the already-qualified real CPU RAPL and GPU locked-clock controllers under
the same explicit ENABLE_SESSION, source-prime, journal and cleanup rules. Step
6F must be introduced as a separate bounded hardware gate; no such hardware
binding is authorized by Step 6E.

## Step 6F — staged real-domain Guardian qualification

Step 6F is intentionally split instead of enabling CPU and GPU hardware
together in the first live Guardian run.

The reason is evidence, not convenience. GPU already has physically qualified
fixed product requests for both sources:

    AC      210..1850 MHz
    Battery 210..1200 MHz

CPU RAPL has physical write evidence, including the historical 20/40 W
qualification, but that value is qualification evidence rather than an agreed
product AC/Battery default. Therefore the first Step 6F hardware gate binds
only the already-qualified GPU clock domain. CPU remains explicitly disabled
and a CPU+GPU enable request is rejected by this gate before any GPU write.

productionHardwareWritesAuthorized remains false. The hardware-write permission
introduced here exists only inside the explicit bounded qualification mode.

### Step 6F.1 — source prime before initial real-domain Apply

The Step 6E source runtime has been split into two authority phases:

    ENABLE_SESSION accepted
      -> Prime()
         -> fresh GetSystemPowerStatus
      -> domain EnableAsync(initial confirmed source)
         -> journal-before-write initial Apply
      -> ActivateListener()
         -> RegisterPowerSettingNotification
      -> one post-registration direct reconciliation query
      -> normal notification/query/dedup transitions

This ordering is required once the initial source selection can cause a real
hardware mutation.

A listener is never registered before explicit ENABLE_SESSION. The initial
hardware Apply never occurs before a direct source query. Listener registration
occurs only after the initial Apply succeeds.

There is an unavoidable small interval between the source prime and listener
registration. Step 6F closes that race with one fresh direct reconciliation
query immediately after registration while callback dispatch is fenced. If the
source changed in that interval, the reconciliation performs the missed
transition. If it did not change, the result is a normal same-source duplicate.
A Windows notification queued by registration then sees the reconciled source
and is also deduplicated if appropriate.

### Step 6F.2 — GPU real-domain lifecycle

QualifiedGpuGuardianDomainLifecycle is the first non-recording Guardian domain
adapter.

It is deliberately constrained to:

    CpuEnabled = false
    GpuEnabled = true

The adapter uses the existing components rather than a second write path:

    GpuClockPresetPolicy
      -> GpuClockSessionController
      -> JsonGpuClockSessionJournal
      -> NvmlGpuClockLimitBackend
      -> NvmlClient

Initial explicit enable:

    confirmed source
      -> fixed qualified preset
      -> durable ApplyWriteArmed
      -> one nvmlDeviceSetGpuLockedClocks
      -> ActiveUnverified

Enabled AC/Battery transition:

    ActiveUnverified old preset
      -> durable PresetSwitchWriteArmed
      -> one nvmlDeviceSetGpuLockedClocks
      -> ActiveUnverified new preset

Normal release:

    ActiveUnverified
      -> durable ReleaseWriteArmed
      -> one nvmlDeviceResetGpuLockedClocks
      -> journal deleted
      -> Disabled

There is no implicit Reset from Dispose and no restart recovery Reset. A
RecoveryRequired state is not converted into normal release authority.

The Guardian report now records domain state/status and CPU/GPU hardware-write
attempt counts. This lets a bounded physical run prove that CPU performed zero
writes while the GPU followed the expected Set/Set/Set/Reset sequence.

### Step 6F.3 — persistent crash journal

The physical Step 6F journal is target-scoped and stable across qualification
runs:

    %LOCALAPPDATA%\VictusFanControl\Performance\
      HP-8C40-9D0R1LA-F18\gpu-clock-session.json

It is intentionally not stored only inside a new timestamped evidence folder.
Otherwise a crash could leave a stale journal in the old folder while a later
run starts with an apparently clean new directory.

The detached child also rejects an arbitrary journal path. Its --journal
argument must resolve exactly to the target-scoped active journal location.

If that journal exists at preflight, the new qualification performs zero
writes. It must be reviewed rather than deleted or overwritten blindly.

### Step 6F.4 — read-only preflight

Before physical qualification, the harness performs a read-only preflight:

- exact HP-8C40-9D0R1LA-F18 target;
- elevated Windows x64 process;
- production target mutex available;
- no stale target-scoped GPU journal;
- source directly confirmed as AC;
- exact NVIDIA GeForce RTX 4060 Laptop GPU found through NVML;
- locked-clock Set and Reset exports available;
- current graphics-clock observation succeeds;
- an NvmlGpuClockLimitBackend constructed with HardwareWritesAuthorized=false
  rejects a Set request at its software gate before a native mutation.

The preflight records HardwareWritesPerformed=false.

### Step 6F.5 — bounded physical GPU-only sequence

The explicit physical entry point is:

    .\scripts\test-performance-guardian-6f-gpu.ps1 \
      -ConfirmTargetProfile HP-8C40-9D0R1LA-F18 \
      -ConfirmExclusiveGpuController

The operator starts with AC connected. Because public NVML cannot query the
exact installed arbitrary locked range or identify its writer, the physical
entry point also requires an explicit -ConfirmExclusiveGpuController
acknowledgement. That acknowledgement means the operator has verified that no
pre-existing or concurrent nvidia-smi -lgc, MSI Afterburner or other
locked-clock controller owns the GPU. The harness cannot infer this safely from
current clock telemetry.

The harness builds, executes the read-only preflight and only then launches the
detached Guardian qualification.

Expected sequence:

    direct AC confirmed
      -> HELLO
      -> ENABLE_SESSION CPU=false GPU=true
      -> prime AC
      -> durable GPU ApplyWriteArmed
      -> Set 210..1850
      -> listener + reconciliation active

    disconnect charger
      -> Windows notification
      -> fresh direct Battery query
      -> durable PresetSwitchWriteArmed
      -> Set 210..1200

    reconnect charger
      -> Windows notification
      -> fresh direct AC query
      -> durable PresetSwitchWriteArmed
      -> Set 210..1850

    DISABLE_SESSION
      -> listener fenced/unregistered
      -> durable ReleaseWriteArmed
      -> one final Reset
      -> GPU Disabled
      -> journal absent

    SHUTDOWN
      -> Guardian exit
      -> mutex released

PASS requires, among other invariants:

- exactly one lifecycle enable and one normal release;
- zero CPU hardware-write attempts;
- exactly four GPU hardware-write attempts: three Sets plus one Reset;
- zero CPU source dispatch attempts;
- exactly two GPU source-transition dispatch attempts;
- at least two real Windows notification signals;
- exactly one post-registration reconciliation query;
- at least one same-source duplicate/reconciliation suppression;
- initial Guardian source Ac and final source Ac;
- final GPU session Disabled;
- no source-domain failure recorded;
- target-scoped GPU journal absent after normal release.

Current graphics clock remains observation only. Even a physical PASS of this
gate must not be described as exact locked-range ownership.

### Step 6F software fixtures and current boundary

Hardware-free fixtures now prove:

- journaled initial AC Apply through the real GPU session/controller graph;
- AC -> Battery -> AC direct preset transitions with no intermediate Reset;
- one final normal Reset;
- combined CPU+GPU enable is rejected by the first hardware gate before writes;
- Unknown initial source is rejected before writes;
- stale target journal and arbitrary child journal paths are fail-closed;
- source transition failures are retained in Guardian diagnostics;
- the prime/apply/register race is closed by post-registration reconciliation.

The CI path invokes only fake backends / argument gates for Step 6F. It does
not load NVML for a write and does not perform physical hardware I/O.

The physical GPU-only Guardian gate is **physically qualified on the exact
HP-8C40-9D0R1LA-F18 target as of 2026-10-04**. The bounded qualification run
completed with Result=PASS and GuardianExitCode=0.

### Step 6F physical qualification evidence — 2026-10-04

The accepted target evidence is committed under release/:

    performance-guardian-6f-gpu-preflight-8c40-2026-10-04.json
    performance-guardian-6f-gpu-qualification-8c40-2026-10-04.json
    performance-guardian-6f-gpu-guardian-report-8c40-2026-10-04.json

The read-only preflight confirmed the exact target, AC source, production mutex,
absence of a stale active journal, the exact NVIDIA GeForce RTX 4060 Laptop
GPU, the required NVML command surface and a closed hardware-write gate.
Preflight performed zero hardware writes.

The physical run then proved, in one Guardian session:

    initial AC      -> committed 210..1850 MHz
    Battery         -> committed 210..1200 MHz
    AC return       -> committed 210..1850 MHz
    normal release  -> one final Reset -> Disabled

The durable journal snapshots use one SessionId across the three committed
requests. ENABLE_SESSION, DISABLE_SESSION and SHUTDOWN all returned Ok=true.
The Guardian exited with code 0, with one lifecycle enable and one release,
zero rejected requests, zero CPU hardware writes, exactly four GPU hardware
write attempts, zero CPU source dispatches and exactly two GPU source
dispatches. Windows delivered three source notifications; the runtime recorded
exactly one post-registration reconciliation and two duplicate suppressions.
InitialSource and final SourceLastSource were both Ac. SourceFailure and Failure
were null. Normal release left the GPU domain Disabled and the target-scoped
journal absent.

This evidence qualifies the bounded GPU-only Guardian AC/Battery/AC transition
and normal-release path. It does **not** establish exact locked-range ownership:
public NVML still cannot read back the arbitrary locked range or identify its
writer. The explicit exclusive-controller contract therefore remains required
for this qualification model.

productionHardwareWritesAuthorized remains false. This physical PASS does not
open production, GUI, startup-persistence or automatic-profile hardware-write
authority.

CPU real-domain binding, combined CPU+GPU qualification, destructive
Guardian-death tests, suspend/resume, GPU driver-reset handling, GUI
integration and startup persistence all remain closed.

productionHardwareWritesAuthorized=false.
guiIntegrationAuthorized=false.
startupPersistenceAuthorized=false.
automaticProfileIntegrationAuthorized=false.

With the physical GPU-only Step 6F PASS complete, the CPU product values are
now explicitly defined below. The next hardware gate is CPU-only Guardian
qualification, followed only after PASS by the combined CPU+GPU source-transition
sequence.

## Step 6G.0 — explicit CPU product defaults

The operator selected the target CPU defaults for HP-8C40-9D0R1LA-F18:

    AC      PL1 35 W / PL2 60 W
    Battery PL1  8 W / PL2 15 W

These values replace the previous "undefined product default" state. The
historical 20/40 W result remains qualification evidence only and is not a
product preset.

Supporting software validation now accepts a minimum PL1 of 8 W so the Battery
preset can be represented by CpuPowerPresetPolicy/CpuPowerLimiter. This is a
software-policy change only. The existing P1 diagnostic harness keeps its own
previously-qualified 10 W explicit-write floor; it is not silently widened by
this product decision.

For user-adjustable persisted configuration on the qualified target, the
product configuration envelope is deliberately bounded below the observed
45/115 W baseline:

    PL1 8..44 W
    PL2 8..114 W
    PL2 >= PL1

The selected 35/60 and 8/15 values are the defaults inside that envelope.
Runtime Apply must still validate the current physical baseline and all existing
journal/ownership/lock conditions before any future write.

### Step 6G.0a — non-authorizing GUI sliders

The WinForms application now exposes a separate Performance tab for the exact
HP-8C40-9D0R1LA-F18 target. It contains independent AC and Battery PL1/PL2
sliders using the product envelope above. Moving a slider only edits in-memory
preferences; Save persists them to:

    %LOCALAPPDATA%\VictusFanControl\performance-ui-settings.json

The settings document persists only watts. It contains no operating authority,
Guardian session, startup-persistence flag, ownership state or automatic
profile state. Invalid/corrupt settings fail back to the exact product defaults.

PL2 >= PL1 is enforced while editing. "Restaurar predeterminados" restores and
persists AC 35/60 W and Battery 8/15 W. Both Save and Reset explicitly perform
zero hardware writes.

This GUI work is intentionally configuration-only. It does not launch the
Performance Guardian, open MSR write authority or bind saved values to a live
CPU session. That binding remains behind the CPU-only Guardian qualification
gate.

CPU Guardian hardware binding and physical CPU-only qualification remain
closed at this point. productionHardwareWritesAuthorized=false.
guiIntegrationAuthorized=false.
startupPersistenceAuthorized=false.
automaticProfileIntegrationAuthorized=false.



## Step 6G.1 — CPU-only read-only hardware preflight

Before opening any real CPU Guardian write authority, Step 6G now has a
dedicated read-only target preflight. This is intentionally separate from the
physical AC/Battery/AC write sequence.

The preflight requires Windows x64, elevation, exact HP-8C40-9D0R1LA-F18 and
i7-13700H identity, signed IntelMSR.bin, a free production target mutex, no
stale target-scoped CPU journal, and a fresh direct AC source. It reads RAPL
0x606/0x610/0x614 through PawnIO, requires PawnIO 2.2+ and 14 physical cores,
requires three identical 0x610 reads, a clear lock bit and enabled PL1/PL2, and
builds the exact AC 35/60 W and Battery 8/15 W plans against the live baseline
and live RAPL power-info constraints.

The active CPU journal path is fixed to:

    %LOCALAPPDATA%\VictusFanControl\Performance\
      HP-8C40-9D0R1LA-F18\cpu-power-session.json

The preflight constructs the CPU backend with hardwareWritesAuthorized=false
and proves that a Write call is rejected before ioctl_write_msr. Its report
therefore requires HardwareWritesPerformed=false.

The key new safety requirement is the live MSR_PKG_POWER_INFO (0x614) minimum.
The earlier physical 20/40 W qualification did not establish that an 8 W PL1
is admissible. If 0x614 reports a nonzero minimum above 8 W, Battery 8/15 is
rejected with zero writes and must be revised before physical CPU
qualification.

Run on the exact notebook with:

    .\scripts\test-performance-guardian-6g-cpu.ps1 -ConfirmTargetProfile HP-8C40-9D0R1LA-F18

This command is read-only. CI only exercises the parser/software fixtures; the
target JSON must be reviewed before opening the physical CPU gate.

productionHardwareWritesAuthorized=false.
guiIntegrationAuthorized=false.
startupPersistenceAuthorized=false.
automaticProfileIntegrationAuthorized=false.


### Step 6G.1 physical preflight evidence — 2026-10-04

The exact target read-only preflight is now physically PASS and the accepted
JSON is retained under release/ as:

    performance-guardian-6g-cpu-preflight-8c40-2026-10-04.json

Observed target evidence:

    CPU                  13th Gen Intel Core i7-13700H
    PawnIO               2.2.0
    Physical cores       14
    Source               AC confirmed
    MSR 0x606            0x00000000000A0E03
    Power unit           0.125 W
    MSR 0x614            0x0000000000000168
    Thermal spec         45 W
    Reported minimum     0 W (no nonzero minimum advertised)
    Reported maximum     0 W (no nonzero maximum advertised)
    MSR 0x610 baseline   0x0042839800DF8168
    Baseline fields      PL1 45 W / PL2 115 W
    Lock                 clear
    PL1/PL2 enable       both enabled

Three consecutive 0x610 reads were identical. The production-shaped backend
successfully planned AC 35/60 W and Battery 8/15 W. The target journal was
absent, the production mutex was available, the software write gate was proven
closed and HardwareWritesPerformed=false.

A reported minimum of 0 W is interpreted only as "no nonzero minimum reported
by 0x614"; it is not evidence that a literal 0 W package limit is supported.
The Battery 8/15 W preset therefore still requires the bounded physical
Guardian qualification before production authority can be considered.

productionHardwareWritesAuthorized=false.
guiIntegrationAuthorized=false.
startupPersistenceAuthorized=false.
automaticProfileIntegrationAuthorized=false.


## Step 6G.2 — bounded physical CPU-only Guardian gate

The physical CPU-only qualification harness is now prepared, but it is not a
physical PASS until run on the exact target.

The hardware-writing entry point is deliberately separate from the read-only
preflight and requires an explicit operator acknowledgement:

    .\scripts\test-performance-guardian-6g-cpu-physical.ps1
      -ConfirmTargetProfile HP-8C40-9D0R1LA-F18
      -ConfirmCpuHardwareWrites

Before granting bounded write authority, the script rebuilds the Guardian and
reruns the read-only Step 6G preflight. The detached child additionally
requires the exact IntelMSR.bin SHA-256 physically observed in Step 6G.1:

    d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f

The target-scoped CPU journal remains:

    %LOCALAPPDATA%\VictusFanControl\Performance\
      HP-8C40-9D0R1LA-F18\cpu-power-session.json

The bounded sequence is AC 35/60 W -> Battery 8/15 W -> AC 35/60 W -> one
conditional restore. Initial hardware authority is qualification-constrained
to a freshly confirmed AC source. Each initial/switch write requires its
durable WriteArmed or PresetSwitchWriteArmed journal phase and exact 0x610
readback before the journal can return to Owned. DISABLE_SESSION uses the
existing durable Restoring path and conditional owned-field restore.

A clean PASS requires exactly four CPU hardware-write attempts and zero GPU
hardware writes, one enable/one release, two CPU source-transition dispatches,
zero GPU dispatches, one reconciliation query, at least two Windows source
notifications, one SessionId across AC/Battery/AC journal snapshots, strictly
increasing journal generations, immutable OriginalBaseline, final CPU state
Disabled, no source/Guardian failure, no remaining CPU journal, and a final
read-only 0x610 verification that the original owned PL1/PL2 fields were
restored.

The final raw 0x610 is not required to equal the original raw bit-for-bit:
non-owned metadata may legitimately change externally. PASS requires the
owned PL1/PL2 fields to match the immutable pre-session baseline.

This gate does not enable production operation, GUI-triggered writes, combined
CPU+GPU authority, startup persistence or automatic profiles. It is only a
bounded physical qualification of the CPU domain.

productionHardwareWritesAuthorized=false.
guiIntegrationAuthorized=false.
startupPersistenceAuthorized=false.
automaticProfileIntegrationAuthorized=false.


### Step 6G.2 first physical execution — hardware path clean, shutdown-state false negative

The first exact-target Step 6G.2 physical execution on 2026-10-04 completed
the complete CPU hardware sequence correctly, but the qualification report
finished Result=FAIL because of an independent Guardian shutdown bookkeeping
bug.

The hardware evidence itself is clean:

    initial direct source          AC
    pre-session baseline           PL1 45 W / PL2 115 W
    initial owned request          AC 35/60 W
    source transition             Battery confirmed
    battery owned request          8/15 W
    source transition             AC confirmed
    AC-return owned request        35/60 W
    CPU hardware write attempts    4
    GPU hardware write attempts    0
    CPU source dispatches          2
    GPU source dispatches          0
    source reconciliation          1
    source notifications           3
    duplicate signals              2
    enable/release calls           1 / 1
    same SessionId                 true
    increasing generations         true
    immutable OriginalBaseline     true
    final owned fields restored    true
    final snapshot                 PL1 45 W / PL2 115 W
    final CPU state                Disabled
    active CPU journal present     false
    Guardian exit code             0
    source/Guardian failure        null / null

The only failed qualification predicates were:

    GuardianReport.ExitReason == "CANCELLED" (expected "CLIENT_SHUTDOWN")
    GuardianReport.FinalPhase == "Idle"       (expected "Stopped")

The accepted SHUTDOWN response was emitted after DISABLE_SESSION while the
authority was already Idle. PerformanceGuardianHost only called MarkStopped()
when SHUTDOWN arrived while a session was still enabled. Therefore a normal
DISABLE_SESSION -> SHUTDOWN path cancelled the host with authority still Idle.

This is a protocol-state defect, not a RAPL write/restore failure. The exact
first-run evidence is retained under release/ with the suffix
step6g-cpu-physical-attempt1-false-negative.

The fix moves the Stopped transition into the semantic SHUTDOWN authority
handler so every accepted SHUTDOWN transitions to Stopped, including after a
successful DISABLE_SESSION. The host no longer has a wasEnabled-only stopping
branch. A regression fixture now requires Disable -> Shutdown to produce
SHUTDOWN_ACCEPTED with Phase=Stopped, and the physical qualification requires
the same response phase in addition to its existing final-report invariants.

Because the recorded qualification JSON itself is Result=FAIL, Step 6G.2 is
not yet declared physically PASS. One final bounded rerun on the fixed build is
required to close the gate. No stale journal remains from the first run.

productionHardwareWritesAuthorized=false.
guiIntegrationAuthorized=false.
startupPersistenceAuthorized=false.
automaticProfileIntegrationAuthorized=false.
