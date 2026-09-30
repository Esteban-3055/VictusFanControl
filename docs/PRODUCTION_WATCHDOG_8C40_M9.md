# HP 8C40 M9 - production watchdog integration and promotion

Target: `HP-8C40-9D0R1LA-F18`.

Status: **M9A CODE/CI PASS. M9B READ-ONLY PREFLIGHT CODE/CI PASS AND READ-ONLY EXECUTION AUTHORIZED. PRODUCTION WATCHDOG CONSTRUCTION AND ALL WRITE-CAPABLE M9 PHYSICAL EXECUTION REMAIN BLOCKED.**

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
Its execution gate is currently closed in the profile until the preparation commit
passes the complete same-HEAD CI workflow.

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

The hardware-side operations are read-only:

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

The profile now sets only:

- `m9b.readOnlyExecutionAuthorized=true`;
- `m9b.physicalWriteAuthorized=false`;
- `m9a.productionConstructionAuthorized=false`;
- `WatchdogRecoveryValidated=false`.

Therefore the versioned M9B preflight may be executed on the exact target, but it
cannot start the watchdog, acquire a lease or write a fan level. M9C remains blocked
until the resulting M9B evidence is reviewed and committed.


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

M9C is now compiled in advance so M9B remains the next machine-side dependency.
It is **not physically authorized**: the controller gate, the temporary
construction gate and the profile write gate are all false.

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

M9C cannot be opened until M9B has physically passed and its evidence is
reviewed/committed. M9D remains a later full GUI/lifecycle last-mile regression.


### M9C deterministic invariant coverage

The M9C controller/construction boundary is now pinned by
`scripts/test-8c40-m9c-production-smoke-invariants.ps1` under both PowerShell 7
and Windows PowerShell 5.1. The invariant verifies the pre-hardware authorization
barrier, both closed compile-time gates, exact factory/public-backend construction,
scope teardown before Custom admission, the one-write 30/30 contract, conservative
thermal/power bounds and continued production/adaptive blocks.


## 8. M9C parent harness, recovery and evidence stack

The complete future M9C physical stack is now versioned but remains hard-blocked.
The parent harness is `scripts/test-8c40-production-watchdog-m9c.ps1`. Its very
first gate reads only the versioned profile and refuses unless M9B physical PASS,
a dedicated M9C physical authorization, controller execution authorization and
temporary construction authorization are all true. At the current stage all M9C
write-capable authorizations remain false.

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

M9C is therefore **CODE/CI PASS**, not physical PASS. All three write/promotion
barriers remain closed:

- `m9c.physicalAuthorization.authorized=false`;
- `m9c.physicalExecutionAuthorized=false`;
- `m9c.qualificationConstructionAuthorized=false`.

M9B remains the first unavoidable machine-side evidence boundary.


## 9. M9D full-GUI production-path lifecycle preparation

M9D is the final planned last-mile integration regression before watchdog promotion.
Its code is being prepared while M9B remains the first unavoidable target-side evidence
boundary and M9C remains physically blocked.

The M9D App mode is
`--8c40-m9d-production-lifecycle-test` with exact token
`8C40-M9D-PRODUCTION-LIFECYCLE30`. Two independent compile-time barriers remain
false: `Hp8C40M9DProductionLifecycleQualification.PhysicalExecutionAuthorized`
and `Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationConstructionAuthorized`.

Unlike historical M6/M7, M9D does not call
`CreateLifecycleQualificationBackend`. It enters a short-lived, exact-target,
token-bound construction scope and then uses the normal production surfaces:

```text
Hp8C40ProductionWatchdogGate.CreateLeaseIfAuthorized
 -> HpFanControlBackendFactory.Create
 -> public Hp8C40FanControlBackend constructor
 -> scope disposed
 -> FanControlCoordinator
```

The scope must be closed before coordinator admission, PREPARE, lease acquisition or
any fan write. Normal GUI construction outside that scope remains blocked by
`WatchdogRecoveryValidated=false` and `ProductionConstructionAuthorized=false`.

M9D reuses the already-qualified M6 display-aware lifecycle engine rather than creating
a second implementation of the safety-critical ordering. Evidence markers use a
separate `m9d-production-lifecycle.*` namespace and record transition mode
`m9d-production-modern-standby`.

The future physical gate is planned as two fresh GUI subcycles:

1. **D1 controller death:** reach durable OWNED 30/30 through the full GUI production
   construction path, bind GUI PID + creation ticks, then force-kill only that exact
   GUI process. The still-running watchdog must restore firmware, clear the journal and
   retain its own PID/creation identity.
2. **D2 Modern Standby lifecycle:** launch a fresh M9D GUI, reach OWNED 30/30, then
   perform a user-initiated Modern Standby cycle. SESSION_DISPLAY_STATUS Off must
   proactively close admission and restore/release before suspend; display On plus five
   fresh Healthy samples are required before one controlled 30/30 re-entry and final
   restore.

M9D does not repeat hibernation because M7 already physically closed that distinct
power-transition boundary. It focuses on whether the newly wired full GUI production
construction path preserves the already-qualified controller-death and display-aware
Modern Standby semantics.

No M9D physical execution is authorized by this preparation.


### M9D parent harness and evidence stack

The complete future M9D parent stack is now versioned but remains hard-blocked.
`scripts/test-8c40-production-watchdog-m9d.ps1` refuses before Administrator,
CIM, evidence creation, service mutation or EC access unless all of the following are
formally recorded in the profile:

- M9B read-only physical PASS;
- M9C physical PASS;
- a dedicated M9D physical authorization;
- M9D controller execution authorization;
- M9D temporary construction authorization.

The harness never installs/replaces the watchdog service and never deletes a retained
journal or lifecycle marker to satisfy a gate.

M9D is one authorized harness invocation containing two fresh GUI subcycles. The M4
LocalSystem watchdog starts once and its PID + creation ticks must remain stable.

**D1** launches the full `VictusFanControl.App` M9D mode, proves schema-v2
generation-3 OWNED 30/30 bound to the exact GUI PID + creation ticks, preserves the
READY marker, and force-kills only that exact GUI process. The parent issues no HP
restore. PASS requires watchdog owner-loss `RestoredFirmware`, journal disappearance,
stable independent FF/FF and the original watchdog PID/creation identity. The delayed
independent failsafe must not take over.

**D2** launches a fresh full-GUI M9D instance from a fresh FF/FF/journal-absent
baseline. After READY the operator must explicitly type `M9D-SLEEP` and then use
Windows **Start -> Power -> Sleep**. The harness contains no `shutdown.exe`,
`SetSuspendState` or equivalent transition command. PASS requires the existing
display-aware engine to produce the M9D-specific pre-sleep, resume-gate, re-entry and
result markers plus causal Kernel-Power 506 -> 507 Modern Standby evidence. Critical
battery and hibernation evidence invalidate the run.

The D2 watchdog log must contain exactly two complete normal transactions for the same
GUI identity: initial 30/30 and the single controlled post-resume 30/30 re-entry. Final
closure requires journal absent, stable two-consecutive FF/FF, delayed failsafe unused
and M4 returned to Manual/Stopped/PID0/LocalSystem.

`watchdog-m9d-service-failsafe-8c40.ps1` is an isolated copy of the already-pinned
bounded 30/30 M9C delayed recovery design. `package-m9d-evidence.ps1` packages PASS
and FAIL_CLOSED evidence with source/installed-binary hashes, full watchdog log/status,
service snapshot, ZIP and SHA-256 sidecar. Packaging never deletes source evidence.
