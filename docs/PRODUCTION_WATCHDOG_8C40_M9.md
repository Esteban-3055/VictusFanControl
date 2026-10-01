# HP 8C40 M9 - production watchdog integration and promotion

Target: `HP-8C40-9D0R1LA-F18`.

Status: **M9A CODE/CI PASS. M9B PHYSICAL READ-ONLY PASS / FORMALLY CLOSED. M9C PHYSICAL AUTHORIZED SUBJECT TO SAME-HEAD CI. M9D REMAINS BLOCKED. M9E PROMOTION-READINESS AUDITOR CODE/CI PASS. PRODUCTION WATCHDOG PROMOTION REMAINS BLOCKED.**

M8 is physically closed, including M8B representative-load ownership and M8C thermal
preemption/restore. That evidence is necessary but does not itself make the watchdog a
normal production dependency. M9 is the explicit last-mile boundary between the
qualification-only M4-M8 paths and ordinary GUI/factory/backend construction.

`WatchdogRecoveryValidated=false`, `control.enabledByDefault=false` and
`automaticPolicyEnabled=false` remain mandatory throughout M9 preparation.

## 1. M9 objective

M9 must prove that the exact route used by the normal application can consume the
already-qualified target-bound watchdog lease without creating a new authority path,
without weakening fail-closed behavior and without relying on qualification-only
constructors.

The intended production route is:

```text
MainForm normal startup
  -> Hp8C40ProductionWatchdogGate
  -> NamedPipeFanControlWatchdogLeaseClient
     target HP-8C40-9D0R1LA-F18
     pipe VictusFanControl.Watchdog.M4.8C40.v2
  -> HpFanControlBackendFactory
  -> Hp8C40FanControlBackend public production constructor
  -> FanControlCoordinator
```

M9 deliberately reuses the exact M4 target-bound protocol/service/pipe that already
has physical recovery evidence. M9A does not rename, reinstall, start, stop or promote
that service. The existing `VictusFanControlWatchdogM4` installation remains a
Manual/Stopped qualification service until later M9 evidence justifies a production
service-lifecycle decision.

## 2. Non-negotiable invariants

M9 does not change:

- exact HP 8C40 rev. 63.43 / SKU 9D0R1LA / BIOS F.18 target;
- equal-only CPU/GPU fan commands;
- validated fan range 10..50;
- no level 0 / no fan-stop;
- no arbitrary EC writes;
- raw/effective SafetyGate thermal authority;
- ownership, watchdog journal or lifecycle fail-closed semantics;
- no repeated WMI commands to fight firmware;
- automatic/adaptive policy remains OFF;
- default control remains OFF.

A production watchdog lease is not created merely because M8 passed.

## 3. M9A - code/CI production wiring preparation

M9A is hardware-neutral and must perform no fan write, no watchdog lease acquisition,
no service mutation and no EC write.

The new `Hp8C40ProductionWatchdogGate` has two independent requirements:

1. the exact target must match and
   `Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated` must be true;
2. `ProductionConstructionAuthorized` must be true.

Both are false for production promotion at M9A closure.

The normal GUI path calls `CreateLeaseIfAuthorized`. While the gate is closed it
returns `null`, so runtime behavior is identical to the pre-M9 state.

Defense in depth is also enforced in:

- `HpFanControlBackendFactory`: any externally supplied HP 8C40 watchdog lease is
  rejected before production backend construction;
- the public `Hp8C40FanControlBackend` constructor: direct watchdog-backed
  construction is independently rejected by the same gate.

The internal synthetic/qualification constructor remains unchanged so deterministic
M5-M8 regressions can continue to exercise lease behavior without opening production.

## 4. M9 sub-gates after M9A

### M9B - read-only production preflight

Prepare and execute a versioned no-write preflight on the exact target. It should prove
repository provenance, M8 closure, M4 service identity/configuration, absent retained
journal, stable firmware ownership and all production-wiring/self-test invariants.
It must not start the service or acquire a lease merely to pass the preflight.

### M9C - bounded normal-production-path smoke

Only after M9B PASS and a separate explicit authorization may a write-capable harness
exercise the normal GUI/factory/public-backend construction route. It must use one
bounded equal-only command, preserve exact M4 causal evidence, require no failsafe
takeover and finish with stable firmware ownership, journal absence and the service
baseline.

### M9D - last-mile recovery/lifecycle regression

A final physical gate should confirm that the newly wired production route preserves
the already-qualified controller-death recovery and display-aware Modern Standby
release/recovery behavior. This is a focused integration regression, not a reason to
repeat the complete historical M5/M6/M7 matrix.

Only after these gates and same-HEAD CI may a separate promotion commit consider
`WatchdogRecoveryValidated=true` and
`ProductionConstructionAuthorized=true`.

That promotion still must leave:

- `automaticPolicyEnabled=false`;
- `control.enabledByDefault=false`.

Adaptive control remains a later independent gate.

## 5. M9A evidence requirements

M9A PASS requires:

- complete GitHub Actions workflow on the exact M9A HEAD;
- PowerShell 7 and Windows PowerShell 5.1 M9 invariant PASS;
- warnings-as-errors solution build;
- HP backend self-test including deterministic M9 factory/gate refusal;
- existing M5-M8/watchdog/SafetyGate/coordinator regressions green;
- zero physical execution.

M9A is **CODE/CI PASS** at commit `f426df1480d92d87b9c5c5b55eb927a5a83dbf93`,
GitHub Actions **#836** (run `36778419056`). The complete workflow passed the
M9 invariant under PowerShell 7 and Windows PowerShell 5.1, warnings-as-errors
build, HP backend tests including deterministic factory/gate refusal, and all
existing M5-M8/watchdog/SafetyGate/coordinator regressions. No hardware path ran.

The immediately preceding preparation run #835 failed only because the historical
legacy-88F8 isolation invariant still required the old literal pre-M9 watchdog
prohibition inside the factory/backend. That invariant was migrated to assert the
new centralized M9 gate and fail-closed ordering; no runtime authorization changed.

M9A is therefore closed. The next step is M9B read-only preflight preparation.


## 6. M9B read-only preflight preparation

The versioned preflight is `scripts/test-8c40-production-watchdog-m9b-preflight.ps1`.
The canonical pre-hardware rebind and the first read-only authorization passed same-HEAD
CI. The first target-side M9B attempt then failed closed at telemetry initialization because
the isolated worktree did not contain the gitignored runtime PawnIO modules. M9B execution
is temporarily re-blocked while a deterministic runtime-dependency bootstrap is qualified.

The script is intentionally stricter than the earlier M8 no-write preflight because
M9 is qualifying the last-mile production watchdog dependency. M9B requires the already
qualified `VictusFanControlWatchdogM4` service to be installed, but it must be:

- `Manual`;
- `Stopped`;
- PID 0;
- LocalSystem;
- still configured to the versioned M4 exact-target lease-service binary/mode;
- free of any retained `lease.json`.

M9B does **not** start or stop the service, reinstall it, acquire a named-pipe lease,
call `SetFanLevel`, restore firmware, dispatch a power transition or enable the M9
production construction gate.

Before hardware reads, the preflight may provision missing local runtime dependencies by invoking the existing pinned `scripts/setup-pawnio-modules.ps1`. That helper downloads PawnIO.Modules 0.2.11, verifies archive SHA-256 `43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4`, and copies only `IntelMSR.bin` and `LpcACPIEC.bin` into the gitignored local `modules/` directory. It does not alter the watchdog service or fan hardware.

The hardware-side operations remain read-only:

- exact SMBIOS/CIM target fingerprint;
- AC/battery baseline;
- existing M8 read-only telemetry/SafetyGate probe;
- independent narrow EC setpoint reads requiring two consecutive FF/FF samples.

The preflight also runs the relevant M5-M9 invariants and deterministic self-tests,
then proves the service state, repository HEAD and durable journal state did not change.

Evidence is written only below a new timestamped `logs/m9b-production-watchdog-preflight_*`
directory. Historical evidence is never deleted.


### M9B code/CI closure and authorization

M9B preparation is **CODE/CI PASS** at commit
`2ec00f047943c46885a15ab96642ec7c69a1e6dd`, GitHub Actions **#838**
(run `36779270309`). The complete workflow passed the new M9B invariant under
PowerShell 7 and Windows PowerShell 5.1, warnings-as-errors build, the M9
factory/gate self-test and all existing M5-M8 regressions. No hardware path ran.

During runtime-bootstrap preparation the profile deliberately returns to:

- `m9b.readOnlyExecutionAuthorized=false`;
- `m9b.physicalWriteAuthorized=false`;
- `m9a.productionConstructionAuthorized=false`;
- `WatchdogRecoveryValidated=false`.

The runtime-bootstrap preparation closed with **CODE/CI PASS** at commit `3d61b8721a9c67a509e4cff6459940378dacd553`, GitHub Actions **#921** (run `36813276213`). The read-only authorization was then carried by canonical HEAD `63409c4d734bcc2c39ab968adb3470f18676463d`, with same-HEAD canonical GitHub Actions **#924** (run `36813663295`) SUCCESS.

The target-side M9B run at 2026-10-01T04:28:29Z is **PHYSICAL READ-ONLY PASS**. Evidence archive `m9b-production-watchdog-preflight_2026-10-01_012803.zip` has SHA-256 `d34ec42b4f22f47e383900b4f22e8dd37e26e4752cfd211a0f607e5e080698f1`; its result is PASS on the exact HP-8C40-9D0R1LA-F18 target and exact canonical HEAD. PawnIO Intel MSR, PawnIO ACPI EC and NVIDIA NVML all initialized; three complete/fresh telemetry samples passed with zero recoveries; the independent firmware proof recorded two consecutive 255/255 (FF/FF) samples. Service state was Manual/Stopped/PID0/LocalSystem before and after, no journal remained, and fanWriteAttempted, firmwareRestoreAttempted, watchdogLeaseAttempted and serviceMutationAttempted were all false.

M9B execution is now re-blocked and `noWritePreflightPassed=true` is formally recorded. M9C remains blocked until a separate authorization commit and its own complete same-HEAD CI.


### M9B automatic evidence packaging

The preflight now packages its evidence automatically on both PASS and FAIL_CLOSED.
`scripts/package-m9b-evidence.ps1` records repository provenance/status, hashes the
result, telemetry transcript, profile, M9 documentation, preflight script and the
installed M4 service executable/module when present, then creates an adjacent ZIP and
SHA-256 sidecar. Packaging is gate-critical: a run that cannot preserve/package its
evidence must not be treated as PASS.

The packaging helper is host/file-only. It contains no EC/WMI/fan/service mutation,
does not delete historical evidence and has its own deterministic CI self-test.


## 7. M9C production-path smoke - advance code preparation

M9B is now physically closed PASS, so M9C is the next machine-side dependency.
A separate authorization opens only the M9C controller gate, the temporary
construction gate and the profile M9C execution gate. Normal production construction,
WatchdogRecoveryValidated, M9D, default control and the automatic/adaptive policy remain closed.

M9C deliberately does not instantiate `Hp8C40FanHardware` or
`Hp8C40FanControlBackend` directly. A short-lived exact-target/token
construction scope is entered only around the existing normal route:

```text
HpFanControlBackendFactory.Create(...)
  -> public Hp8C40FanControlBackend(...)
```

The AsyncLocal construction scope is disposed and verified closed before
`TryEnterCustomAsync`, PREPARE or any WMI write. Normal GUI production
construction remains blocked by `WatchdogRecoveryValidated=false` and
`ProductionConstructionAuthorized=false`.

The future physical transaction is intentionally smaller than M8B: three
consecutive fresh complete SafetyGate-permitted bounded-load frames, exactly
one 30/30 ApplyAsync, parent journal/PID/failsafe proof, five supervision
frames without command retransmission, then normal RESTORE_BEGIN -> FF/FF ->
RELEASE. Qualification-only abort limits are CPU >=90 C and GPU >=82 C; the
controller also refuses to hold 30/30 above 60 W CPU package or 75 W GPU power.
These limits do not modify production SafetyGate thresholds.

M9C could not be opened until M9B had physically passed and its evidence was reviewed/committed. That prerequisite is now satisfied by the formal M9B closure at `c65e3970bbb04cdd75186354947a6ad49f89e89d`, canonical GitHub Actions **#931** (run `36816264269`) SUCCESS. The M9C authorization still requires complete CI on its exact authorization SHA and then the same SHA as the canonical branch HEAD before one harness execution. M9D remains a later full GUI/lifecycle last-mile regression.


### M9C deterministic invariant coverage

The M9C controller/construction boundary is now pinned by
`scripts/test-8c40-m9c-production-smoke-invariants.ps1` under both PowerShell 7
and Windows PowerShell 5.1. The invariant verifies the pre-hardware authorization
barrier, both closed compile-time gates, exact factory/public-backend construction,
scope teardown before Custom admission, the one-write 30/30 contract, conservative
thermal/power bounds and continued production/adaptive blocks.


## 8. M9C parent harness, recovery and evidence stack

The complete M9C physical stack is versioned. The parent harness is `scripts/test-8c40-production-watchdog-m9c.ps1`. Its very first gate reads only the versioned profile and refuses unless M9B physical PASS, a dedicated M9C physical authorization, controller execution authorization and temporary construction authorization are all true. Those M9C-only gates are now authorized for one bounded run, but execution remains contingent on complete CI and exact canonical branch/HEAD equality.

The parent does not install or replace the watchdog service. It requires the already
qualified `VictusFanControlWatchdogM4` definition to remain Manual/Stopped/PID 0,
LocalSystem and to retain the M4 target-bound command line. Before the one 30/30
transaction it requires repository/upstream equality, exact target, AC/battery
sanity, absent journal and a stable read-only firmware baseline.

Before launching the write-capable controller the harness starts the exact M4
service, binds watchdog PID + creation ticks and arms a separate 120-second
`watchdog-m9c-service-failsafe-8c40.ps1`. The controller is launched by the
native `System.Diagnostics.Process` helper rather than `Start-Process -PassThru`,
preserving reliable PID/ExitCode evidence.

The parent writes `M9C-CONTINUE` only after READY proves one 30/30 ApplyAsync,
the temporary construction scope is already closed, the durable schema-v2
generation-3 OWNED journal is bound to the exact controller PID + creation ticks,
the watchdog PID + creation ticks are unchanged and the independent failsafe has
not taken over.

PASS requires exactly one causal M4 sequence:

```text
PREPARE
 -> WRITE_INTENT 30/30
 -> COMMIT 30/30
 -> RESTORE_BEGIN
 -> RELEASE
```

Final and cleanup firmware proof each sample up to six independent setpoint reads
and require two consecutive FF/FF. Every sample is persisted. Journal absence and
M4 Manual/Stopped/PID0/LocalSystem are required. If retained ownership exists,
the harness preserves a copy of the journal and allows the qualified watchdog/
failsafe recovery path to clear it; it never deletes the journal to satisfy a gate.

Evidence is automatically packaged on PASS and FAIL_CLOSED by
`package-m9c-evidence.ps1`, with source/installed-binary hashes, service/log
snapshots, manifest, ZIP and ZIP SHA-256. Packaging failure invalidates PASS.


### M9C full code/CI closure

The complete hard-blocked M9C stack passed GitHub Actions **#844**
(run `36783609945`) at commit
`5d5d2d93f7d5aee0d79ee37bf1597cec197882c2`.

This run covered the controller/factory construction invariant, parent harness,
independent 30/30 delayed failsafe, native tracked-child PID/ExitCode helper and
automatic evidence packaging under PowerShell 7 and Windows PowerShell 5.1,
plus warnings-as-errors build and the existing M5-M8/SafetyGate/coordinator/
watchdog/backend regressions. No physical hardware path was executed.

M9C remains **CODE/CI PASS**, not physical PASS. After formal M9B closure, the three M9C-only qualification gates are intentionally opened for one bounded execution:

- `m9c.physicalAuthorization.authorized=true`;
- `m9c.physicalExecutionAuthorized=true`;
- `m9c.qualificationConstructionAuthorized=true`.

This does **not** promote the normal production watchdog path: `WatchdogRecoveryValidated=false`, `ProductionConstructionAuthorized=false`, `control.enabledByDefault=false` and `automaticPolicyEnabled=false` remain unchanged. The authorization is valid only after complete same-HEAD CI on the exact canonical authorization SHA.


## 8. M9D production-path Modern Standby lifecycle preparation

Development continues beyond the already CI-green, write-blocked M9C harness so that
M9B remains the first unavoidable machine-side command.

M9D is a focused last-mile lifecycle regression. Unlike historical M6, its GUI backend
is constructed through the normal production route:

```text
MainForm M9D mode
 -> temporary M9D construction-only scope
 -> HpFanControlBackendFactory.Create
 -> public Hp8C40FanControlBackend
 -> scope disposed and proven closed
 -> FanControlCoordinator
 -> existing display-aware Modern Standby lifecycle engine
```

The temporary scope exists only to qualify the still-unpromoted production constructor.
It is forbidden to remain active at Custom admission and does not set
`WatchdogRecoveryValidated=true` or `ProductionConstructionAuthorized=true`.

The M9D app mode is `--8c40-m9d-production-lifecycle-test` with token
`8C40-M9D-PRODUCTION-LIFECYCLE30`. Both the compile-time execution gate and the
construction gate remain false during preparation.

M9D reuses the physically qualified M6 display-aware lifecycle engine rather than
forking a new power-state implementation. Evidence uses a separate namespace:

- `m9d-production-lifecycle.ready`
- `m9d-production-lifecycle.presleep`
- `m9d-production-lifecycle.resume-gate`
- `m9d-production-lifecycle.reentry`
- `m9d-production-lifecycle.result`

Physical authorization is forbidden until M9B read-only physical PASS and M9C
production-path smoke PASS are both formally recorded. Automatic/adaptive policy and
default control remain OFF.


### M9D parent harness preparation

The versioned parent harness is
`scripts/test-8c40-production-watchdog-m9d.ps1`. It is hard-blocked before
Administrator checks, evidence creation, service start, PawnIO/EC access, GUI launch or
fan writes unless all of the following are formally recorded:

- M9B no-write physical PASS;
- M9C physical production-path smoke PASS;
- explicit M9D physical authorization;
- M9D controller execution authorization;
- M9D temporary construction authorization.

The harness does not reinstall or replace the already-qualified M4 service. It requires
the installed service to begin `Manual / Stopped / LocalSystem / PID 0`, starts it only
inside the authorized physical boundary, binds PID + creation ticks, and returns it to
the same Manual/Stopped baseline after independent FF/FF and journal-absence proof.

A delayed independent M9D failsafe is armed before GUI launch. Any takeover invalidates
normal-path PASS. Evidence uses a unique timestamped directory and the GUI receives that
directory through `--8c40-m9d-marker-root`; existing lifecycle markers are never
overwritten. PASS/FAIL evidence is packaged automatically with a manifest, per-file
SHA-256 hashes, ZIP and ZIP SHA-256 sidecar.

The real power transition is intentionally not dispatched by the harness. After READY,
the operator must explicitly choose Windows **Start -> Power -> Sleep**, preserving the
same manual transition model used by the physically qualified M6 gate.


### M9D code/CI closure

The complete M9D GUI route, parent harness, independent failsafe, native tracked-child
helper and evidence packager are **CODE/CI PASS** at commit
`91332ac590a456c0489406e9262d25b85a6528ca`, GitHub Actions **#894**
(run `36803864224`).

The workflow passed the M9D preparation and parent-harness invariants under both
PowerShell 7 and Windows PowerShell 5.1, native child and packaging self-tests,
warnings-as-errors build, and the existing M5-M9/SafetyGate/coordinator/backend
regressions. No physical M9D execution occurred.

This closure does not authorize M9D. Its controller/construction/physical authorization
flags remain false and M9D still requires formally recorded M9B and M9C physical PASS.


## 9. GUI-side production watchdog service bootstrap

A final software-only gap was identified after the first M9D closure: the future normal
GUI could construct the target-bound named-pipe client only if the M4 service was
already running. Leaving service startup to an external harness would not qualify the
true production path.

The application now contains `Hp8C40WatchdogServiceBootstrap`. It is deliberately
narrow:

- it runs only after the exact production gate, or the separately blocked M9D physical
  gate, has authorized the route;
- it requires the existing `VictusFanControlWatchdogM4` service;
- it requires startup type `Manual`;
- it may start that existing service, but never installs, deletes or reconfigures it;
- it waits for the exact M4 Ready state and validates Session 0, LocalSystem,
  target profile, live PID + creation time and journal absence;
- it has no fan/EC/WMI write method.

While M9 production promotion is closed, the ordinary GUI never invokes this bootstrap,
so current production behavior remains unchanged.

The M9D harness was correspondingly hardened: it now requires M4 to begin
Manual/Stopped and deliberately does **not** start it on the normal path. The M9D GUI
must bootstrap M4 itself before constructing the target-bound lease. READY evidence
binds `serviceBootstrap=gui-ensure-ready`, the bootstrap PID/start ticks and the later
watchdog OWNED lease to the same process identity. Failure-recovery code may still start
the qualified service when a retained journal exists; that path cannot satisfy normal
M9D PASS.


### GUI service-bootstrap code/CI closure

The GUI-side watchdog service bootstrap and the hardened M9D parent route are
**CODE/CI PASS** at commit `f322e195523b8001b11449f8d18cb7f2facf5d74`, GitHub Actions **#901**
(run `36804784469`).

Run #900 had already passed the new PowerShell invariants but the warnings-as-errors
C# build found one compile-only ambiguity between `System.TimeoutException` and the
ServiceController package's timeout type. The exception was explicitly qualified as
`System.TimeoutException`; #901 then passed the complete workflow. No physical
execution occurred.

The production gate, M9C/M9D physical gates, `WatchdogRecoveryValidated`,
automatic/adaptive policy and default control all remain closed.


## 10. M9E promotion-readiness auditor

M9E is not a physical test and does not promote anything. Its purpose is to make the
future production promotion transaction explicit and mechanically audited before any
boolean is changed.

Production watchdog promotion is allowed only after formal physical closure of:

1. M9B read-only exact-target preflight;
2. M9C bounded normal factory/public-backend 30/30 smoke;
3. M9D GUI-side service-bootstrap + production-path Modern Standby lifecycle regression.

The eventual promotion must be a single reviewable transaction that changes both
independent runtime gates together:

```text
Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated
    false -> true

Hp8C40ProductionWatchdogGate.ProductionConstructionAuthorized
    false -> true
```

and records the same state in `profiles/HP-8C40.json`.

A half-promotion is explicitly invalid. Setting only one runtime gate must keep
production construction blocked rather than creating an alternate route.

Even after M9 production promotion, the following must remain unchanged:

- `control.enabledByDefault=false`;
- `automaticPolicyEnabled=false`;
- equal-only 10..50;
- no level 0/fan-stop;
- no asymmetric CPU/GPU commands;
- no arbitrary EC writes;
- SafetyGate thermal authority unchanged;
- M8C remains physically re-blocked;
- qualification-only M9C/M9D gates are reclosed after their evidence is captured.

The M9E auditor intentionally contains no mutator that flips these values. Promotion
must remain an explicit evidence-driven repository change, followed by complete
same-HEAD CI.


### M9E promotion-readiness auditor code/CI closure

The promotion-readiness auditor is **CODE/CI PASS** at commit
`ac9041ffef21adc96f8f2190455d3468cf13204a`, GitHub Actions **#908**
(run `36806074535`). The complete workflow passed the M9E readiness invariant
under both PowerShell 7 and Windows PowerShell 5.1, warnings-as-errors build,
all historical M5-M9 invariants, SafetyGate, coordinator and HP backend self-tests.
No physical execution occurred.

The prior #905 failure was not a runtime/control failure. Windows PowerShell 5.1
serialized the documented `false -> true` strings differently when the invariant
searched a `ConvertTo-Json` blob. A first correction removed serializer dependence;
#907 then exposed that prerequisite checks had historically used prefix/substring
matching rather than exact array equality. The final invariant now checks prerequisite
array entries by ordinal substring and the atomic promotion/invariant arrays by exact
ordinal value. This preserves the intended contract without depending on JSON escaping.

This closure changes **no** production or hardware authorization. In particular:

- `WatchdogRecoveryValidated=false`;
- `ProductionConstructionAuthorized=false`;
- M9C and M9D physical execution/construction gates remain false;
- M8C remains physically re-blocked;
- `control.enabledByDefault=false`;
- `automaticPolicyEnabled=false`.

M9 software-only promotion preparation has therefore reached its evidence boundary.
The next trustworthy step is target-side M9B read-only evidence on a single canonical
pre-hardware branch/HEAD; M9C/M9D and the final production promotion must remain blocked
until that evidence chain is formally closed.


## 11. Canonical pre-hardware branch rebind

After the software-only M9 readiness work, the repository contained several historical
parallel M9 branches. Physical evidence must not be split across those branches. The
single canonical line for the next target-side evidence is therefore prepared as:

`feature/victus-8c40-m9-canonical-prehardware`

This preparation rebinds M9B, M9C and M9D repository-provenance checks to that same
branch while keeping every active boundary closed. Commit
`e896d0d52f22079608302f81f5b0177e5d47d093` completed GitHub Actions **#912**
(run `36806751677`) with **SUCCESS**, closing the canonical rebind as software-only
CODE/CI PASS. No physical hardware path ran.

The canonical line was then advanced to
`4dcaff63bc79b8cd8866c5af22d87e44d4c68b78`, and GitHub Actions **#915**
(run `36811353546`) completed **SUCCESS** on that exact SHA and canonical branch.
That closes the prerequisite for a separate M9B read-only authorization.

The authorization sets `m9b.readOnlyExecutionAuthorized=true` only. It does not authorize
fan writes, service mutation, watchdog lease acquisition or a completed M9B preflight.
Execution remains contingent on complete CI for the exact authorization SHA and that SHA
being the canonical branch HEAD. M9C/M9D physical execution/construction,
`WatchdogRecoveryValidated`, production construction, default control and the
automatic/adaptive policy all remain false.

Status: **M9B PHYSICAL READ-ONLY PASS / FORMALLY CLOSED**.
