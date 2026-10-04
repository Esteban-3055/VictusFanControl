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

