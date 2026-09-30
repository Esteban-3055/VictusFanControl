# HP 8C40 M9 - production watchdog integration and promotion

Target: `HP-8C40-9D0R1LA-F18`.

Status: **M9A CODE PREPARED / CI PENDING. PRODUCTION WATCHDOG CONSTRUCTION BLOCKED. NO M9 PHYSICAL EXECUTION AUTHORIZED.**

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

Until that is recorded in the profile, M9A is only prepared, not closed.
