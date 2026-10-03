# P16 — normal user Manual promotion

## P16A architecture preparation

P16 starts from the formally closed P15D2 baseline:

- HEAD: `c1e04963448e78a87dccc1719f11d9951f2970a7`
- CI: #1180 / run `37050355620` / SUCCESS
- P15A/P15B/P15C/P15D1/P15D2 stay closed and re-blocked.

P16 does not reopen P15.

The first P16A step adds a new qualification-only source gate,
`Hp8C40P16NormalManualQualificationGate.PhysicalExecutionAuthorized`,
and wires it only into the ordinary application's Manual authorization path.
The gate is false in P16A.

No P16 command-line startup mode, marker-root argument or alternate MainForm
qualification state machine is introduced. When a later bounded authorization
opens this dedicated gate, the normal application must still start in Firmware
and reuse the existing P13 surface, AdaptiveFanProductionController,
FanControlCoordinator, HP 8C40 backend and M4 watchdog.

The permanent user gate remains independently false:

- `Hp8C40PostM9UserControlGate.ManualExecutionAuthorized = false`
- `Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized = false`

Candidate V1 remains physically unvalidated and production-unauthorized.
Control remains disabled by default.

## Planned bounded sequence after P16A is fully prepared and closed

Firmware -> Manual -> 30/30 -> 40/40 -> 30/30 -> Firmware -> tray Exit.

The future parent harness must independently prove EC setpoints, durable
ownership generations 3 -> 5 -> 7, strong restore, journal absence, causal
watchdog RESTORE_BEGIN/RELEASE and final FF/FF. Automatic is forbidden.

P16A architecture preparation itself performs no hardware execution and opens
no physical gate. Harness, failsafe and evidence packaging are the next
software-only P16A substep and must also be closed by same-head CI before any
P16B physical authorization.

P16C permanent Manual promotion is a later separate commit after a physical
PASS has been independently audited and evidence-closed.


## P16A implementation staged after architecture CI

The architecture commit `8fbe8aba0dc892f0fba988246164a13357325a7c`
passed full CI #1182 / run `37057172456` with the P16 architecture
invariant and all historical regressions green.

The next P16A software-only layer now stages:

- `scripts/test-p16-normal-manual.ps1`: parent harness for the normal app;
- `scripts/package-p16-evidence.ps1`: source/evidence manifest + ZIP;
- `scripts/test-p16-evidence-packaging.ps1`: deterministic safety-contract self-test;
- external audit of the normal persistent application log;
- independent read-only EC setpoint proof;
- schema-v2 journal PID/start/session/generation proof;
- watchdog causal PREPARE -> three WRITE_INTENT/COMMIT pairs -> RESTORE_BEGIN -> RELEASE proof;
- final and post-tray-exit FF/FF proof.

P16A deliberately reuses `scripts/watchdog-p15d2-service-failsafe-8c40.ps1`
as the delayed independent recovery process. That failsafe is already constrained
to the exact HP 8C40 target and equal 30/30 or 40/40 retained lease states,
which exactly covers the bounded P16 sequence. Reusing it avoids creating a
second recovery implementation with equivalent authority.

The P16 source gate remains false in this implementation. Therefore CI can
parse, build and statically audit the complete future physical harness while no
P16 hardware execution is possible.

Formal P16A closure requires full SUCCESS on the exact implementation HEAD.


### Preserved software-only CI failure

Implementation HEAD `3ae998ea72acaec2f562e87c6737556a89b41eb6`
produced CI #1183 / run `37058121007` FAILURE at the P16 static
preparation invariant. The invariant used a double-quoted source-search
literal containing PowerShell variable names, so the variables expanded before
the harness text comparison. The physical gate was still false, no target
hardware execution occurred, and the failure is preserved rather than removed.
The follow-up changes only that invariant literal.


### Second preserved software-only invariant failure

Correction HEAD `a21bbba6c3bad64d6ce1a0121fc832498806d383`
produced CI #1184 / run `37058417708` FAILURE in the same static P16
invariant. The harness regex correctly escapes the final period in the startup
log pattern, but the source invariant searched for the unescaped full string.
The runtime/harness behavior was not executed and no physical gate was open.
The next correction only makes that static source assertion insensitive to the
regex escape.


## P16A formal software closure

The completed P16A implementation/correction HEAD
`635e0a0d83922331a4a940206f150d7c68c8b01b` passed full same-head
CI #1185 / run `37058666450` / SUCCESS.

That run validated the P16 preparation invariant, P16 evidence packaging,
Windows PowerShell 5.1 compatibility, the warnings-as-errors build and the
historical regression suite.

P16A is therefore software-complete. The dedicated parent/source physical
gates remain false and permanent user Manual is still false. No P16 target
execution has occurred.

The next milestone is P16B. It must be opened by a separate authorization
commit from this closed baseline and that authorization HEAD must itself pass
full same-head CI before `scripts/test-p16-normal-manual.ps1` may be run on
the target.

## P16B bounded physical authorization

P16A formal closure HEAD `a5c987ee539235ae8325379edb135790377f55b4`
passed full same-head CI #1186 / run `37059019922` / SUCCESS.

P16B opens only the dedicated
`Hp8C40P16NormalManualQualificationGate.PhysicalExecutionAuthorized`
bridge and the matching versioned parent/source authorization flags. It does
not promote the permanent user Manual gate.

The following remain false:

- `Hp8C40PostM9UserControlGate.ManualExecutionAuthorized`;
- `Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized`;
- `control.enabledByDefault`;
- Candidate V1 `PhysicallyValidated`;
- Candidate V1 `AuthorizedForProduction`.

The authorization commit performs no target hardware execution. The physical
harness remains blocked until the exact P16B authorization HEAD itself has
completed full same-head CI SUCCESS. Only after that CI result may
`scripts/test-p16-normal-manual.ps1` be run on the exact HP 8C40 target.

The bounded physical sequence remains:

`Firmware -> Manual -> 30/30 -> 40/40 -> 30/30 -> Firmware -> tray Exit`.

No Automatic request, profile change, Candidate promotion, power-limit work or
P15 gate reopening is part of P16B.

## P16B target attempts — FAIL_CLOSED and gate re-blocked

Authorization HEAD `0eba7426455adcca2594a612abd2c5753a52d235`
passed same-head CI #1187 / run `37064935429` / SUCCESS.

Two target evidence packages from that authorization are preserved and both are
FAIL_CLOSED; neither is a P16 PASS.

The first package,
`p16-normal-manual_2026-10-02_181225.zip`
(SHA-256 `2f164727994683bb7f5c1d49eeff871b017d6a129dc4c6de9ca1e5c51d44a178`),
proved the initial 30/30 generation-3 ownership. A later 40 request encountered
an ownership mismatch (expected 30/30, observed 40/30), restored Firmware, and a
subsequent new Custom admission made the original generation-5 same-session
contract invalid.

The second package,
`p16-normal-manual_2026-10-02_181434.zip`
(SHA-256 `c26eb3afab86a6998c8f52233165b276e4bd1d52a4ec803ef03bc9192978a661`),
proved 30/30 generation 3 and 40/40 generation 5 in one session. The real GUI
then logged the return 30/30 Apply as successful, but the parent independent EC
probe failed to acquire `Global\Access_EC` within the current single 500 ms
mutex-acquisition window. Final Firmware/tray evidence was therefore not
claimed.

Review of both packages exposes software hardening work before any retry:
bounded handling of transient EC-mutex acquisition contention, immediate harness
abort on an expected interaction that logs FAILED CLOSED, and a one-shot
qualification-attempt fence so one authorization cannot silently be reused.

The dedicated P16 physical gate is re-blocked. Permanent user Manual, Automatic,
Candidate V1 and default control remain closed. A retry requires software-only
hardening, same-head CI closure and a fresh authorization.

## P16B software-only hardening staged

The two preserved target failures are treated as qualification evidence, not as
reasons to relax ownership or SafetyGate behavior.

This hardening keeps every P16 physical gate false and makes four narrow changes:

1. The production HP 8C40 setpoint-acknowledgement loop tolerates at most two
   transient `TimeoutException`/`IOException` EC setpoint read failures.
   A third failed snapshot remains fail-closed and uses the existing restore
   path. The 500 ms `Global\Access_EC` mutex timeout itself is not globally
   increased, and ownership mismatches such as `40/30` remain failures.
2. The independent P16 parent EC probe retries only the exact
   `Timed out waiting for Global\Access_EC.` condition, at most three times.
   Other probe failures are not retried.
3. Each requested GUI interaction now has a per-action log cursor. A matching
   `FAILED CLOSED`/blocked line wins immediately over any later success, so a
   second click or a new Custom session cannot accidentally satisfy the same
   qualification step.
4. A durable one-shot attempt fence is atomically created under ProgramData,
   keyed by the exact authorization HEAD. Re-running a consumed authorization
   is refused before the failsafe/GUI physical boundary; a fresh authorization
   commit produces a new key.

Deterministic C# backend tests cover transient setpoint-read recovery and
bounded repeated failure. A PowerShell helper self-test covers transient-only
retry classification, first-failure interaction ordering and one-shot attempt
consumption. The attempt-fence record is included in physical evidence whenever
a token-bearing attempt claims it.

This commit is software-only. It does not reopen P16, permanent Manual,
Automatic, Candidate V1 or default control. Full same-head CI SUCCESS and a
separate formal hardening closure are required before a fresh P16B
authorization.

### Preserved hardening CI syntax failure

Hardening implementation HEAD
`77e3fae3300ed6a1b9a6190c84d75fd110cfb401` produced CI #1189 /
run `37067450978` FAILURE at the PowerShell syntax-check step.

The failure was software-only. The P16 physical source/contract gates were
already false and no target execution occurred.

The cause was generation of `test-p16-normal-manual.ps1`: a JavaScript
`String.replace` replacement string contained the PowerShell regex end-anchor
sequence `$'`, which JavaScript interpreted as a replacement-token for the
suffix of the original string. That truncated `Read-8C40Setpoint` before its
closing regex/string/function body.

The correction rebuilds that function as a complete source slice rather than a
replacement-string expansion and additionally makes the interaction waiter skip
resolver invocation until at least one new log line exists. The P16 physical
gate remains closed.

### Preserved CI #1190 interpolation syntax failure

Correction HEAD `3c299bb0bb9b82fbb13690959adb9d200aec1c31` produced
CI #1190 / run `37067717318` FAILURE at PowerShell syntax check. The
remaining parse error was the interpolated fragment `$Label:`, which
PowerShell interprets as a drive-qualified variable reference. It is corrected
to `${Label}:`.

This was software-only: the dedicated P16 physical gate remained false and no
target hardware execution occurred.


### Preserved CI #1191 and clean harness reconstruction

HEAD `7ed5f4d81c0f116942766e7603304401f90a494d` preserved CI #1191 /
run `37068264838` as another software-only PowerShell syntax failure.

The colon-adjacent Label interpolation itself was corrected, but review of the
complete file showed that the prior source-generation corruption had also left a
duplicated trailing harness fragment after the intended final `exit 1`.
That fragment produced the remaining unexpected `)` / `}` parser errors.

The follow-up does not patch that corrupted tail incrementally. It reconstructs
`test-p16-normal-manual.ps1` from the last known-good pre-hardening harness
(`59df2b5c1edb0d107e7d766960a439dd79c40a60`) and reapplies only the reviewed
P16B hardening deltas: bounded parent EC contention retry, first-outcome
interaction fencing, one-shot authorization consumption and evidence tracking.

Static invariants now require exactly one setpoint-probe function, one stable
setpoint proof function, one interaction waiter, the safe formatted failure
diagnostic, and a clean final fail-closed `exit 1`. All physical gates remain
closed.

## P16B hardening formally closed after CI #1192

The reconstructed software-only hardening HEAD
`6539671204d3e9c548d1d9b5b553920ccd553525` passed full same-head CI
#1192 / run `37069306742` with PowerShell syntax validation, the P16 invariant,
the dedicated P16B hardening helper self-test, Windows PowerShell 5.1
compatibility, warnings-as-errors build and the HP fan backend self-test all
passing.

The hardening correction is therefore formally closed. This closure is still
software-only: the dedicated P16 physical source gate remains false, permanent
Manual remains false, Automatic remains false, Candidate V1 remains unvalidated
and unpromoted, and default control remains disabled.

The next physical step is intentionally separate. Only after this closure commit
itself receives full same-head CI SUCCESS may a fresh one-shot P16B
authorization be created. The prior authorization is consumed and cannot be
reused.

## P16B fresh one-shot reauthorization after closure CI #1193

Formal hardening-closure HEAD
`0b32d210468c4bf4a535566da6d5c6bae3b3f3a9` passed full same-head CI
#1193 / run `37069744953` SUCCESS.

A fresh P16B authorization now opens only the dedicated normal-application
Manual qualification gate and matching contract gate. Permanent user Manual,
Automatic, Candidate V1 and default control remain closed.

This authorization commit performs no hardware execution. The target harness is
still forbidden until this exact authorization HEAD itself passes full same-head
CI. After that external barrier is satisfied, the ProgramData one-shot attempt
fence atomically consumes this exact authorization HEAD before the failsafe/GUI
physical boundary, preventing reuse of the same authorization for a second
attempt.

The original authorization `0eba7426455adcca2594a612abd2c5753a52d235`
remains preserved as consumed with its two FAIL_CLOSED attempts and cannot be
reused.

## P16B attempt 3 — preserved FAIL_CLOSED and immediate re-block

Authorization HEAD `258d39cd5ab968b55983447f409f2210c7c1fc65` passed same-head CI #1194 / run `37070246335` SUCCESS and was consumed exactly once by the durable ProgramData attempt fence.

Evidence `p16-normal-manual_2026-10-02_190623.zip` has SHA-256 `35bfd02daafe126f72af096e57a49c709195e05d563c1fa0666f459bcb2038bd`. The session completed the real normal-app Manual sequence 30 -> 40 -> 30 and then the real Firmware restore. Generation 3/5/7 OWNED journals remained bound to one GUI PID/start identity and one ownership session; the watchdog segment preserves PREPARE, three WRITE_INTENT/COMMIT pairs, RESTORE_BEGIN and RELEASE. Independent Firmware proof reached two consecutive FF/FF reads.

The result remains FAIL_CLOSED. `Assert-AppInteractionAudit` called the two-argument `System.String.Contains` overload, which is unavailable under Windows PowerShell 5.1 / .NET Framework, so the harness threw before tray Exit could be requested and proven. The correction must use a Windows-PowerShell-compatible ordinal search and add a runtime compatibility regression test.

The return-30 parent proof also preserved intermediate EC observations `144/30` and `164/17` before two consecutive `30/30` reads. These anomalies must be reviewed rather than discarded before any fresh authorization.

The generation-2 authorization is consumed and all dedicated P16 physical gates are re-blocked. Permanent Manual, Automatic, Candidate V1 and default control remain closed. No target rerun is authorized.

## P16B attempt-3 software hardening staged after CI #1195

The immediate re-block HEAD
`1f8891e07706369c25adc6af34e32c6703c0a82d` passed full same-head CI
#1195 / run `37071194963` SUCCESS with every physical and permanent control
gate closed.

Two software-only corrections are now staged.

First, the final normal-app interaction audit no longer calls the
`String.Contains(string, StringComparison)` overload that is unavailable in
Windows PowerShell 5.1. Ordinal matching is routed through
`Test-P16OrdinalContains`, implemented with
`IndexOf(..., StringComparison.Ordinal)`. Its self-test is now executed in a
dedicated Windows PowerShell 5.1 CI step, so this is a runtime compatibility
check rather than only a parser/static check. The final audit also rejects any
preserved `FAILED CLOSED` or `BLOCKED` P13 interaction, even if later success
text exists.

Second, the attempt-3 samples `144/30` and `164/17` are handled as a
read-coherence problem rather than being silently treated as valid ownership
states. HP 8C40 ownership reads now use a bounded pair stabilizer: two
consecutive identical complete CPU/GPU setpoint snapshots are required within
six successful snapshots. The filter does **not** require CPU and GPU values to
be equal. A stable asymmetric pair such as `40/30` is returned unchanged and
therefore still triggers the existing ownership-loss/refusal logic. If no pair
stabilizes, the read fails closed as lost EC observability.

This stable reader is opted into the repo-built HP 8C40 production hardware
path and the HP 8C40 probe used by the independent parent CLI. The already
qualified installed M4 watchdog service is deliberately **not** replaced or
mutated by this software-only correction; its exact installed runtime remains
the previously qualified one. The legacy 88F8 narrow setpoint path is also
intentionally unchanged. The parent P16 proof is
also tightened: after the stable reader returns a coherent pair, any pair other
than the expected value is an immediate FAIL_CLOSED rather than something the
proof may wait through.

Deterministic C# coverage reproduces the exact attempt-3 sequence
`144/30 -> 30/30 -> 164/17 -> 30/30 -> 30/30`, verifies recovery to the stable
`30/30` pair, verifies that a stable asymmetric overwrite remains visible,
and verifies fail-closed behavior when no pair stabilizes within six snapshots.
Future P16 evidence packages now include the production backend, its self-test,
the HP 8C40 probe, the ACPI EC reader and the setpoint stabilizer source.

This commit performs no target hardware execution and leaves the dedicated P16
source/contract gates, permanent Manual, Automatic, Candidate V1 and default
control closed. Full same-head CI, independent review and a separate formal
closure are required before another authorization can even be considered.

## P16B attempt-3 hardening formally closed after CI #1196

Software-hardening HEAD
`34c6d8d3a7245d0697f5803c74e2194fe89297f5` passed full same-head CI
#1196 / run `37072759522` SUCCESS.

The validating workflow explicitly ran the new helper under Windows PowerShell
5.1.26100.33438, where the hardening helper self-test passed. The full P16
invariant and evidence-packaging self-test passed, the warnings-as-errors build
passed, and the HP 8C40 backend self-test passed the three new setpoint-coherence
cases: filtering the exact attempt-3 one-off `144/30` and `164/17` samples,
preserving a stable asymmetric/external overwrite, and failing closed when no
pair stabilizes within six snapshots.

Independent review confirms that the bounded coherence reader does not assume
CPU/GPU equality and therefore does not weaken ownership mismatch detection.
The normal fast path requires two complete identical pair snapshots; instability
is bounded to six successful snapshots and then fails closed. The repo-built HP
8C40 production/probe paths use the stable reader, while the already-qualified
installed M4 watchdog binary/runtime and the legacy 88F8 narrow setpoint path
remain unchanged.

This formal closure performs no hardware execution. The dedicated P16 physical
source/contract gates remain false; permanent Manual, Automatic, Candidate V1
and default control remain closed. A fresh one-shot P16B authorization may only
be created after this closure commit itself receives full same-head CI SUCCESS.

## P16B generation-3 one-shot reauthorization after attempt-3 closure CI #1197

Formal attempt-3 hardening closure HEAD
`04bd301b5693d8a471ffb556b3ba5dd4ebe122da` passed full same-head CI
#1197 / run `37073553766` SUCCESS.

Generation 3 opens only the dedicated P16 normal-application Manual
qualification source/contract gates. Permanent user Manual, Automatic,
Candidate V1 and default control remain closed.

The generation-1 authorization remains consumed by attempts 1 and 2. The
generation-2 authorization `258d39cd5ab968b55983447f409f2210c7c1fc65`
remains consumed by preserved attempt 3 and its ProgramData one-shot fence.
Neither prior authorization may be reused.

This generation-3 authorization commit performs no hardware execution. Target
execution is forbidden until this exact new authorization HEAD itself passes
full same-head CI. After that barrier, the existing ProgramData fence mechanism
must atomically consume that exact HEAD before the failsafe/GUI physical
boundary, allowing only one new target invocation.

## P16B attempt 4 — no-write suspend/resume interruption

Generation-3 authorization HEAD
`fdf08d94cddca82e3d1431bdf95e68c37aae24aa` passed same-head CI
#1198 / run `37074191886` SUCCESS and was consumed exactly once by the
ProgramData attempt fence.

Evidence archive `p16-normal-manual_2026-10-02_194949.zip` has SHA-256
`f95a529b3e0ccbe38ea1ef4a541e08a0030cd10031ea616deef0c710bbb85da6`.
The ZIP/sidecar identity was independently verified, and every included
evidence file whose basename is carried in the package manifest matches its
recorded SHA-256.

This attempt is FAIL_CLOSED but is specifically classified as a
`NO_WRITE_POWER_TRANSITION_INTERRUPTION`. The initial parent Firmware proof
was stable `FF/FF`, the normal GUI reached Healthy after three complete
snapshots, and the real Manual request logged
`action=HoldFirmware; authority=Firmware` with the explicit statement that no
new fan command was issued. No initial Apply 30/30 was observed, no OWNED
session was created, and the package contains no 30/40 setpoint or OWNED journal
proof.

Approximately eleven seconds after the Manual request, the application observed
`WM_POWERBROADCAST/PBT_APMSUSPEND`. On return it observed
`PBT_APMRESUMESUSPEND` plus the coalesced automatic-resume signal and recovered
Healthy after five complete snapshots. The subsequent real Firmware request
again logged `HoldFirmware` with no new fan command, followed by explicit GUI
shutdown. The harness therefore terminated with
`P16 GUI exited before initial Apply 30/30.`

The same M4 watchdog PID/start identity is preserved in the before,
normal-GUI-Healthy and cleanup service snapshots. The delayed failsafe remained
ARMED and did not take over.

The evidence establishes temporal proximity between selecting Manual and the
system power transition, but it does **not** establish that the Manual request
caused the suspend/hibernation event. Identifying the OS/firmware power
transition trigger requires separate Windows power-event evidence.

Generation 3 is consumed and all P16 physical gates are re-blocked. Before any
new authorization, the P16 qualification harness must gain an explicit
lifecycle-interruption fence so a suspend/resume after one-shot authorization
invalidates the session on return rather than allowing the qualification to
continue toward Apply. Permanent Manual, Automatic, Candidate V1 and default
control remain closed.


## P16 qualification session interruption hardening (software only)

After attempt 4, the ordinary app creates an `Hp8C40P16QualificationSession`
only for the exact-target P16 gate bridge. The first suspend or any resume
notification irreversibly invalidates it before existing lifecycle handling.
Healthy recovery, Firmware selection, repeated notifications and new telemetry
do not renew it. Manual mode/Apply are fenced at the UI and production adapter;
a linked token cancels queued/in-flight preparation before command dispatch.
Already-dispatched native work is handled by existing coordinator lifecycle
release and durable watchdog; cancellation is not proof of native cancellation.
Firmware release and explicit shutdown remain available. The parent scans its
whole app-log segment for `P16 QUALIFICATION INTERRUPTED:` on each observation,
including startup and final audit, and aborts rather than waiting for Apply.
The durable per-authorization-HEAD attempt fence is unchanged.

Implementation and regression CI must pass before a separate software closure.
All physical/permanent Manual/Automatic gates remain closed; a fresh hardware
authorization is still a separate future change.

The lifecycle session implementation is formally software-closed after HEAD
`86d6509bf4a71946067f0c34f53fdda976eec7c7`, full Windows CI #1203
(run 37088402551) SUCCESS. Seven deterministic session cases, PowerShell 5.1
helper runtime, SDK build/publish, packaging and regression checks passed.
This separate closure needs its own same-head CI. The original attempt-4
FAIL_CLOSED history remains unchanged; residual control tachometer migration
and separate fresh physical authorization/evidence still remain.
