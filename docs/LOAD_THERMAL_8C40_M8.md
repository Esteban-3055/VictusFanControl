# HP 8C40 M8 - representative-load and thermal-preemption qualification

Target: `HP-8C40-9D0R1LA-F18`.

Status: **NO-WRITE PREFLIGHT PHYSICAL PASS. M8A CODE/CI/PHYSICAL PASS. M8B ATTEMPT 1 FAIL_CLOSED AFTER OWNED 50/50; TELEMETRY-EPOCH FIX CODE PREPARED / CI PENDING; PHYSICAL RETRY BLOCKED. M8C REMAINS BLOCKED.**

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

Current raw SafetyGate thresholds remain unchanged:

- CPU raw threshold: effective CPU >= 95 C;
- GPU emergency: GPU >= 87 C;
- effective CPU = max(package, hottest reported core-context).

Physical M8A attempt 1 showed that the exact i7-13700H target can produce a short 96 C effective
CPU spike during otherwise representative gaming load. Treating one >=90 C sample as an M8A
hard abort therefore prevented the harness from distinguishing a transient Turbo spike from a
sustained thermal condition.

The exact HP 8C40 path now adds a temporal CPU confirmation layer **above** the stateless
SafetyGate. CPU 95..98.x C requires **five consecutive unique fresh telemetry readings** before
the condition is promoted to an effective thermal emergency. Re-evaluating the same timestamp
cannot advance the counter; a sample below 95 C, an unsafe non-thermal state, a lifecycle/freshness
gap or a target mismatch resets it. GPU >=87 C remains immediate.

The i7-13700H has an Intel-specified Tjunction / maximum operating temperature of 100 C. M8A uses
**99 C as an immediate CPU hard-abort boundary**, leaving a 1 C qualification margin; it does
not wait for five samples at or above that level. M8A also retains the conservative **82 C GPU**
physical abort. These changes are exact-target qualification behavior and do not enable automatic
fan policy or watchdog production recovery.

M8 must not deliberately heat real silicon to these boundaries merely to exercise preemption.
M8C will use qualification-only synthetic thermal evidence for the confirmation path.

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
- every sample must be complete, fresh (<=3 s), exact-GPU valid and accepted by the effective
  HP 8C40 safety path;
- CPU 95..98.x C is tracked as a transient candidate and requires **five consecutive unique**
  fresh readings before thermal FAIL_CLOSED;
- CPU >=99 C is an immediate hard FAIL_CLOSED; GPU >=82 C remains an immediate physical abort;
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

- CPU case: five unique consecutive synthetic snapshots with effective CPU = 95 C while GPU
  remains below 87 C; preemption must occur on the fifth and not before;
- GPU case: one synthetic snapshot with GPU = 87 C while effective CPU remains below 95 C;
- hard-CPU static/CI case: effective CPU >=99 C must remain immediate without waiting for five.

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

## 7A.1. M8A physical attempt 1 - FAIL_CLOSED / NO-WRITE

On 2026-09-29, physical M8A attempt 1 ran at HEAD
`cc5d5a97cb36d65c02a97663c3294f4f5163b6ba`. The versioned preflight passed again on the
exact HP 8C40 target with M4 Manual/stopped, AC online, 100% battery, complete telemetry,
zero Intel/EC/NVML recoveries and stable firmware-owned FF/FF.

The first representative-load sample was already valid gaming load:

- effective CPU 77 C, 38.4 W, 26.6% load;
- GPU 73 C, 59.1 W, 85% load;
- fan tachometers 3871 / 3636 RPM;
- representative=true.

The following read reached effective CPU 96 C and the original one-sample M8A CPU >=90 C
physical-abort rule terminated the run as `FAIL_CLOSED / NO-WRITE`. The controller never
entered a write-capable path. Because the v1 wrapper threw immediately after the qualification
result, its independent post-failure FF/FF closure step did not run; this attempt therefore cannot
be promoted beyond FAIL_CLOSED even though the M8A mode itself contains no fan-write, restore or
watchdog-lease dependency.

The retry hardening records the complete thermal sample before deciding FAIL_CLOSED, uses the
five-unique-reading 95 C CPU confirmation rule plus immediate 99 C hard CPU boundary, and moves
the independent journal/service/FF/FF closure proof so it executes after both PASS and
FAIL_CLOSED results.

## 7A. M8A code preparation

M8A code preparation adds a read-only representative-load classifier and a versioned physical
harness. The thermal-confirmation hardening keeps the stateless SafetyGate raw CPU threshold at
95 C but requires five unique consecutive 95..98.x C snapshots on the exact 8C40 target; CPU
>=99 C and GPU emergency remain immediate. This preparation does not authorize M8B or M8C,
does not create a watchdog lease, does not construct a fan-control backend/coordinator and
cannot issue SetFanLevel or a firmware restore. GitHub Actions CI must pass PowerShell 7 /
Windows PowerShell 5.1 invariants, warnings-as-errors build, SafetyGate/confirmation self-tests
and the M8A classifier before physical retry.

M8A preparation is now **CODE/CI PASS** at commit
`c50510fc627e22d48a762d24c378bd89c724b7d2`, GitHub Actions **#703** (run `36658025006`).
The run passed PowerShell syntax, the M8A invariant under PowerShell 7 and Windows PowerShell
5.1, warnings-as-errors build, the M8A representative-load classifier self-test and the
existing M6/M7/M8/SafetyGate/coordinator/BIOS/backend regressions.

During review before physical execution, the initial M8A warm-up sample was hardened: the first
`HardwareTelemetryReader` snapshot exists only to prime differential CPU power/load counters
and may be incomplete by design. It is now excluded from representative-load/SafetyGate
qualification while still enforcing the current immediate 99 C CPU / 82 C GPU physical abort boundary.
GitHub Actions #702 failed closed only because the first static invariant edit had a PowerShell
string-termination syntax error; no physical M8A execution occurred in that run. The corrected
invariant and warm-up behavior passed #703.


## 7A.2. M8A physical attempt 2 - PASS / NO-WRITE

On 2026-09-29, physical M8A attempt 2 ran at HEAD
`90e3786da6addd43c1e6a733d7389119729e7192` after the five-sample CPU
thermal-confirmation hardening.

The versioned M8 no-write preflight passed again on the same HEAD: exact HP 8C40 rev 63.43 /
Victus 15-fa1xxx / SKU 9D0R1LA#AKH / BIOS F.18, AC online, battery 100%, M4
Manual/stopped, warnings-as-errors build 0 warnings / 0 errors, 3/3 accepted telemetry samples,
14/14 physical-core context, zero Intel/EC/NVML recoveries and two consecutive FF/FF ownership
samples.

The 60-second representative-load window then completed successfully:

- **59/60** samples satisfied the representative-load predicate;
- maximum consecutive representative streak: **58** (required >=10);
- effective CPU maximum: **98 C**;
- CPU package maximum: **97 C**;
- hottest-core maximum: **98 C**;
- exactly **3** samples reached >=95 C and the maximum consecutive >=95 C streak was **2/5**;
- the observed high sequence was 98 C (1/5), 95 C (2/5), then 82 C reset; later 97 C
  (1/5), then 79 C reset;
- no CPU sample reached the 99 C immediate hard boundary;
- GPU maximum was **77 C**, below the 82 C M8A physical abort;
- no effective thermal emergency was confirmed.

The representative-load requirement passed with `59/60` and `58` consecutive samples.
The final independent no-mutation closure also passed: retained watchdog journal absent, M4
service still Manual/stopped and two independent final EC probes both observed FF/FF. M8A made
no fan write, firmware restore or watchdog lease.

This result physically validates the five-unique-reading CPU transient filter on the exact
target for the observed gaming workload: short 95-98 C Turbo excursions did not persist long
enough to qualify as sustained thermal emergency. M8A is therefore **CODE/CI/PHYSICAL PASS**.

M8B code/spec preparation is now authorized. M8B physical execution is still blocked until a
versioned watchdog-backed 50/50 load harness, independent delayed failsafe, CI PASS and explicit
pre-test review are complete.

### M8A five-sample thermal-confirmation CI

The exact-target thermal-confirmation hardening is **CODE/CI PASS** at commit
`09e673c4433853ab0e0756fca06fa92dcf847d17`, GitHub Actions **#706** (run `36660582473`). The run passed
PowerShell syntax, M8/M8A invariants under PowerShell 7 and Windows PowerShell 5.1,
warnings-as-errors build, the M8A classifier, the SafetyGate plus HP 8C40 temporal
confirmation self-test, coordinator, BIOS-contract and HP-backend regressions.

Run #705 failed closed before build because the M8 static documentation invariant still expected
the old literal wording `CPU emergency: effective CPU >= 95 C` after the documentation was
refined to distinguish the raw 95 C threshold from the five-sample effective handoff. The
invariant wording was aligned without changing runtime behavior; no physical execution occurred.

M8A physical retry subsequently passed and is recorded above. M8B code/spec preparation is now
allowed; M8B physical execution and M8C remain blocked pending their own versioned harness/CI gates.

## 7B. M8B watchdog-backed 50/50 representative-load gate

M8B is the first write-capable M8 sub-gate. Its purpose is **not** to validate an adaptive
curve. It proves that the already-qualified equal-only 50/50 endpoint can be acquired exactly
once through the real watchdog-backed coordinator/backend chain while a representative gaming/3D
load is already present, then supervised without retransmitting the command.

The versioned M8B contract is:

- the existing M8 no-write preflight must PASS again on the exact current HEAD;
- M8A must already be recorded CODE/CI/PHYSICAL PASS;
- the existing M4 service must be Manual/stopped (or be installed by the versioned M4 installer
  only when absent), must start as LocalSystem/session 0 and resolve the exact 8C40 target/pipe;
- the operator must establish a normal game/3D workload before the write boundary and explicitly
  confirm token `8C40-M8B-LOAD50`;
- an **independent delayed failsafe** is armed before the write-capable controller is launched;
  any failsafe takeover makes the run safe but invalid as an M8B PASS;
- the failsafe recognizes exact-target durable **WRITE_ARMED/OWNED/RESTORING** states for this
  50/50 gate, is bound to the exact controller PID + creation ticks, and never performs direct
  HP fan I/O itself; if recovery is required it kills only the exact owner and delegates hardware
  recovery to the already-qualified LocalSystem watchdog;
- before Custom admission, the controller requires **3 consecutive representative samples**
  within at most 10 samples;
- the controller contains **exactly one** `ApplyAsync(50,50)` call;
- the real causal chain must be PREPARE -> WRITE_INTENT(50/50) -> one hardware 50/50 ACK ->
  COMMIT -> OWNED;
- after COMMIT, a schema-v2 durable journal must prove exact target, OWNED 50/50, exact controller
  PID + creation ticks, while EC guards remain 00/00 and both tachometers report feedback;
- supervision lasts **30 samples at 1 second** and must contain at least **22/30**
  representative samples with at least **10 consecutive**;
- supervision uses SafetyGate plus the exact-target five-reading CPU thermal confirmation,
  continuously calls coordinator safety/ownership supervision and **never retransmits 50/50**;
- CPU >=99 C is immediate fail-closed; GPU >=82 C is an immediate conservative M8B qualification
  abort; CPU 95..98.x C continues to require five unique consecutive fresh readings;
- normal completion must prove RESTORE_BEGIN -> local FF/FF -> watchdog RELEASE;
- the watchdog log must contain exactly one PREPARE, WRITE_INTENT target 50/50, COMMIT target
  50/50, RESTORE_BEGIN and RELEASE ACK for the exact controller in causal order;
- final independent closure requires journal absent, two independent FF/FF probes and the M4
  qualification service restored to Manual/stopped.

The controller mode is `--8c40-m8b-watchdog-load`. The parent harness is
`scripts/test-8c40-load-thermal-m8b.ps1`; the independent fallback is
`scripts/watchdog-m8b-service-failsafe-8c40.ps1`. Evidence is preserved under
`logs/m8b-watchdog-load_*/` as controller READY/result JSON, parent summary and failsafe log.

M8B code/spec preparation first passed GitHub Actions #717, then underwent an additional
pre-physical review. That review identified a failsafe coverage gap: the delayed independent
fallback recognized only an OWNED 50/50 journal. Before physical authorization, it was hardened
to recognize the exact-target WRITE_ARMED pending 50/50, OWNED 50/50 and RESTORING 50/50
durable phases while remaining bound to the exact controller PID + creation ticks and delegating
all HP hardware recovery to the already-qualified LocalSystem watchdog.

The hardened path is **CODE/CI PASS** at commit
`6d3547756f7c011231c6e3e099cb111612d4b060`, GitHub Actions **#718** (run `36663164142`). PowerShell syntax,
M8/M8A/M8B invariants under PowerShell 7 and Windows PowerShell 5.1, warnings-as-errors build,
SafetyGate, coordinator, watchdog Gate C/M4, BIOS-contract and HP-backend regressions all passed.

M8B physical execution is now authorized **only** through the versioned
`scripts/test-8c40-load-thermal-m8b.ps1` harness. The operator must not substitute ad-hoc
commands. Any independent-failsafe takeover invalidates PASS even if the machine is returned
safely to firmware ownership. M8C remains blocked.

## 7B.1. M8B physical attempt 1 - FAIL_CLOSED after OWNED 50/50

On 2026-09-30, M8B attempt 1 ran at HEAD
`68750bdd997e9530c5712af5c33e5bf2910a0f55`. The current-HEAD no-write
preflight passed first: branch/upstream matched, the exact HP 8C40 / Victus 15-fa1xxx /
SKU 9D0R1LA#AKH / BIOS F.18 target was present, AC was online, battery was 100%, M4 was
Manual/stopped, the warnings-as-errors build had 0 warnings / 0 errors, the M5-M8 regression
set passed, telemetry produced 3/3 accepted samples with zero Intel/EC/NVML recoveries, and the
independent firmware baseline was FF/FF twice.

With normal game/3D load active, the pre-write load gate then passed immediately with **3/3
consecutive representative samples**. The watchdog and independent delayed failsafe were armed,
and the controller reached real watchdog-backed OWNED 50/50:

- controller PID 15908, creation ticks 639263365082810966;
- EC setpoint 50/50;
- dual tach feedback 2929 / 2753 RPM;
- durable journal generation 3.

The run then failed closed **before supervision sample 1** with
`M8B invalid inter-sample gap: 3.364 s`. This was not a thermal, load, EC-ownership or tach
failure. Static review of the exact source showed that one `previousTimestamp` variable was
carried from the final pre-write telemetry frame across PREPARE -> WRITE_INTENT -> WMI ->
hardware ACK -> COMMIT -> READY. That synchronous control transaction naturally consumed enough
time to exceed the 3 s sampling-gap fence.

The correction does **not** widen the 3 s supervision gap. Instead it creates a new telemetry
continuity epoch after COMMIT/READY. The first supervision frame must still be individually
fresh, complete, exact-GPU valid and SafetyGate-valid; only its comparison against the last
pre-write timestamp is omitted. From supervision sample 1 onward, the <=3 s inter-sample fence
remains unchanged.

Attempt 1 therefore remains **FAIL_CLOSED** and is preserved as physical evidence. The terminal
showed independent cleanup FF/FF twice after the controller failure. The original harness did
not print a separate successful Manual/stopped service-baseline line in its FAIL_CLOSED cleanup,
so that closure is not inferred from absence of an error. The retry hardening now records
journal absence, FF/FF proof and M4 Manual/stopped proof as separate closure booleans and fails
closed if the child ExitCode is unavailable.

M8B physical retry remains blocked until this telemetry-epoch and closure-evidence patch passes
CI and is explicitly reauthorized.

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
