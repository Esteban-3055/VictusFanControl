# HP 8C40 M8 - representative-load and thermal-preemption qualification

Target: `HP-8C40-9D0R1LA-F18`.

Status: **NO-WRITE PREFLIGHT PHYSICAL PASS. M8A CODE/CI/PHYSICAL PASS. M8B PHYSICAL PASS ON RETRY 6 WITH 30/30 REPRESENTATIVE SUPERVISION, EXACT CAUSAL WATCHDOG CHAIN, ARMED FAILSAFE EVIDENCE AND COMPLETE FINAL CLOSURE. M8C M8B-PREREQUISITE IS SATISFIED BUT PHYSICAL EXECUTION REMAINS BLOCKED WHILE ITS CHILD-PROCESS/FAILSAFE EVIDENCE PATH IS HARDENED AND RE-RUN THROUGH CI. AUTOMATIC/ADAPTIVE POLICY OFF.**

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

### M8C synthetic preparation

M8C synthetic preparation is allowed to advance before the M8B physical retry because it performs
no hardware I/O and cannot authorize M8C physical execution. The internal
`Hp8C40M8CThermalQualificationInjection` boundary creates telemetry frames marked
`M8C_SYNTHETIC_QUALIFICATION_ONLY` and routes them only through the production
`SafetyGate`, exact-target `Hp8C40ThermalEmergencyConfirmation`, and
`FanControlCoordinator` using a synthetic backend.

The hardware-free M8C self-test covers all of the threshold/preemption semantics needed before
the physical harness is worth constructing:

- CPU effective 95 C is submitted as five unique consecutive fresh synthetic snapshots;
  raw SafetyGate sees the threshold on every frame, the 8C40 confirmation layer keeps the first
  four admitted, and the fifth must force firmware handoff;
- GPU = 87 C must force immediate handoff on the first synthetic snapshot;
- effective CPU >=99 C must force immediate hard handoff without consuming a five-sample streak;
- thermal handoff racing an in-flight `ApplyAsync` must cancel the command and finish in
  firmware authority.

The injection helper is intentionally hardware-free: it cannot construct HP WMI/EC hardware,
the real HP 8C40 backend or a watchdog lease, and production GUI/runtime code must not reference it.
Static invariants enforce that isolation.

**M8B physical PASS is now satisfied; M8C physical execution remains blocked pending post-M8B process/evidence hardening CI and a separate explicit authorization.** A green synthetic M8C result
proves only threshold, sequencing and coordinator-preemption behavior; it cannot prove local
FF/FF restore acknowledgement, watchdog Release, journal cleanup, dual-tach behavior or the
real physical race timing required for M8C closure.

The synthetic M8C block is **CODE/CI PASS** at commit
`738e308def4824c9200f9c364121a8a1d56ce0f1`, GitHub Actions **#737**
(run `36667160690`). The workflow passed PowerShell syntax, the M8C static invariant under
PowerShell 7 and Windows PowerShell 5.1, warnings-as-errors build, the synthetic M8C
thermal-preemption self-test and the existing M5-M8/SafetyGate/coordinator/backend regressions.
An earlier intermediate run (#731) failed at compile time because the new helper referenced the
14-core count as a nonexistent static target-profile member; the code was corrected to use the
versioned target-profile instance and #737 passed. No hardware execution occurred.

### M8C physical-controller preparation

The real-hardware M8C controller remains prepared in code but deliberately unreachable while post-M8B process/evidence hardening is validated. Its
`PhysicalExecutionAuthorized=false` readonly authorization flag is checked before hardware identity, PawnIO,
watchdog, WMI, EC or telemetry hardware objects are constructed. The profile independently keeps
`physicalExecutionAuthorized=false`; the M8B physical prerequisite is now recorded PASS.

When a later evidence-backed commit authorizes it, each physical subcycle will:

1. prove representative real load and firmware-owned FF/FF;
2. acquire the real M4 watchdog-backed Custom path;
3. issue exactly one already-qualified real 50/50 command and prove EC + dual-tach ownership;
4. prove a fresh representative real sample below the CPU temporal candidate threshold;
5. publish `M8C_READY_BEFORE_INJECTION` and stop;
6. require a parent-side `M8C-CONTINUE` marker only after the parent has independently checked
   watchdog/journal/failsafe evidence;
7. execute either the CPU five-frame 95 C synthetic subcycle or the one-frame GPU 87 C subcycle
   through the real coordinator;
8. require firmware authority and local FF/FF after preemption.

The hard-CPU 99 C case remains synthetic/code-only because no real-silicon excursion is required.
The parent physical harness and independent journal/failsafe closure are intentionally separate
from the controller. **M8B physical PASS is now satisfied; M8C physical execution remains blocked pending post-M8B process/evidence hardening CI and a separate explicit authorization.**

### M8C parent physical harness preparation

The parent harness `scripts/test-8c40-load-thermal-m8c.ps1` and its independent delayed failsafe
are prepared but remain unreachable. A **profile gate** is the first executable boundary in the
script and requires both `m8b.physicalPassed=true` and
`m8c.physicalExecutionAuthorized=true` before Administrator checks, service operations, process
launches, PawnIO probes or fan-control activity can occur.

Once a future evidence-backed commit authorizes that boundary, the harness performs **two independent physical subcycles**:

- CPU: real representative load + real watchdog-backed 50/50 ownership, parent journal/failsafe
  proof, then five qualification-only 95 C frames with handoff required on frame 5/5;
- GPU: a fresh independent real 50/50 ownership cycle followed by one qualification-only 87 C
  frame with immediate handoff.

Each subcycle arms an independent delayed failsafe before launching the write-capable controller,
proves the exact schema-v2 OWNED 50/50 journal bound to PID + process creation time before writing
`M8C-CONTINUE`, and requires the causal watchdog sequence
PREPARE -> WRITE_INTENT -> COMMIT -> RESTORE_BEGIN -> RELEASE. It then independently proves
journal absence, two FF/FF observations and restoration of the M4 service to Manual/Stopped.

The M8B side of the profile gate is now satisfied, but M8C authorization remains false and the controller independently retains
`PhysicalExecutionAuthorized=false`. Preparing these files does not authorize or perform M8C.

The complete no-hardware preparation is **CODE/CI PASS** at commit
`ccb11f285a92c6f789929eee6911b5213fd95ddd`, GitHub Actions **#773**
(run `36669038036`). That full workflow passed PowerShell syntax, all M5-M8 invariants under
PowerShell 7, the M8C controller/harness invariants under Windows PowerShell 5.1,
warnings-as-errors build, M8A classifier, M8C synthetic preemption, watchdog M2/M3/M4
self-tests, SafetyGate, coordinator, adaptive-policy, BIOS-contract and HP-backend regressions.
No physical M8B/M8C execution occurred.


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

The deterministic production-race audit is **CODE/CI PASS** in GitHub Actions #773. This closes
the code/synthetic ordering questions listed above, but it does not replace the remaining real
under-load M8B retry or the eventual real restore/journal/tach timing evidence required by M8C.


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

The telemetry-epoch and closure-evidence hardening is **CODE/CI PASS** at commit
`7b5b104c9cd3c1f695f217693043383299410ec2`, GitHub Actions **#726** (run `36666102187`). PowerShell syntax,
M8B invariants under PowerShell 7 and Windows PowerShell 5.1, warnings-as-errors build,
SafetyGate, coordinator, watchdog and HP-backend regressions all passed. Run #725 failed only
because the new documentation invariant searched for a literal phrase split by a Markdown line
break; aligning that invariant did not change runtime behavior.

M8B physical attempt 2 is now authorized only through the versioned harness. The 3 s gap limit
remains unchanged **inside** the supervision epoch; the patch only prevents the intentional
PREPARE/WRITE_INTENT/WMI/ACK/COMMIT transaction from being misclassified as a telemetry/lifecycle
sampling gap.

## 7B.2. M8B physical attempt 2 - FAIL_CLOSED / NO-WRITE

On 2026-09-30, the operator supplied a partial terminal excerpt for the second physical
attempt, with durable controller evidence at
`logs/m8b-watchdog-load_2026-09-30_005823/m8b-result.json`.
The excerpt does not show the initial repository HEAD; the preserved result/summary must be
checked before attributing an exact evidence SHA.

All **10/10 pre-write samples were non-representative**, with 0/3 consecutive representative
samples. CPU load ranged from 20.2% to 67.8% and package power from 28.0 W to 91.8 W;
the CPU activity requirement was fulfilled. GPU power remained **4.3..17.8 W** even when
GPU load intermittently crossed 35%. The unchanged representative predicate requires
**GPU load >=35% AND GPU power >=20 W**, plus material CPU activity. The classifier correctly
failed closed **before PREPARE, WRITE_INTENT, the only allowed 50/50 ApplyAsync, COMMIT or READY**.
No M8B fan write was attempted. The terminal showed independent cleanup FF/FF twice and
M4 `StartType=Manual, Status=Stopped`; the journal/failsafe outcome requires confirmation
from the retained harness summary rather than assumption from terminal silence.

One plausible explanation is that foregrounding PowerShell to type the token makes the game
reduce background rendering; this is **not proven** by the excerpt. The harness now prints an
explicit request to return to active gameplay/rendering and provides a fixed
**5-second no-write refocus grace** immediately after the exact token and *before* starting
the M4 watchdog, failsafe or write-capable controller. GPU thresholds, 3/10 pre-write
consecutive requirement, safety boundaries and watchdog ordering remain unchanged.

The parent also surfaces durable `m8b-result.json` failureReason when the child exits before
READY instead of showing only an empty `ExitCode=`. This improves diagnosis without
changing fan hardware operations.

The post-token refocus and early-failure diagnostic patch passed the full GitHub Actions
workflow **#797** (run `36676421562`) on exact HEAD
`68e190dea8d3131fb74c87429e3ddbf32b2294c4`. M8B/M8C invariants, PowerShell syntax and 5.1 compatibility,
warnings-as-errors build, SafetyGate, fan coordinator, watchdog, M8C synthetic,
HP backend and adaptive shadow-isolation regressions passed.

**M8B physical attempt 3 is now authorized**, only through the versioned M8B harness, after
its fresh same-HEAD no-write preflight and independent failsafe. The operator must return
focus to the normal game during the five-second post-token grace; insufficient load still
fails closed without any fan write. No prior M8B attempt is promoted to PASS. M8C physical
qualification and automatic/adaptive production remain blocked.

## 7B.3. M8B physical attempt 3 - FAIL_CLOSED / NO-WRITE

The third operator-supplied M8B terminal excerpt on 2026-09-30 reports controller evidence
at `logs/m8b-watchdog-load_2026-09-30_031121/m8b-result.json` and parent evidence at
`logs/m8b-watchdog-load_2026-09-30_031121/m8b-harness-summary.json`. The excerpt starts
at step 6 and does not independently show the preflight/HEAD or the durable closure booleans.
The versioned authorizing HEAD was `71db885fe0158be4298dba91f813f6c25d0c1ee4`;
attribute the physical run's actual HEAD only after checking the retained harness summary.

The controller's initial firmware-owned EC baseline showed one isolated unexpected read that
recovered on **read 2/3**. No persistent unexpected state was reported; the first raw EC value
was not included in the console excerpt and must not be inferred.

The pre-write sample window yielded **2/10 representative samples** (indices 4 and 9) and
**maximum consecutive streak 1/3**. GPU telemetry showed the precise limiting behavior:
sample 3 = 37%/19.9 W (under power limit); sample 4 = 35%/21.2 W (representative);
sample 9 = 38%/21.5 W (representative); sample 10 = 30%/25.4 W (under GPU load limit).
GPU temperature was 49..56 C; CPU effective maximum 93 C, and CPU activity was material.
The fixed predicate is GPU load >=35% **AND** GPU power >=20 W **AND** material CPU activity;
three consecutive qualifying samples were never obtained.

The controller therefore emitted `FAIL_CLOSED`:
`M8B representative load was not established before the write boundary`.
Its classifier throws before Custom admission/coordinator/backend construction, PREPARE,
WRITE_INTENT, ApplyAsync, COMMIT and READY, so **no M8B fan write occurred**.
The parent printed two independent final FF/FF observations and M4 Manual/stopped.
The `finalJournalAbsent`, `finalClosurePass` and `failsafeTakeover` outcome must still
be checked directly from the preserved harness summary, rather than inferred from
absence of an alarm.

The five-second post-token refocus grace operated before watchdog/controller launch, but it
did not establish sufficiently sustained GPU demand. A CPU-limited or capped scene, a background
rendering behavior, or a different workload from the earlier M8A physical PASS are possible
explanations, not proven diagnoses. The earlier M8A attempt 2 had 59/60 representative samples
and GPU power reaching 70.4 W; this is evidence that representative load was achievable under
a different observed workload condition.

**M8B attempt 4 remains blocked** pending durable evidence/workload review. Do not lower
35%/20 W or remove the three-consecutive rule to force a PASS; do not rerun M8B blindly.
M8C hardware qualification and production watchdog/adaptive policy remain blocked.

## 7B.4. M8B physical attempt 4 - CONTROLLER PASS / PARENT FAIL_CLOSED

The operator reports a later M8B run under **God of War 2018** with preserved evidence at
`logs/m8b-watchdog-load_2026-09-30_032326/`. The console excerpt begins at step 6,
so its exact physical-run HEAD and preceding preflight must be confirmed from the parent
summary rather than inferred from the current Git branch.

The real pre-write classifier passed 3/3 representative samples: GPU utilization
91/99/98%, GPU power 62.7/68.3/68.1 W. The controller reached real READY at
PID **13688**, creation ticks **639263462484122422**, durable journal generation **3**,
EC **50/50**, dual tach **4400/4076 RPM**. Every printed supervision sample remained
representative with exact EC 50/50: **30/30 representative**, maximum consecutive **30**,
CPU effective temperature maximum **76 C**, GPU maximum **74 C**, GPU power maximum
**70.7 W**, CPU high-temperature streak 0/5. The controller printed
`PASS: HP 8C40 M8B watchdog-backed 50/50 representative-load gate completed`,
following its normal restore logic.

The **parent nevertheless FAIL_CLOSED** before reaching `Assert-CausalServiceLog` because
the PowerShell `Start-Process -PassThru` child object returned an unavailable `ExitCode`
after exit (`M8B controller terminated but ExitCode was unavailable.`).
The parent cleanup printed independent firmware-owned **FF/FF twice** and service
`Manual/Stopped`. This is a distinction between physical controller evidence and the
parent's formal harness outcome; do not promote the run to M8B PHYSICAL PASS from console text.

**Full causal chain and no-failsafe-takeover proof are still pending.** Inspect the retained
`m8b-ready.json`, `m8b-result.json`, `m8b-harness-summary.json`,
`m8b-failsafe.log` if present, plus the dated watchdog M4 service log. An offline
evidence review must confirm exact HEAD/target/process identity, normal restore/Release,
one PREPARE -> WRITE_INTENT 50/50 -> COMMIT 50/50 -> RESTORE_BEGIN -> RELEASE,
independent final journal absence and FF/FF, and no independent failsafe takeover.
The parent summary's `causalChainPass=false` is expected from the early ExitCode abort
and cannot be treated as proof that the causal chain did or did not occur.

The code-level correction moves controller launch and exit waiting to a directly owned
`System.Diagnostics.Process` with a bounded native `WaitForExit` and `ExitCode`
read from the same instance; a hardware-free synthetic regression covers exit status
0 and 7 under PowerShell 7 and Windows PowerShell 5.1. This does not waive the exit-code
requirement, change fan thresholds, redo any physical hardware test, or infer a PASS.

The tracked native-child code/invariant correction is **CODE/CI PASS** at commit
`1d672389ee2bc7980bdfd38aa56fbeb390567f32`, GitHub Actions **#801** (run `36678972013`).
Both PowerShell 7 and Windows PowerShell 5.1 exercised successful child exit code 0 and
nonzero child exit code 7; full M4-M8/Adaptive invariants, warnings-as-errors build,
SafetyGate, coordinator, BIOS and backend regressions passed. Run #800 failed solely on a
static invariant string left pointing to the old launch form, before executing the new
native-child test; that assertion was aligned and #801 passed fully.

At HEAD `b350106c824af9953c2541f651f62418396ac1e4`, **M8B physical execution remained blocked** pending retrospective evidence audit. The original independent M4/failsafe logs are now confirmed unavailable, so that historical attempt remains incomplete rather than being promoted.

A fresh **M8B retry 5 is explicitly authorized** through the unchanged versioned M8B harness after the native tracked-child ExitCode fix. This authorization does not change the 50/50 target, representative-load predicate, thermal limits, watchdog protocol, failsafe behavior, or closure requirements. M8C physical execution, production watchdog promotion and automatic/adaptive policy remain OFF.


## 7B.5. M8B physical retry 5 - explicitly authorized

Because the original attempt-4 independent M4 service log and optional failsafe log are no longer
available, the previous run cannot be promoted retrospectively. Repeating M8B is therefore
explicitly authorized solely to obtain a complete, self-consistent evidence set using the already
CI-validated native tracked-child ExitCode path.

Retry 5 must use only `scripts/test-8c40-load-thermal-m8b.ps1` on the exact
`HP-8C40-9D0R1LA-F18` target. All existing criteria remain unchanged: normal representative
game/3D load, 3 consecutive qualifying samples before the write, exactly one equal-only 50/50
ApplyAsync, 30-second supervision with >=22/30 representative and >=10 consecutive, CPU temporal
confirmation/hard boundary unchanged, GPU 82 C physical abort unchanged, exact M4 watchdog identity,
schema-v2 journal binding, causal PREPARE -> WRITE_INTENT -> COMMIT -> RESTORE_BEGIN -> RELEASE,
no failsafe takeover, final journal absence, two independent FF/FF observations, and M4 restored to
Manual/Stopped.

Immediately after the run, preserve the entire newly created `logs/m8b-watchdog-load_*` directory
and copy the same-date `C:\ProgramData\VictusFanControl\WatchdogM4\logs\watchdog-m4-8c40-YYYY-MM-DD.log`
into that evidence directory before cleanup or another qualification attempt. The harness-generated
`m8b-failsafe.log` must also be kept if present. Do not lower load thresholds or bypass a FAIL_CLOSED
to force PASS.

This authorization is **M8B only**. M8C physical execution remains blocked; production watchdog
promotion and automatic/adaptive fan control remain OFF.


## 7B.6. Attempt-4 retrospective chain recovered; retry 5 FAIL_CLOSED exposes a diagnostic gap

The evidence bundle preserved after retry 5 included the complete dated M4 service log for
2026-09-30. That log also contains the earlier God of War attempt-4 controller PID **13688**,
creation ticks **639263462484122422**, matching the preserved READY/result/summary identity.
For that controller the dated service log contains exactly one ordered:

`PREPARE -> WRITE_INTENT target=50/50 -> COMMIT target=50/50 -> RESTORE_BEGIN -> RELEASE`.

The historical attempt-4 summary configured the independent failsafe for **120 s**, while the
controller/harness attempt completed in about **44 s**. The versioned failsafe sleeps for the full
delay before its first action, and the parent cleanup terminates it if the run has already ended.
Therefore absence of `m8b-failsafe.log` in that historical bundle is expected and is consistent
with `failsafeTakeover=false`; it is not evidence of a hidden takeover. Attempt 4 is therefore
classified as having a **retrospectively reconstructed causal evidence PASS**, while its historical
parent result remains FAIL_CLOSED because the old PowerShell child ExitCode gate aborted before
the parent itself called the causal-log assertion.

Retry 5 ran on HEAD `fc31d42b7e2d06d7a52b34cb19e59f3d8e0dffe6` under God of War 2018.
It admitted 3/3 representative pre-write samples, issued exactly one 50/50 command, reached READY
with controller PID **14472**, creation ticks **639263896604537167**, journal generation **3**,
and recorded **27/27 representative consecutive supervision samples** before the next supervision
cycle returned authority to firmware. All 27 persisted samples showed EC 50/50; CPU effective
temperature stayed <=70 C, GPU <=74 C and CPU95 streak remained 0/5.

The controller result was FAIL_CLOSED with:

`M8B safety/ownership supervision returned authority to firmware:`

and no text after the colon. The SafetyGate reason list was empty. The current M8B controller only
prints `safety.Reasons` when `EnforceSafetyAsync` returns false, even though the coordinator can
also return false after a backend ownership/feedback status failure. The exact backend
`FanBackendStatus.Detail` is present in the coordinator's Custom->Restoring transition reason, but
retry 5 did not subscribe to/persist that transition. The exact failing backend state is therefore
not recoverable from retry-5 evidence.

The retry-5 watchdog log still proves ordered
`PREPARE -> WRITE_INTENT -> COMMIT -> RESTORE_BEGIN -> RELEASE` for PID 14472. Parent cleanup
proved journal absent, two FF/FF observations and M4 Manual/Stopped; the summary reports
`failsafeTakeover=false` and `finalClosurePass=true`. The failure was therefore safe, but it
reopens M8B until the intermittent status handoff is explained.

### Diagnostic-only observability hardening

Before any further physical write, the M8B qualification controller is hardened only for evidence:

- subscribe to `FanControlCoordinator.AuthorityChanged`;
- persist every authority transition and the exact Custom->Restoring reason;
- preserve the failing supervision telemetry/SafetyGate snapshot in structured JSON;
- include the captured handoff reason in the thrown FAIL_CLOSED message;
- print `M8B_AUTHORITY` transitions to the terminal;
- make the independent failsafe write an `M8B FAILSAFE ARMED` line before its delay;
- store failsafe PID and log-presence state in the parent summary.

No fan target, command count, load predicate, SafetyGate threshold, thermal confirmation, watchdog
lease behavior, ownership rule or restore behavior is changed. At the diagnostic-preparation commit,
further M8B physical execution remained blocked pending complete CI and a separate authorization.
That CI/authorization boundary is closed in the following subsection. M8C remains physically blocked
and automatic/adaptive control remains OFF.


## 7B.7. Diagnostic hardening CI PASS; M8B retry 6 authorized

The diagnostic-only hardening is **CODE/CI PASS** at commit
`d6b625d006f546e71ad11cf619c8413bf4209bc9`, GitHub Actions **#811**
(run `36761787998`). PowerShell syntax, M4-M8 invariants under PowerShell 7 and Windows
PowerShell 5.1, warnings-as-errors build, native M8B child ExitCode regression, SafetyGate,
FanControlCoordinator, watchdog and HP backend self-tests all passed.

The change does not relax fail-closed behavior. It only makes the previously lost failure cause
durable: authority transitions, the exact Custom->Restoring reason, the failing supervision
snapshot and the independent failsafe ARMED marker are now preserved.

One bounded **M8B retry 6 is explicitly physically authorized** through
`scripts/test-8c40-load-thermal-m8b.ps1` on the exact target. All physical criteria remain
unchanged. If the status anomaly recurs, the run must stop after FAIL_CLOSED and
`supervisionFailure` plus `lastCustomHandoffReason` must be reviewed before any further
write-capable attempt. If the full 30/30 window and normal closure pass, the complete M8B evidence
set can then be reviewed for gate closure.

This authorization does not authorize M8C, production watchdog promotion, or automatic/adaptive
fan control.


## 7B.8. M8B retry 6 - PHYSICAL PASS / gate closed

Retry 6 ran on 2026-09-30 at HEAD
`b634d1581e8837c19f60c26fbd01fcff40639e83` using God of War 2018 and the
diagnostic-only hardened harness.

The preserved evidence set is complete:

- controller PID **3896**, creation ticks **639263917907280082**;
- watchdog PID **23700**, creation ticks **639263917888353441**;
- independent failsafe PID **22728** with durable `M8B FAILSAFE ARMED` evidence before controller launch;
- journal generation **3** at OWNED;
- 3/3 consecutive representative pre-write samples;
- exactly one real equal-only 50/50 ApplyAsync;
- READY EC 50/50, guards 00/00 and dual tach **3776/3307 RPM**;
- **30/30 representative** supervision samples, maximum consecutive **30**;
- effective CPU **63..88 C**, GPU **68..72 C**, GPU power **58.549..74.869 W** and GPU load **93..100%**;
- CPU95 confirmation streak remained 0/5 and no EC transient recovery was required;
- `normalRestoreCompleted=true`, `finalFirmwareOwned=true`;
- no failsafe takeover;
- exactly one ordered watchdog chain for the same controller:
  `PREPARE -> WRITE_INTENT 50/50 -> COMMIT 50/50 -> RESTORE_BEGIN -> RELEASE`;
- parent summary `result=PASS`, `causalChainPass=true`,
  `finalClosurePass=true`, `finalJournalAbsent=true`,
  `finalFirmwareProofPass=true`, `finalServiceBaselinePass=true`.

The diagnostic fields also behaved as intended: there was no abnormal supervision handoff,
`supervisionFailure=null`, and the only Custom->Restoring reason was the expected normal
M8B release after the full 30-s window.

**M8B is physically closed PASS.** Further M8B write-capable qualification is disabled in the
profile. This PASS does not promote production watchdog recovery and does not enable automatic or
adaptive fan control.

### Post-M8B M8C process/evidence hardening

Review of the already-prepared M8C parent found one issue that must be fixed before M8C can be
authorized: it still launches the controller with PowerShell `Start-Process -PassThru`, the same
process-wrapper pattern that previously made M8B child ExitCode unavailable. M8C also arms a delayed
failsafe but, unlike the hardened M8B path, does not durably prove `ARMED` before controller launch.

Therefore the M8B prerequisite is now satisfied, but M8C remains physically blocked while the
following hardware-neutral changes pass CI:

- directly owned `System.Diagnostics.Process` controller launch and bounded ExitCode read;
- exit-code 0/7 host-only regression under PowerShell 7 and Windows PowerShell 5.1;
- `M8C FAILSAFE ARMED` written before the failsafe delay;
- parent waits for that ARMED marker before launching the real controller;
- failsafe PID/log-presence retained in subcycle evidence.

No M8C threshold, fan level, representative-load criterion, watchdog lease rule or restore behavior
is changed by this hardening.

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
