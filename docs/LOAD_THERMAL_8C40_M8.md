# HP 8C40 M8 - representative-load and thermal-preemption qualification

Target: `HP-8C40-9D0R1LA-F18`.

Status: **NO-WRITE PREFLIGHT PHYSICAL PASS. M8A REPRESENTATIVE-LOAD HARNESS CODE PREPARED / CI PENDING. M8B/M8C WRITE-CAPABLE HARNESS NOT YET AUTHORIZED.**

M4A/B/C, M5A-E, M6 Modern Standby and M7 hibernation are already physically closed.
M8 is the next independent authorization boundary before production watchdog promotion or any
automatic/adaptive fan policy.

`WatchdogRecoveryValidated` remains **false**. Automatic/adaptive fan policy remains **OFF**.

Preparation CI is closed at commit `a1c4e6adaa25518b36d6b1f59b9d800845b1fc82`,
GitHub Actions **#694** (run `36653787634`) **SUCCESS**. The M8 invariant passed
under PowerShell 7 and Windows PowerShell 5.1, with warnings-as-errors build,
M6/M7 regressions, SafetyGate, coordinator and HP backend self-tests green.

Two earlier preparation runs (#692 and #693) failed closed only because existing
legacy/M6 static invariants still matched the old wording of the production
watchdog prohibition after that wording was corrected for the post-M7 state.
Those invariants were updated without changing fan authority, watchdog authority,
thermal thresholds or hardware write behavior. No physical M8 execution occurred.

## 1. Objective

M8 must answer three questions on the exact HP 8C40 target without weakening any previously
qualified safety boundary:

1. Can the real telemetry/SafetyGate/coordinator/backend/watchdog chain remain causally healthy
   during representative CPU+GPU gaming load?
2. Can the real controller surrender Custom authority promptly and verifiably when the thermal
   SafetyGate becomes unsafe?
3. Are there any remaining production races that appear only while telemetry, EC ownership,
   watchdog liveness and a real fan command are active under load?

M8 does **not** design or enable the adaptive fan curve. It qualifies the safety envelope that a
future policy would depend on.

## 2. Non-negotiable invariants

The following rules remain unchanged:

- exact target only: HP 8C40 rev. 63.43, SKU 9D0R1LA#AKH / prefix 9D0R1LA, BIOS F.18;
- CPU and GPU fan commands are equal-only;
- validated command range remains 10..50;
- fan-stop / level 0 is forbidden;
- asymmetric CPU/GPU commands remain unqualified;
- no arbitrary EC writes;
- EC 0x62/0x63 remain read-only diagnostics;
- GetFanLevel is not ownership acknowledgement;
- command acknowledgement remains WMI success + exact EC 0x34/0x35 + dual-tach feedback +
  healthy guards;
- unknown external overrides are preserved and are never fought by repeated VFC writes;
- GPU temperature 0 C remains invalid/fail-closed;
- CPU safety temperature remains max(package, hottest reported core-context);
- isolated asymmetric/torn EC reads or isolated unexpected guards require bounded read-only
  confirmation;
- durable journal schema v2, TargetProfileId and exact controller process identity remain part of
  PASS evidence;
- a final FF/FF state without the complete causal chain is not a PASS;
- if an independent delayed failsafe takes control, that physical run is safety-recovered but
  cannot count as M8 PASS.

## 3. Existing thermal contract

M8 must reuse the production SafetyGate contract; it must not invent a second thermal policy.

Current handoff thresholds:

- CPU emergency: effective CPU >= 95 C;
- GPU emergency: GPU >= 87 C;
- effective CPU = max(package, hottest reported core-context).

The existing per-core characterization script aborts at 90 C and is read-only. It is useful
historical sensor evidence, but it is **not** a thermal-emergency qualification.

M8 must not deliberately heat real silicon to the production emergency thresholds merely to
exercise preemption. The physical harness will use conservative real-temperature abort limits
of **90 C CPU effective** and **82 C GPU**. Reaching either physical abort limit is an immediate
fail-closed restore/cleanup condition, not the intended trigger for M8C.

## 4. Gate decomposition

### M8A - representative-load admission

M8A begins firmware-owned and performs no VFC fan write while establishing representative load.

The operator supplies a normal gaming/3D workload. The versioned harness must observe a bounded,
sustained window with:

- complete and fresh telemetry;
- valid exact GPU identity and nonzero GPU temperature;
- material GPU load plus material CPU activity;
- AC/battery sanity;
- stable firmware ownership FF/FF;
- healthy guards;
- no retained watchdog journal;
- no power/lifecycle transition.

If the representative-load window is not proven, M8A ends **FAIL_CLOSED / NO-WRITE**.

The exact numeric load/duration criteria must be versioned in the harness before physical use and
must be reported in the final evidence. They are not to be improvised by operator commands.
The versioned M8A harness now fixes those criteria before physical use:

- **60 samples at 1 second** (approximately one minute);
- at least **45/60** samples must simultaneously satisfy the representative-load predicate;
- at least **10 consecutive** samples must satisfy it;
- GPU load >= 35% **and** GPU power >= 20 W;
- CPU load >= 5% **or** CPU package power >= 15 W;
- every sample must be complete, fresh (<=3 s), production-SafetyGate-ready, exact-GPU valid
  and below the 90 C CPU / 82 C GPU physical abort limits;
- AC must remain online and battery present at >=20%;
- narrow read-only EC evidence is checked at the start, every 5 samples and at the end;
  ownership must remain FF/FF and MaxFan/FanSwitch must remain 00/00;
- an isolated unexpected EC ownership/guard sample gets at most three read-only reads with
  25 ms spacing; two consecutive identical unexpected samples are treated as persistent and
  M8A fails closed.

The threshold pair is intentionally a **material-load** gate rather than a maximum-stress gate:
GPU utilization alone is insufficient (idle/video-like low-power activity is excluded by the
20 W requirement), while CPU activity may be demonstrated by either scheduler load or package
power. VictusFanControl generates no stress load itself; the operator supplies normal gameplay
or a normal 3D workload.

The harness is `scripts/test-8c40-load-thermal-m8a.ps1`. It first reruns the versioned M8
NO-WRITE preflight, then waits for the operator to reach active gameplay/rendering and explicitly
confirm `M8A`. The observation mode is `--8c40-m8a-representative-load` with a required
`--8c40-m8a-result-path`. Durable evidence is written under
`logs/m8a-representative-load_*/m8a-result.json`. The entire M8A path remains NO-WRITE.

### M8B - watchdog-backed 50/50 under representative load

Only after M8A has established representative load may M8B become write-capable.

Safety ordering:

1. arm the independent delayed failsafe;
2. start/verify the exact M4 LocalSystem watchdog;
3. launch the M8 qualification controller;
4. obtain real SafetyGate admission;
5. acquire Custom;
6. durable PREPARE;
7. durable WRITE_INTENT;
8. one HP WMI SetFanLevel(50,50);
9. exact EC 50/50 acknowledgement;
10. dual-tach physical acknowledgement;
11. watchdog Commit -> OWNED;
12. continuous supervision while representative load remains present.

50/50 is chosen because it is already physically qualified and maximizes cooling margin. M8 does
not extend the fan envelope.

During the bounded soak, every accepted supervision sample must preserve:

- complete/fresh plausible telemetry;
- representative-load condition;
- exact EC ownership;
- healthy dual tachometers;
- healthy or correctly confirmed guards;
- exact watchdog identity/liveness;
- correct schema-v2 OWNED journal;
- acceptable AC/battery state.

Any loss of these conditions must fail closed and restore firmware authority. VFC must not
continuously retransmit 50/50.

### M8C - physical preemption path with qualification-only thermal injection

M8C proves the real physical restore path while avoiding intentional overtemperature.

Preconditions are the same real representative load, real watchdog-backed Custom 50/50 ownership
and real hardware acknowledgement as M8B.

The controller may then use an **internal qualification-only injection point** to submit a
synthetic SafetyGate snapshot that crosses exactly one production threshold:

- CPU case: effective CPU = 95 C while GPU remains below 87 C;
- GPU case: GPU = 87 C while effective CPU remains below 95 C.

The injected frame must be explicitly marked as synthetic qualification evidence and must never
be exposed through the ordinary production runtime path.

For each case PASS requires:

1. SafetyGate reports ThermalEmergency=true and CustomControlPermitted=false;
2. FanControlCoordinator cancels/preempts any in-flight command;
3. coordinator restores HP firmware through the normal backend path;
4. local restore acknowledgement proves FF/FF;
5. watchdog Release is acknowledged;
6. durable journal is absent;
7. two consecutive independent FF/FF samples are observed;
8. delayed failsafe did not take over;
9. exact controller/watchdog identities and event ordering are retained in evidence.

CPU and GPU threshold cases are separate subcycles; one does not imply the other.

## 5. Remaining production-race audit

Before M8 is closed, code/CI and the physical harness must explicitly review the already-known race
boundaries under load rather than re-running the M5 kill matrix blindly.

At minimum retain coverage for:

- stale SafetyGate result versus a newer accepted result;
- safety preemption versus an in-flight ApplyAsync;
- lifecycle/admission fence versus command dispatch;
- watchdog liveness failure versus concurrent EC/telemetry failure;
- external ownership override before first write and after WRITE_INTENT;
- transient guard/torn-read confirmation;
- restore/journal cleanup ordering.

If implementation review finds a new unqualified physical race, it becomes an explicit M8
sub-gate. M5D/M5E are not repeated solely to reproduce historical post-check behavior.

## 6. Required stage order

M8 is developed strictly in this order:

~~~text
specification
  -> static/synthetic invariants
  -> GitHub Actions CI
  -> versioned NO-WRITE preflight
  -> versioned physical harness
  -> physical evidence
  -> safety-first cleanup proof
  -> profile/docs closure
~~~

No physical M8 execution is authorized before the no-write preflight passes on the exact target.

## 7. No-write preflight requirements

The versioned preflight is implemented as
`scripts/test-8c40-load-thermal-m8-preflight.ps1`. Its code/invariant preparation passed
GitHub Actions **#696** (run `36655313676`) at commit
`53adc295d7888b0669dcd38e5e7d3e9457965e02`, including PowerShell 7,
Windows PowerShell 5.1, warnings-as-errors build, M6/M7 regressions and the
SafetyGate/coordinator/BIOS/backend self-tests. It is now the next authorized
**NO-WRITE** physical step; the write-capable M8 harness is still not authorized.

The preflight performs **no fan write, no firmware restore, no watchdog lease,
no service start/stop mutation and no deliberate stress load**. A dedicated
`--8c40-m8-preflight-probe` path constructs only the exact-target
`HardwareTelemetryReader` and evaluates the production `SafetyGate` with
`fanWritePathPresent:false`; it never constructs a fan backend, coordinator or watchdog lease.

The read-only telemetry probe requires three consecutive `PreconditionsReady` samples,
the exact RTX 4060 Laptop GPU identity, GPU temperature > 0 C, complete 14/14 core-context
telemetry, plausible power/load/tach data, and temperatures below the M8 physical abort
limits (effective CPU < 90 C and GPU < 82 C).


It must verify at least:

- exact target fingerprint;
- current repository HEAD;
- M6/M7 already closed;
- `WatchdogRecoveryValidated=false`;
- automatic/adaptive policy OFF;
- M4 service Manual/stopped if installed;
- no retained journal;
- build with warnings as errors;
- all required M5-M8 invariants/self-tests;
- telemetry backends initialized and plausible;
- GPU temperature > 0 C;
- AC online and sane battery status;
- two consecutive independent FF/FF samples.

The preflight binds executable/source provenance to
`feature/victus-8c40-port` with local HEAD equal to its configured upstream.
Tracked modifications/deletions and untracked files outside `logs/` remain blocking.
Untracked historical evidence below `logs/` is explicitly preserved and allowed because
it cannot alter the committed executable/configuration under test; the preflight reports
those paths and never deletes them. It also checks M6/M7/profile policy boundaries,
requires AC online with at least 20% readable battery charge, requires M4 Manual/stopped
if installed, and proves two consecutive independent FF/FF setpoint reads before declaring PASS.

### Physical no-write preflight attempt 1

Attempt 1 failed closed at repository provenance before target/telemetry/EC stages because
historical M0/M6/M7/power-transition evidence existed as untracked paths under `logs/`.
No fan write, restore, watchdog lease, service mutation, stress load or control path was
entered. This exposed an overly strict provenance rule: preserved historical evidence was
being treated the same as an untracked source/configuration change. The rule was hardened
to allow only `?? logs/...` entries while continuing to fail closed for every other
working-tree change.

### Physical no-write preflight attempt 2 - PASS

Attempt 2 passed on 2026-09-30 at repository HEAD
`16ea0578d5dd4c69a39a0a0405b4522f37cef0aa`. The exact HP 8C40 target,
AC/battery baseline and M4 Manual/Stopped baseline all matched the M8 contract.
The warnings-as-errors build completed with 0 warnings and 0 errors and the full
M5-M8 static/synthetic regression set passed.

The dedicated read-only M8 telemetry path resolved the exact RTX 4060 Laptop GPU.
Three consecutive samples were complete with 14/14 physical-core context, zero
Intel/EC/NVML recoveries, effective CPU temperatures 50/49/49 C, GPU temperature
47 C, `PreconditionsReady=true`, valid GPU identity and no thermal emergency.

The independent final ownership proof observed `FF/FF` twice consecutively.
No fan write, firmware restore, watchdog lease, service mutation, fault injection,
sleep transition or deliberate stress load occurred. The physical NO-WRITE preflight
is therefore closed as PASS. M8A representative-load admission is now the next
development/qualification step; M8B/M8C remain blocked.

## 7A. M8A code preparation

M8A code preparation adds a read-only representative-load classifier and a versioned physical
harness. This preparation does not authorize M8B or M8C, does not create a watchdog lease,
does not construct a fan-control backend/coordinator and cannot issue SetFanLevel or a firmware
restore. GitHub Actions CI must pass the new PowerShell 7 / Windows PowerShell 5.1 invariant,
warnings-as-errors build and the synthetic M8A classifier self-test before physical M8A use.

## 8. Physical harness safety and evidence

The future physical harness must:

- arm the delayed independent failsafe before launching any write-capable controller;
- preserve journal and marker evidence durably;
- never require ad-hoc PowerShell commands from the operator;
- use only versioned repo scripts/modes;
- make cleanup safety-first;
- refuse PASS if the fallback took control;
- refuse PASS if causal evidence is incomplete even when final EC is FF/FF;
- restore the M4 qualification service to Manual/stopped at the end.

Physical evidence must include timestamps, controller/watchdog PID + creation ticks, journal
generation/phase, real load/temperature/power telemetry, EC setpoints, guards, dual tach feedback,
SafetyGate decision, restore evidence, Release acknowledgement and independent final FF/FF proof.

## 9. Authorization boundary after M8

M8 PASS does not automatically enable an adaptive curve.

Only after M8A/M8B/M8C and any newly discovered production-race sub-gates are explicitly closed may
the project separately decide whether the evidence is sufficient to set
`WatchdogRecoveryValidated=true` and expose watchdog-backed construction through the production
factory.

Automatic/adaptive policy remains a later gate and stays OFF throughout M8.
